using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using PlcKi.Core.Model;
using PlcKi.Server.Live;

namespace PlcKi.Server.Link;

/// <summary>
/// "link": PLC-Ki Link, run on the laptop that reaches the PLC (and has TIA) when PLC-Ki itself runs on a server.
/// It only connects out to the server:
///   - reads the PLC read-only with the same driver as PLC-Ki and sends the values (once a second)
///   - with --tia, starts "TiaImporter serve --local" on the laptop and carries the server's TIA calls to it
/// The PLC password, if the CPU needs one, stays on the laptop (--plc-password or PLCKI_PLC_PASSWORD).
/// </summary>
public static class LinkCommand
{
    public const string Usage = """
Usage: PlcKi.Server link --server <http://server:5080> --key <key> [--tia <TiaImporter.exe>]

  --server <url>         the PLC-Ki server, as you open it in the browser
  --key <key>            the Link key from Setup, Connections (or set PLCKI_LINK_KEY)
  --tia <exe>            also carry TIA work: path of TiaImporter.exe on this laptop
  --allow-real <ips>     real PLC addresses the TIA helper may download to after confirmation (comma-separated)
  --plcsim-ip <ip>       PLCSIM address the TIA helper may download to for tests
  --plc <address>        read this PLC address instead of the one set on the server
  --plc-password <pw>    only if the CPU has access protection (or set PLCKI_PLC_PASSWORD)
  --tia-port <port>      port of the local TIA helper (default 5090)
  --snapshot <file>      send values from a test file instead of reading a PLC (for trying Link without a PLC)
""";

    public static async Task<int> RunAsync(string[] args, CancellationToken ct = default)
    {
        var o = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i + 1 < args.Length; i += 2)
        {
            if (!args[i].StartsWith("--")) { Console.Error.Write(Usage); return 2; }
            o[args[i][2..]] = args[i + 1];
        }
        var key = o.GetValueOrDefault("key") ?? Environment.GetEnvironmentVariable("PLCKI_LINK_KEY");
        if (!o.TryGetValue("server", out var server) || string.IsNullOrWhiteSpace(key)) { Console.Error.Write(Usage); return 2; }
        if (!Uri.TryCreate(server.TrimEnd('/') + "/", UriKind.Absolute, out var baseUri)) { Console.Error.WriteLine("--server is not an address: " + server); return 2; }

        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Cancel(); };
        var link = new Agent(baseUri, key!, o);
        Console.WriteLine($"PLC-Ki Link on {Environment.MachineName}: connecting to {baseUri}. Ctrl+C stops it.");
        try { await link.RunAsync(stop.Token); }
        catch (OperationCanceledException) { }
        finally { link.StopHelper(); }
        Console.WriteLine("PLC-Ki Link stopped.");
        return 0;
    }

    private sealed class Agent
    {
        private readonly HttpClient _http;
        private readonly Dictionary<string, string> _o;
        private readonly HttpClient _tia;
        private readonly string _tiaToken = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        private readonly int _tiaPort;
        private Process? _helper;
        private volatile Config? _config;
        private volatile bool _tiaOk;
        private volatile string? _tiaMessage;
        private string? _lastSay;

        public Agent(Uri server, string key, Dictionary<string, string> o)
        {
            _o = o;
            _http = new HttpClient { BaseAddress = server, Timeout = TimeSpan.FromMinutes(35) };
            _http.DefaultRequestHeaders.Add(LinkEndpoints.KeyHeader, key);
            _tiaPort = int.TryParse(o.GetValueOrDefault("tia-port"), out var p) ? p : 5090;
            _tia = new HttpClient { BaseAddress = new Uri($"http://localhost:{_tiaPort}/"), Timeout = TimeSpan.FromMinutes(30) };
            _tia.DefaultRequestHeaders.Add("X-PlcKi-Token", _tiaToken);
        }

        private void Say(string text)
        {
            if (text == _lastSay) return;
            _lastSay = text;
            Console.WriteLine($"{DateTime.Now:HH:mm:ss} {text}");
        }

        public async Task RunAsync(CancellationToken ct)
        {
            if (_o.ContainsKey("tia")) StartHelper();
            var tasks = new List<Task> { HelloLoop(ct), PlcLoop(ct) };
            if (_o.ContainsKey("tia")) tasks.Add(TiaLoop(ct));
            await Task.WhenAll(tasks);
        }

        // ---------- hello: who we are, what the server wants ----------

        private async Task HelloLoop(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    if (_o.ContainsKey("tia")) await CheckHelper(ct);
                    var hello = new LinkHello { Machine = Environment.MachineName, Version = typeof(LinkCommand).Assembly.GetName().Version?.ToString() ?? "", Tia = _tiaOk, TiaMessage = _tiaMessage };
                    using var resp = await _http.PostAsJsonAsync("link/hello", hello, ct);
                    if (resp.StatusCode == HttpStatusCode.Unauthorized) { Say("The server does not know this key. Make a new one under Setup, Connections."); }
                    else if (!resp.IsSuccessStatusCode) Say($"Server answered {(int)resp.StatusCode}.");
                    else
                    {
                        var c = await resp.Content.ReadFromJsonAsync<Config>(Json, ct);
                        if (_config == null) Say($"Connected to the server.{(c?.Plc != null ? $" Reading PLC {PlcAddress(c)}." : " The server does not read the PLC through Link.")}{(c?.Tia == true ? (_o.ContainsKey("tia") ? " Carrying TIA work." : " The server wants TIA through Link: start Link with --tia <TiaImporter.exe>.") : "")}");
                        _config = c;
                    }
                }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
                {
                    Say("Cannot reach the server: " + ex.Message + " Retrying.");
                    _config = null;
                }
                await Task.Delay(_config == null ? 5000 : 20000, ct);
            }
        }

        private string PlcAddress(Config? c) => _o.GetValueOrDefault("plc") ?? c?.Plc?.Address ?? "";

        // ---------- PLC: read-only, same driver as PLC-Ki ----------

        private Task PlcLoop(CancellationToken ct) => Task.Factory.StartNew(async () =>
        {
            using var logs = LoggerFactory.Create(b => b.AddSimpleConsole(x => x.SingleLine = true).SetMinimumLevel(LogLevel.Warning));
            IPlcSource? source = null;
            string? sourceKey = null;
            List<TagDef> tags = new();
            while (!ct.IsCancellationRequested)
            {
                var c = _config;
                if (c?.Plc == null || c.Plc.Tags.Count == 0)
                {
                    if (source != null) { source.Dispose(); source = null; sourceKey = null; }
                    await Task.Delay(2000, ct);
                    continue;
                }
                var key = PlcAddress(c) + "|" + c.Plc.Security + "|" + string.Join(',', c.Plc.Tags.Select(t => t.Symbol));
                var started = DateTime.UtcNow;
                var values = new LinkValues { Address = PlcAddress(c), Time = DateTimeOffset.Now };
                try
                {
                    if (source == null || key != sourceKey)
                    {
                        source?.Dispose();
                        var opt = new PlcOptions
                        {
                            Address = PlcAddress(c), Security = c.Plc.Security ?? "Auto", Username = c.Plc.Username ?? "",
                            Password = _o.GetValueOrDefault("plc-password") ?? Environment.GetEnvironmentVariable("PLCKI_PLC_PASSWORD") ?? "",
                            TimeoutMs = c.Plc.TimeoutMs > 0 ? c.Plc.TimeoutMs : 5000,
                        };
                        source = _o.GetValueOrDefault("snapshot") is { Length: > 0 } file
                            ? new SnapshotFileSource(Path.GetFullPath(file))
                            : new S7CommPlusSource(opt, logs.CreateLogger("plc"));
                        sourceKey = key;
                        tags = c.Plc.Tags;
                    }
                    if (!source.IsConnected)
                    {
                        var missing = source.Connect(tags);
                        Say($"PLC {PlcAddress(c)} connected ({tags.Count - missing.Count} of {tags.Count} tags found).");
                    }
                    var snap = source.Read();
                    values.Connected = true;
                    values.Time = snap.Time;
                    values.Values = snap.Values.ToDictionary(kv => kv.Key, kv => JsonSerializer.SerializeToElement(kv.Value));
                    values.Errors = snap.Errors;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    values.Connected = false;
                    values.Error = ex.Message;
                    Say($"PLC {PlcAddress(c)}: {ex.Message}");
                    try { source?.Disconnect(); } catch { /* reconnect next round */ }
                }
                try { using var r = await _http.PostAsJsonAsync("link/values", values, ct); }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested) { /* hello reports it */ }

                var poll = c.Plc.PollMs > 0 ? c.Plc.PollMs : 1000;
                var wait = (values.Connected ? poll : 5000) - (int)(DateTime.UtcNow - started).TotalMilliseconds;
                if (wait > 0) await Task.Delay(wait, ct);
            }
            source?.Dispose();
        }, ct, TaskCreationOptions.LongRunning, TaskScheduler.Default).Unwrap();

        // ---------- TIA: the helper on this laptop, calls carried from the server ----------

        private void StartHelper()
        {
            var exe = _o["tia"].Trim().Trim('"');
            if (!File.Exists(exe)) { _tiaMessage = $"TiaImporter.exe not found at {exe}."; Say(_tiaMessage); return; }
            var args = new List<string> { "serve", "--local", "--port", _tiaPort.ToString(), "--token", _tiaToken };
            if (_o.GetValueOrDefault("plcsim-ip") is { Length: > 0 } sim) { args.Add("--plcsim-ip"); args.Add(sim); }
            if (_o.GetValueOrDefault("allow-real") is { Length: > 0 } real) { args.Add("--allow-real"); args.Add(real); }
            // Its own window: a real-PLC download is confirmed there by typing the PLC's address
            var psi = new ProcessStartInfo(exe) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(exe)! };
            foreach (var a in args) psi.ArgumentList.Add(a);
            try { _helper = Process.Start(psi); Say($"Started the TIA helper (its own window, port {_tiaPort})."); }
            catch (Exception ex) { _tiaMessage = "Could not start TiaImporter: " + ex.Message; Say(_tiaMessage); }
        }

        public void StopHelper()
        {
            try { if (_helper is { HasExited: false }) _helper.Kill(); } catch { /* gone */ }
        }

        private async Task CheckHelper(CancellationToken ct)
        {
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromSeconds(4));
                using var r = await _tia.GetAsync("status", cts.Token);
                _tiaOk = r.IsSuccessStatusCode;
                _tiaMessage = _tiaOk ? null : $"The TIA helper answered {(int)r.StatusCode}.";
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
            {
                _tiaOk = false;
                _tiaMessage = _helper is { HasExited: true } ? "The TIA helper window was closed. Restart Link." : "The TIA helper does not answer yet (TIA may still be starting).";
            }
        }

        private async Task TiaLoop(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                if (_config == null) { await Task.Delay(2000, ct); continue; }
                try
                {
                    using var resp = await _http.GetAsync("link/next", ct);
                    if (resp.StatusCode != HttpStatusCode.OK) { if (!resp.IsSuccessStatusCode) await Task.Delay(3000, ct); continue; }
                    var req = await resp.Content.ReadFromJsonAsync<Request>(Json, ct);
                    if (req != null) _ = Task.Run(() => Carry(req, ct), ct);
                }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
                {
                    await Task.Delay(3000, ct);
                }
            }
        }

        private async Task Carry(Request req, CancellationToken ct)
        {
            try
            {
                using var msg = new HttpRequestMessage(new HttpMethod(req.Method), req.Path.TrimStart('/'));
                HttpResponseMessage? bodyResp = null;
                if (req.HasBody)
                {
                    bodyResp = await _http.GetAsync($"link/req/{req.Id}/body", HttpCompletionOption.ResponseHeadersRead, ct);
                    bodyResp.EnsureSuccessStatusCode();
                    msg.Content = new StreamContent(await bodyResp.Content.ReadAsStreamAsync(ct));
                    if (req.ContentType != null) msg.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(req.ContentType);
                }
                using (bodyResp)
                {
                    HttpResponseMessage answer;
                    try { answer = await _tia.SendAsync(msg, HttpCompletionOption.ResponseHeadersRead, ct); }
                    catch (HttpRequestException ex)
                    {
                        await Reply(req.Id, 502, new StringContent(JsonSerializer.Serialize(new { error = "The TIA helper on the laptop does not answer: " + ex.Message }), System.Text.Encoding.UTF8, "application/json"), ct);
                        return;
                    }
                    using (answer)
                    {
                        var content = new StreamContent(await answer.Content.ReadAsStreamAsync(ct));
                        if (answer.Content.Headers.ContentType != null) content.Headers.ContentType = answer.Content.Headers.ContentType;
                        await Reply(req.Id, (int)answer.StatusCode, content, ct);
                    }
                }
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                Say("A TIA call could not be carried: " + ex.Message);
            }
        }

        private async Task Reply(string id, int status, HttpContent content, CancellationToken ct)
        {
            using var msg = new HttpRequestMessage(HttpMethod.Post, $"link/req/{id}/response") { Content = content };
            msg.Headers.Add("X-Status", status.ToString());
            using var r = await _http.SendAsync(msg, ct);
        }
    }

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private sealed class Config
    {
        public PlcConfig? Plc { get; set; }
        public bool Tia { get; set; }
        public string? PlcsimIp { get; set; }
    }

    private sealed class PlcConfig
    {
        public string Address { get; set; } = "";
        public string? Security { get; set; }
        public string? Username { get; set; }
        public int PollMs { get; set; }
        public int TimeoutMs { get; set; }
        public List<TagDef> Tags { get; set; } = new();
    }

    private sealed class Request
    {
        public string Id { get; set; } = "";
        public string Method { get; set; } = "GET";
        public string Path { get; set; } = "/";
        public string? ContentType { get; set; }
        public bool HasBody { get; set; }
    }
}
