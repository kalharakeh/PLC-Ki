using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using PlcKi.Server.Generate;
using PlcKi.Server.Live;

namespace PlcKi.Server.Jobs;

/// <summary>
/// The way to TIA Portal for the import page and the block generator. TIA work is always done by
/// "TiaImporter serve" next to TIA; this class knows where it is and, when TIA is on this PC, starts it.
/// </summary>
public sealed class TiaConnection
{
    // Kept across in-process restarts: the helper (and TIA inside it) keeps running when settings are applied
    private static Process? _helper;
    private static string? _helperKey;
    private static readonly string LocalToken = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));

    private readonly TiaOptions _opt;
    private readonly ILogger<TiaConnection> _log;

    public TiaConnection(PlcKiOptions opt, ILogger<TiaConnection> log)
    {
        _opt = opt.Tia;
        _log = log;
    }

    public string Mode => _opt.Mode;

    /// <summary>Where the helper answers (null when not set up).</summary>
    public string? Url => _opt.Mode switch
    {
        "local" => $"http://localhost:{_opt.Port}",
        "remote" => string.IsNullOrWhiteSpace(_opt.Url) ? null : _opt.Url.Trim().TrimEnd('/'),
        _ => null,
    };

    public string? Token => _opt.Mode == "local" && string.IsNullOrWhiteSpace(_opt.Token) ? LocalToken
        : string.IsNullOrWhiteSpace(_opt.Token) ? Environment.GetEnvironmentVariable("PLCKI_VM_TOKEN") : _opt.Token;

    public bool HelperRunning => _helper is { HasExited: false };

    /// <summary>Why TIA cannot be used, or null when it is set up (reachability is checked separately).</summary>
    public string? NotSetUp() => _opt.Mode switch
    {
        "link" => "TIA through PLC-Ki Link comes with the next update. For now choose \"This PC\" or \"Another PC\".",
        "local" when string.IsNullOrWhiteSpace(_opt.ImporterPath) => "Enter where TiaImporter.exe is on this PC.",
        "remote" when Url == null => "Enter the address of the TIA PC, for example http://192.168.10.3:5090.",
        "remote" when string.IsNullOrWhiteSpace(Token) => "Enter the token TiaImporter serve was started with.",
        _ => null,
    };

    public VmClient Client()
    {
        if (NotSetUp() is { } why) throw new JobException(why, 409);
        return new VmClient(Url!, Token!);
    }

    /// <summary>Environment for a job that talks to the helper (the token never goes on a command line).</summary>
    public Dictionary<string, string?> JobEnvironment() => new() { ["PLCKI_VM_TOKEN"] = Token };

    /// <summary>TIA on this PC: starts "TiaImporter serve --local" in its own window, unless it already runs with these settings.</summary>
    public string? EnsureLocalHelper()
    {
        if (_opt.Mode != "local") return null;
        var exe = _opt.ImporterPath.Trim().Trim('"');
        if (exe.Length == 0) return "Enter where TiaImporter.exe is on this PC.";
        if (!File.Exists(exe)) return $"TiaImporter.exe was not found at {exe}. Build it once (see tools/TiaImporter/README.md).";
        var args = new List<string> { "serve", "--local", "--port", _opt.Port.ToString(), "--token", Token! };
        if (!string.IsNullOrWhiteSpace(_opt.PlcsimIp)) { args.Add("--plcsim-ip"); args.Add(_opt.PlcsimIp.Trim()); }
        if (!string.IsNullOrWhiteSpace(_opt.AllowReal)) { args.Add("--allow-real"); args.Add(_opt.AllowReal.Trim()); }
        var key = exe + "|" + string.Join(' ', args);
        if (HelperRunning && _helperKey == key) return null;
        StopLocalHelper();
        try
        {
            // Its own visible window: a real PLC download is confirmed there, by typing the PLC's address
            var psi = new ProcessStartInfo(exe) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(exe)! };
            foreach (var a in args) psi.ArgumentList.Add(a);
            _helper = Process.Start(psi);
            _helperKey = key;
            _log.LogInformation("Started the TIA helper: {Exe} serve --local --port {Port}.", exe, _opt.Port);
            return null;
        }
        catch (Exception ex)
        {
            return "Could not start TiaImporter: " + ex.Message;
        }
    }

    public static void StopLocalHelper()
    {
        try { if (_helper is { HasExited: false }) _helper.Kill(); } catch { /* gone */ }
        _helper = null;
        _helperKey = null;
    }

    /// <summary>Checks that the helper answers, and what it allows.</summary>
    public async Task<object> StatusAsync(CancellationToken ct)
    {
        var baseInfo = new Dictionary<string, object?>
        {
            ["mode"] = _opt.Mode, ["url"] = Url, ["helperRunning"] = HelperRunning, ["project"] = _opt.Project,
        };
        if (NotSetUp() is { } why) { baseInfo["ok"] = false; baseInfo["message"] = why; return baseInfo; }
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(4));
            var text = await Client().StatusAsync(cts.Token);
            using var doc = JsonDocument.Parse(text);
            var r = doc.RootElement;
            baseInfo["ok"] = true;
            baseInfo["machine"] = r.TryGetProperty("machine", out var m) ? m.GetString() : null;
            baseInfo["projectsFolder"] = r.TryGetProperty("projects", out var p) ? p.GetString() : null;
            baseInfo["plcsimIp"] = r.TryGetProperty("plcsimIp", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString() : null;
            baseInfo["realAllowed"] = r.TryGetProperty("realAllowed", out var ra) && ra.ValueKind == JsonValueKind.True;
            baseInfo["message"] = $"TIA helper answers on {baseInfo["machine"]}.";
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or TaskCanceledException or JobException)
        {
            baseInfo["ok"] = false;
            baseInfo["message"] = ex is TaskCanceledException
                ? $"No answer from {Url} within 4 s. Is TiaImporter serve running there, and the port open?"
                : ex.Message.Contains("401") ? "The TIA helper refused the token. Enter the same token it was started with."
                : $"Cannot reach the TIA helper at {Url}: {ex.Message}";
        }
        return baseInfo;
    }
}
