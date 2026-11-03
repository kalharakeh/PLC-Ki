using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Xml.Linq;

namespace PlcKi.Core.Generate;

public sealed class ReportMessage
{
    [JsonPropertyName("state")] public string State { get; set; } = "";
    [JsonPropertyName("path")] public string Path { get; set; } = "";
    [JsonPropertyName("description")] public string Description { get; set; } = "";
}

public sealed class ReportFileError
{
    [JsonPropertyName("file")] public string File { get; set; } = "";
    [JsonPropertyName("error")] public string Error { get; set; } = "";
}

/// <summary>
/// import-report.json, written by "TiaImporter import-blocks" into the version folder it imported: what TIA refused
/// and what the compiler said. "PlcKi.Server generate --fix" sends it back to Claude.
/// </summary>
public sealed class ImportReport
{
    [JsonPropertyName("project")] public string Project { get; set; } = "";
    [JsonPropertyName("plc")] public string Plc { get; set; } = "";
    [JsonPropertyName("version")] public string Version { get; set; } = "";
    [JsonPropertyName("time")] public DateTimeOffset Time { get; set; }
    [JsonPropertyName("saved")] public bool Saved { get; set; }
    [JsonPropertyName("compileState")] public string CompileState { get; set; } = "";
    [JsonPropertyName("errorCount")] public int ErrorCount { get; set; }
    [JsonPropertyName("warningCount")] public int WarningCount { get; set; }
    [JsonPropertyName("fileErrors")] public List<ReportFileError> FileErrors { get; set; } = new();
    [JsonPropertyName("messages")] public List<ReportMessage> Messages { get; set; } = new();
    /// <summary>Download to PLCSIM: null when not tried, else whether it worked and what TIA said.</summary>
    [JsonPropertyName("download")] public string? Download { get; set; }
    /// <summary>What went wrong while the PLC ran the new code: CPU not in RUN, new alarms (from "develop").</summary>
    [JsonPropertyName("runtime")] public List<string> Runtime { get; set; } = new();

    public static ImportReport Load(string path) => JsonSerializer.Deserialize<ImportReport>(File.ReadAllText(path)) ?? new();

    public void Save(string path) => File.WriteAllText(path, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));

    /// <summary>Imported and compiled without errors (warnings allowed).</summary>
    public bool Compiled => FileErrors.Count == 0 && ErrorCount == 0 && CompileState != "" && !CompileState.Equals("Error", StringComparison.OrdinalIgnoreCase);

    public bool Clean => Compiled && Runtime.Count == 0;

    /// <summary>The report as problems for Claude: refused files, compile errors, then warnings.</summary>
    public List<string> AsProblems(bool withWarnings = true)
    {
        var list = FileErrors.Select(e => $"TIA refused {e.File}: {e.Error}").ToList();
        list.AddRange(Messages.Where(m => m.State.Equals("Error", StringComparison.OrdinalIgnoreCase)).Select(m => $"Compile error in {m.Path}: {m.Description}"));
        if (withWarnings)
            list.AddRange(Messages.Where(m => m.State.Equals("Warning", StringComparison.OrdinalIgnoreCase)).Select(m => $"Compile warning in {m.Path}: {m.Description}"));
        if (Download != null && !Download.StartsWith("OK", StringComparison.Ordinal)) list.Add("Download to PLCSIM failed: " + Download);
        list.AddRange(Runtime.Select(r => "While running on PLCSIM: " + r));
        return list;
    }
}

/// <summary>Reads the source of existing blocks from a TiaImporter export, so Claude can edit them.</summary>
public static class ExportSources
{
    /// <summary>
    /// For each name: SCL blocks as their SCL source, everything else (LAD, FBD, GRAPH, DBs, types) as SimaticML.
    /// Returns (block name, file extension, text). Names not found are left out.
    /// </summary>
    public static List<(string Name, string Extension, string Text)> Read(string exportPath, string? plcName, IEnumerable<string> names)
    {
        if (File.Exists(exportPath) && exportPath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "plcki-export-" + Guid.NewGuid().ToString("N")[..8]);
            ZipFile.ExtractToDirectory(exportPath, dir);
            try { return Read(dir, plcName, names); }
            finally { try { Directory.Delete(dir, true); } catch { /* temp folder */ } }
        }
        var manifest = Directory.GetFiles(exportPath, "manifest.json", SearchOption.AllDirectories).OrderBy(p => p.Length).First();
        var root = System.IO.Path.GetDirectoryName(manifest)!;
        string plcDir = root;
        using (var doc = JsonDocument.Parse(File.ReadAllText(manifest)))
        {
            foreach (var p in doc.RootElement.GetProperty("plcs").EnumerateArray())
            {
                var n = p.TryGetProperty("name", out var nn) ? nn.GetString() : null;
                if (plcName == null || string.Equals(n, plcName, StringComparison.OrdinalIgnoreCase))
                {
                    plcDir = System.IO.Path.Combine(root, p.TryGetProperty("folder", out var f) ? f.GetString() ?? n ?? "" : n ?? "");
                    break;
                }
            }
        }

        var result = new List<(string, string, string)>();
        var xmlFiles = new[] { "Blocks", "Types" }.Select(s => System.IO.Path.Combine(plcDir, s)).Where(Directory.Exists)
            .SelectMany(d => Directory.GetFiles(d, "*.xml", SearchOption.AllDirectories)).ToList();
        foreach (var name in names)
        {
            var scl = Directory.Exists(System.IO.Path.Combine(plcDir, "SclSource"))
                ? Directory.GetFiles(System.IO.Path.Combine(plcDir, "SclSource"), PlcKi.Core.Import.ProgramExport.FileSafe(name) + ".scl", SearchOption.AllDirectories).FirstOrDefault()
                : null;
            if (scl != null) { result.Add((name, ".scl", File.ReadAllText(scl).TrimStart('﻿'))); continue; }
            foreach (var x in xmlFiles)
            {
                if (!System.IO.Path.GetFileNameWithoutExtension(x).Equals(name, StringComparison.OrdinalIgnoreCase) && !XmlName(x).Equals(name, StringComparison.OrdinalIgnoreCase)) continue;
                result.Add((name, ".xml", File.ReadAllText(x).TrimStart('﻿')));
                break;
            }
        }
        return result;
    }

    private static string XmlName(string file)
    {
        try
        {
            return XDocument.Load(file).Root?.Elements().FirstOrDefault(e => e.Name.LocalName.StartsWith("SW."))?.Element("AttributeList")?.Element("Name")?.Value ?? "";
        }
        catch { return ""; }
    }
}
