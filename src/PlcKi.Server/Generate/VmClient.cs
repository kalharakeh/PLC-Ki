using System.IO.Compression;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace PlcKi.Server.Generate;

/// <summary>Answer of the TIA PC to an import or download: exit code, its log, and the import report if any.</summary>
public sealed record VmResult(int ExitCode, string Log, string? ReportJson);

/// <summary>
/// Talks to "TiaImporter serve" on the TIA PC: uploads a package folder, imports it into the named project
/// (compile included) and downloads to PLCSIM. The TIA side does the work; this side never talks to TIA directly.
/// </summary>
public sealed class VmClient
{
    private readonly HttpClient _http;

    /// <summary>How often to ask the TIA PC how a job is going.</summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(3);

    /// <summary>How long the TIA PC may be unreachable during a job before giving up (a download can reset its adapter).</summary>
    public TimeSpan Patience { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>Gets each new piece of the TIA PC's log while a job runs.</summary>
    public Action<string>? Progress { get; set; }

    public VmClient(string baseUrl, string token, HttpMessageHandler? handler = null)
    {
        _http = handler != null ? new HttpClient(handler) : new HttpClient();
        _http.BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/");
        _http.Timeout = TimeSpan.FromMinutes(10);   // per request (uploads of big archives); the long work runs as a job that is polled
        _http.DefaultRequestHeaders.Add("X-PlcKi-Token", token);
    }

    public async Task<string> StatusAsync(CancellationToken ct)
    {
        using var resp = await _http.GetAsync("status", ct);
        var text = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode) throw new InvalidOperationException($"TIA PC answered {(int)resp.StatusCode}: {Error(text)}");
        return text;
    }

    /// <summary>Zips the package folder (generated.json and every version) and sends it under its folder name.</summary>
    public async Task UploadAsync(string packageDir, string name, CancellationToken ct)
    {
        var zip = Path.Combine(Path.GetTempPath(), $"plcki-{name}-{Guid.NewGuid():N}.zip");
        try
        {
            // Without the import reports: they are the TIA PC's own answers, and a copy sent back with this PC's clock
            // could look newer than the report the next import writes.
            using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
                foreach (var file in Directory.EnumerateFiles(packageDir, "*", SearchOption.AllDirectories))
                {
                    if (Path.GetFileName(file).Equals("import-report.json", StringComparison.OrdinalIgnoreCase)) continue;
                    archive.CreateEntryFromFile(file, Path.GetRelativePath(packageDir, file).Replace('\\', '/'), CompressionLevel.Optimal);
                }
            await using var f = File.OpenRead(zip);
            using var content = new StreamContent(f);
            content.Headers.ContentType = new MediaTypeHeaderValue("application/zip");
            using var resp = await _http.PutAsync("packages/" + Uri.EscapeDataString(name), content, ct);
            if (!resp.IsSuccessStatusCode) throw new InvalidOperationException($"Upload refused ({(int)resp.StatusCode}): {Error(await resp.Content.ReadAsStringAsync(ct))}");
        }
        finally { try { File.Delete(zip); } catch { /* temp file */ } }
    }

    /// <summary>The .ap20 projects the TIA PC may open (under its projects folder).</summary>
    public async Task<string> ProjectsAsync(CancellationToken ct)
    {
        using var resp = await _http.GetAsync("projects", ct);
        var text = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode) throw new InvalidOperationException($"TIA PC answered {(int)resp.StatusCode}: {Error(text)}");
        return text;
    }

    /// <summary>Sends a .zap20 archive to the TIA PC for "export".</summary>
    public async Task UploadArchiveAsync(string file, string name, CancellationToken ct)
    {
        await using var f = File.OpenRead(file);
        using var content = new StreamContent(f);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        using var resp = await _http.PutAsync("archives/" + Uri.EscapeDataString(name), content, ct);
        if (!resp.IsSuccessStatusCode) throw new InvalidOperationException($"Upload refused ({(int)resp.StatusCode}): {Error(await resp.Content.ReadAsStringAsync(ct))}");
    }

    /// <summary>Exports the uploaded archive's PLC programs on the TIA PC (TIA opens it in the background).</summary>
    public Task<VmResult> ExportAsync(string archive, CancellationToken ct, bool hardwareOnly = false) => PostAsync("export", new { archive, hardwareOnly }, ct);

    /// <summary>RUN/STOP (and LEDs) of the PLCSIM CPU the TIA helper downloads to, as JSON.</summary>
    public async Task<string> PlcsimStateAsync(CancellationToken ct)
    {
        using var resp = await _http.GetAsync("cpu", ct);
        var text = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode) throw new InvalidOperationException($"TIA PC answered {(int)resp.StatusCode}: {Error(text)}");
        return text;
    }

    /// <summary>
    /// Asks the TIA helper to put its PLCSIM CPU in RUN or STOP. The helper refuses any address but its own PLCSIM
    /// address, and its PLCSIM API cannot reach a real PLC. Returns the new state as JSON.
    /// </summary>
    public async Task<string> SwitchPlcsimAsync(string mode, string ip, CancellationToken ct)
    {
        using var content = new StringContent(JsonSerializer.Serialize(new { mode, ip }), Encoding.UTF8, "application/json");
        using var resp = await _http.PostAsync("cpu", content, ct);
        var text = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode) throw new InvalidOperationException(Error(text));
        return text;
    }

    /// <summary>Exports a TIA project (.ap20 on the TIA PC) as it is now, under <paramref name="name"/>.</summary>
    public Task<VmResult> ExportProjectAsync(string project, string name, CancellationToken ct) => PostAsync("export", new { project, name }, ct);

    /// <summary>Fetches a finished export as a zip file.</summary>
    public async Task DownloadExportAsync(string name, string toFile, CancellationToken ct)
    {
        using var resp = await _http.GetAsync("exports/" + Uri.EscapeDataString(name), HttpCompletionOption.ResponseHeadersRead, ct);
        if (!resp.IsSuccessStatusCode) throw new InvalidOperationException($"TIA PC answered {(int)resp.StatusCode}: {Error(await resp.Content.ReadAsStringAsync(ct))}");
        await using var f = File.Create(toFile);
        await resp.Content.CopyToAsync(f, ct);
    }

    public Task<VmResult> ImportAsync(string package, string project, string? plc, string version, CancellationToken ct) =>
        PostAsync("import", new { package, project, plc, version }, ct);

    /// <summary>PLCSIM download, or with <paramref name="real"/> the guarded real-PLC download (confirmed at the TIA PC).</summary>
    public Task<VmResult> DownloadAsync(string project, string? plc, CancellationToken ct, bool real = false) =>
        PostAsync("download", new { project, plc, real }, ct);

    /// <summary>Starts the job on the TIA PC, then asks every few seconds until it is done. Short network drops are retried.</summary>
    private async Task<VmResult> PostAsync(string path, object body, CancellationToken ct)
    {
        string job;
        using (var content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"))
        using (var resp = await _http.PostAsync(path, content, ct))
        {
            var text = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode) throw new InvalidOperationException($"TIA PC answered {(int)resp.StatusCode}: {Error(text)}");
            using var doc = JsonDocument.Parse(text);
            job = doc.RootElement.GetProperty("job").GetString()!;
        }

        var shown = 0;
        var lastContact = DateTime.UtcNow;
        while (true)
        {
            await Task.Delay(PollInterval, ct);
            string text;
            try
            {
                using var each = CancellationTokenSource.CreateLinkedTokenSource(ct);
                each.CancelAfter(TimeSpan.FromMinutes(1));
                using var resp = await _http.GetAsync("jobs/" + Uri.EscapeDataString(job), each.Token);
                text = await resp.Content.ReadAsStringAsync(ct);
                if (!resp.IsSuccessStatusCode) throw new InvalidOperationException($"TIA PC answered {(int)resp.StatusCode}: {Error(text)}");
            }
            catch (Exception ex) when ((ex is HttpRequestException || ex is TaskCanceledException) && !ct.IsCancellationRequested)
            {
                if (DateTime.UtcNow - lastContact > Patience)
                    throw new InvalidOperationException($"Lost the TIA PC for {Patience.TotalMinutes:0} min during the job: {ex.Message}");
                continue;   // the job goes on over there; ask again
            }
            lastContact = DateTime.UtcNow;
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            var log = root.TryGetProperty("log", out var l) ? l.GetString() ?? "" : "";
            var done = root.TryGetProperty("done", out var d) && d.ValueKind == JsonValueKind.True;
            var upto = done ? log.Length : log.LastIndexOf('\n') + 1;   // whole lines only, until the end
            if (upto > shown) { Progress?.Invoke(log[shown..upto]); shown = upto; }
            if (!done) continue;
            return new VmResult(
                root.GetProperty("exitCode").GetInt32(),
                log,
                root.TryGetProperty("report", out var r) && r.ValueKind == JsonValueKind.Object ? r.GetRawText() : null);
        }
    }

    private static string Error(string text)
    {
        try { using var d = JsonDocument.Parse(text); return d.RootElement.GetProperty("error").GetString() ?? text; }
        catch { return text.Length > 300 ? text[..300] : text; }
    }
}
