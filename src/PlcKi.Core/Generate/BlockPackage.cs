using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace PlcKi.Core.Generate;

/// <summary>One file of a generated block package, e.g. "10_FB_Valve.scl".</summary>
public sealed record PackageFile(string Name, string Text)
{
    public string Extension => Path.GetExtension(Name).ToLowerInvariant();
}

/// <summary>Tag tables of a package (the "*.json" file): created in TIA through Openness, not as XML.</summary>
public sealed class TagTables
{
    [JsonPropertyName("tables")] public List<TagTable> Tables { get; set; } = new();
}

public sealed class TagTable
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("tags")] public List<TagEntry> Tags { get; set; } = new();
}

public sealed class TagEntry
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("dataType")] public string DataType { get; set; } = "";
    [JsonPropertyName("address")] public string Address { get; set; } = "";
    [JsonPropertyName("comment")] public string? Comment { get; set; }
}

/// <summary>
/// What the generator hands to TIA: PLC data types (.udt), data blocks (.db), SCL blocks (.scl), LAD/FBD/GRAPH blocks
/// as SimaticML (.xml) and tag tables (.json). The two-digit prefix of each file name is the import order.
/// Claude answers with &lt;file name="..."&gt; sections and one &lt;review&gt; section, which <see cref="Parse"/> reads.
/// </summary>
public sealed class BlockPackage
{
    public const string ManifestFile = "generated.json";
    public const string ReviewFile = "REVIEW.md";
    public const string ReportFile = "import-report.json";

    public static readonly string[] Extensions = { ".udt", ".db", ".scl", ".xml", ".json" };
    public static readonly Regex FileNamePattern = new(@"^\d{2}_[A-Za-z0-9_]+\.(udt|db|scl|xml|json)$", RegexOptions.IgnoreCase);

    public List<PackageFile> Files { get; } = new();
    public string Review { get; set; } = "";

    private static readonly Regex FileSection = new(@"<file\s+name\s*=\s*""([^""]+)""\s*>\r?\n?(.*?)</file>", RegexOptions.Singleline | RegexOptions.IgnoreCase);
    private static readonly Regex ReviewSection = new(@"<review>\s*(.*?)\s*</review>", RegexOptions.Singleline | RegexOptions.IgnoreCase);

    /// <summary>Reads Claude's answer. Code fences inside a file section are removed.</summary>
    public static BlockPackage Parse(string answer)
    {
        var p = new BlockPackage();
        foreach (Match m in FileSection.Matches(answer))
            p.Files.Add(new PackageFile(m.Groups[1].Value.Trim(), StripFence(m.Groups[2].Value)));
        var r = ReviewSection.Match(answer);
        if (r.Success) p.Review = r.Groups[1].Value.Trim();
        p.Files.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
        return p;
    }

    /// <summary>Reads a package back from its folder (for a correction round). Only package files are read.</summary>
    public static BlockPackage Load(string dir)
    {
        var p = new BlockPackage();
        foreach (var f in Directory.GetFiles(dir).Select(Path.GetFileName).OfType<string>().Where(IsPackageFile).OrderBy(n => n, StringComparer.Ordinal))
            p.Files.Add(new PackageFile(f, File.ReadAllText(Path.Combine(dir, f))));
        var review = Path.Combine(dir, ReviewFile);
        if (File.Exists(review)) p.Review = File.ReadAllText(review);
        return p;
    }

    public static bool IsPackageFile(string name) => FileNamePattern.IsMatch(name) && name != ManifestFile && name != ReportFile;

    /// <summary>Writes the package. Package files from an earlier version that are no longer in it are deleted.</summary>
    /// <summary>A file TIA imports as an external source (SCL, DB, UDT, STL text).</summary>
    public static bool IsSource(string name) =>
        Path.GetExtension(name).ToLowerInvariant() is ".scl" or ".db" or ".udt" or ".awl";

    public void Save(string dir)
    {
        Directory.CreateDirectory(dir);
        var keep = new HashSet<string>(Files.Select(f => f.Name), StringComparer.OrdinalIgnoreCase);
        foreach (var old in Directory.GetFiles(dir).Select(Path.GetFileName).OfType<string>().Where(IsPackageFile))
            if (!keep.Contains(old)) File.Delete(Path.Combine(dir, old));
        foreach (var f in Files)
        {
            if (!FileNamePattern.IsMatch(f.Name)) throw new InvalidOperationException($"Not a package file name: {f.Name}");
            // TIA reads an external source without a byte order mark as ANSI, which turns a tag like "DO_BöackPBLED" into
            // "DO_BÃ¶ackPBLED" ("not defined"). With the mark it reads UTF-8.
            File.WriteAllText(Path.Combine(dir, f.Name), f.Text, new UTF8Encoding(IsSource(f.Name)));
        }
        File.WriteAllText(Path.Combine(dir, ReviewFile), Review.Length > 0 ? Review + "\n" : "", new UTF8Encoding(false));
    }

    /// <summary>The package as Claude writes it, for a correction round.</summary>
    public string ToAnswerText()
    {
        var sb = new StringBuilder();
        foreach (var f in Files) sb.Append("<file name=\"").Append(f.Name).Append("\">\n").Append(f.Text.TrimEnd()).Append("\n</file>\n\n");
        sb.Append("<review>\n").Append(Review).Append("\n</review>\n");
        return sb.ToString();
    }

    private static string StripFence(string text)
    {
        var t = text.Trim('\r', '\n');
        var lines = t.Split('\n').ToList();
        if (lines.Count > 1 && lines[0].TrimStart().StartsWith("```") && lines[^1].Trim() == "```")
            t = string.Join('\n', lines.Skip(1).Take(lines.Count - 2));
        return t.TrimEnd() + "\n";
    }
}
