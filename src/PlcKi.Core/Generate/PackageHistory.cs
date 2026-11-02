using System.Text.Json;
using System.Text.Json.Serialization;

namespace PlcKi.Core.Generate;

/// <summary>One saved version of a package (a folder v001, v002, ... next to generated.json).</summary>
public sealed class PackageVersion
{
    [JsonPropertyName("version")] public int Version { get; set; }
    [JsonPropertyName("folder")] public string Folder { get; set; } = "";
    [JsonPropertyName("time")] public DateTimeOffset Time { get; set; }
    /// <summary>"new" (first request), "change" (a later request), "fix" (after a TIA import/compile report).</summary>
    [JsonPropertyName("kind")] public string Kind { get; set; } = "new";
    [JsonPropertyName("request")] public string Request { get; set; } = "";
    [JsonPropertyName("model")] public string? Model { get; set; }
    /// <summary>Existing blocks, types and tags in the PLC program that this package may overwrite (the blocks it was asked to edit).</summary>
    [JsonPropertyName("replaces")] public List<string> Replaces { get; set; } = new();
    [JsonPropertyName("files")] public List<string> Files { get; set; } = new();
    [JsonPropertyName("added")] public List<string> Added { get; set; } = new();
    [JsonPropertyName("changed")] public List<string> Changed { get; set; } = new();
    [JsonPropertyName("removed")] public List<string> Removed { get; set; } = new();
    /// <summary>Problems the offline check still found. Empty when the package passed.</summary>
    [JsonPropertyName("problems")] public List<string> Problems { get; set; } = new();
}

/// <summary>
/// generated.json in a package folder: every version made from a request, a change or a fix. Each version is a
/// complete folder, so any one of them can be imported again (rollback) or compared with another.
/// tools/TiaImporter reads it to find the latest version and the blocks it may replace.
/// </summary>
public sealed class PackageHistory
{
    [JsonPropertyName("plc")] public string? Plc { get; set; }
    [JsonPropertyName("context")] public string? Context { get; set; }
    [JsonPropertyName("versions")] public List<PackageVersion> Versions { get; set; } = new();

    [JsonIgnore] public PackageVersion? Latest => Versions.Count == 0 ? null : Versions[^1];

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static PackageHistory Load(string dir)
    {
        var path = Path.Combine(dir, BlockPackage.ManifestFile);
        return File.Exists(path) ? JsonSerializer.Deserialize<PackageHistory>(File.ReadAllText(path)) ?? new() : new();
    }

    public void Save(string dir) => File.WriteAllText(Path.Combine(dir, BlockPackage.ManifestFile), JsonSerializer.Serialize(this, Options));

    /// <summary>Adds a version: saves the package into the next vNNN folder and records what changed against the one before.</summary>
    public PackageVersion Add(string dir, BlockPackage package, PackageVersion info, BlockPackage? previous)
    {
        info.Version = (Latest?.Version ?? 0) + 1;
        info.Folder = $"v{info.Version:000}";
        info.Files = package.Files.Select(f => f.Name).ToList();
        if (previous != null)
        {
            var before = previous.Files.ToDictionary(f => f.Name, f => f.Text, StringComparer.OrdinalIgnoreCase);
            foreach (var f in package.Files)
            {
                if (!before.TryGetValue(f.Name, out var old)) info.Added.Add(f.Name);
                else if (Normalize(old) != Normalize(f.Text)) info.Changed.Add(f.Name);
            }
            info.Removed = previous.Files.Select(f => f.Name).Where(n => package.Files.All(f => !f.Name.Equals(n, StringComparison.OrdinalIgnoreCase))).ToList();
        }
        else info.Added = info.Files.ToList();
        package.Save(Path.Combine(dir, info.Folder));
        Versions.Add(info);
        Save(dir);
        return info;
    }

    private static string Normalize(string s) => s.Replace("\r\n", "\n").TrimEnd();
}
