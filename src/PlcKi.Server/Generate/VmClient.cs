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

    public VmClient(string baseUrl, string token, HttpMessageHandler? handler = null)
    {
        _http = handler != null ? new HttpClient(handler) : new HttpClient();
        _http.BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/");
        _http.Timeout = TimeSpan.FromMinutes(30);   // TIA start, import, compile and download take minutes
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
            ZipFile.CreateFromDirectory(packageDir, zip, CompressionLevel.Optimal, includeBaseDirectory: false);
            await using var f = File.OpenRead(zip);
            using var content = new StreamContent(f);
            content.Headers.ContentType = new MediaTypeHeaderValue("application/zip");
            using var resp = await _http.PutAsync("packages/" + Uri.EscapeDataString(name), content, ct);
            if (!resp.IsSuccessStatusCode) throw new InvalidOperationException($"Upload refused ({(int)resp.StatusCode}): {Error(await resp.Content.ReadAsStringAsync(ct))}");
        }
        finally { try { File.Delete(zip); } catch { /* temp file */ } }
    }

    public Task<VmResult> ImportAsync(string package, string project, string? plc, string version, CancellationToken ct) =>
        PostAsync("import", new { package, project, plc, version }, ct);

    /// <summary>PLCSIM download, or with <paramref name="real"/> the guarded real-PLC download (confirmed at the TIA PC).</summary>
    public Task<VmResult> DownloadAsync(string project, string? plc, CancellationToken ct, bool real = false) =>
        PostAsync("download", new { project, plc, real }, ct);

    private async Task<VmResult> PostAsync(string path, object body, CancellationToken ct)
    {
        using var content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        using var resp = await _http.PostAsync(path, content, ct);
        var text = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode) throw new InvalidOperationException($"TIA PC answered {(int)resp.StatusCode}: {Error(text)}");
        using var doc = JsonDocument.Parse(text);
        var root = doc.RootElement;
        return new VmResult(
            root.GetProperty("exitCode").GetInt32(),
            root.TryGetProperty("log", out var log) ? log.GetString() ?? "" : "",
            root.TryGetProperty("report", out var r) && r.ValueKind == JsonValueKind.Object ? r.GetRawText() : null);
    }

    private static string Error(string text)
    {
        try { using var d = JsonDocument.Parse(text); return d.RootElement.GetProperty("error").GetString() ?? text; }
        catch { return text.Length > 300 ? text[..300] : text; }
    }
}
