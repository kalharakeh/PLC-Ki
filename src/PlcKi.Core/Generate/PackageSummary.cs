using System.Text.Json;
using System.Text.RegularExpressions;

namespace PlcKi.Core.Generate;

/// <summary>One block or type of a version, compared with the version before.</summary>
/// <param name="Change">"new", "changed", "same" or "removed".</param>
/// <param name="Networks">LAD/FBD/GRAPH (XML) blocks: networks now, and before. Null for SCL, DB and UDT files.</param>
public sealed record BlockChange(string Name, string Kind, string? Language, string File, string Change,
    int Lines, int LinesAdded, int LinesRemoved, int? Networks, int? NetworksBefore, string? InstanceOf = null);

/// <summary>One PLC tag of a version, compared with the version before. Change is "added", "removed" or "changed".</summary>
public sealed record TagChange(string Table, string Name, string Change, string? Address, string? Detail);

/// <summary>One sentence of a <see cref="PackageSummary"/>.</summary>
public sealed record SummaryLine(string Kind, string Text);

/// <summary>
/// What a version made or changed, in plain words, for the page and the job log: which blocks are new, changed
/// (lines or networks), unchanged or gone, and which tags were added, removed or changed.
/// </summary>
public sealed class PackageSummary
{
    public List<BlockChange> Blocks { get; } = new();
    public List<TagChange> Tags { get; } = new();
    /// <summary>The same as plain sentences, most important first. Kind is "add", "change", "remove" or "same".</summary>
    public List<SummaryLine> Lines { get; } = new();
    [System.Text.Json.Serialization.JsonIgnore] public IEnumerable<string> Text => Lines.Select(l => l.Text);

    private static readonly Regex Network = new(@"<SW\.Blocks\.CompileUnit\b", RegexOptions.IgnoreCase);

    /// <summary>Compares two version folders. <paramref name="previousDir"/> is null for the first version.</summary>
    public static PackageSummary Compare(string? previousDir, string dir) =>
        Compare(previousDir != null && Directory.Exists(previousDir) ? BlockPackage.Load(previousDir) : null, BlockPackage.Load(dir));

    public static PackageSummary Compare(BlockPackage? previous, BlockPackage current)
    {
        var s = new PackageSummary();
        var before = previous != null ? PackageCheck.Blocks(previous) : new List<PackageBlock>();
        var oldText = previous?.Files.ToDictionary(f => f.Name, f => f.Text, StringComparer.OrdinalIgnoreCase) ?? new(StringComparer.OrdinalIgnoreCase);
        var newText = current.Files.ToDictionary(f => f.Name, f => f.Text, StringComparer.OrdinalIgnoreCase);

        foreach (var b in PackageCheck.Blocks(current))
        {
            var text = newText[b.File];
            var lines = SplitLines(text);
            var xml = b.File.EndsWith(".xml", StringComparison.OrdinalIgnoreCase);
            int? nets = xml ? Network.Matches(text).Count : null;
            var old = before.FirstOrDefault(x => x.Name.Equals(b.Name, StringComparison.OrdinalIgnoreCase) && x.Kind == b.Kind);
            if (old == null)
            {
                s.Blocks.Add(new BlockChange(b.Name, b.Kind, b.Language, b.File, "new", lines.Count, lines.Count, 0, nets, null, b.InstanceOf));
                continue;
            }
            var was = oldText[old.File];
            var (added, removed) = Diff(SplitLines(was), lines);
            s.Blocks.Add(new BlockChange(b.Name, b.Kind, b.Language, b.File, added + removed == 0 ? "same" : "changed",
                lines.Count, added, removed, nets, xml ? Network.Matches(was).Count : null, b.InstanceOf));
        }
        foreach (var b in before.Where(o => !s.Blocks.Any(n => n.Name.Equals(o.Name, StringComparison.OrdinalIgnoreCase) && n.Kind == o.Kind)))
            s.Blocks.Add(new BlockChange(b.Name, b.Kind, b.Language, b.File, "removed", 0, 0, 0, null, null, b.InstanceOf));

        var oldTags = TagsOf(previous);
        var newTags = TagsOf(current);
        foreach (var (key, t) in newTags)
        {
            if (!oldTags.TryGetValue(key, out var o)) { s.Tags.Add(new TagChange(t.Table, t.Tag.Name, "added", t.Tag.Address, t.Tag.DataType)); continue; }
            var diffs = new List<string>();
            if (!Same(o.Tag.Address, t.Tag.Address)) diffs.Add($"address {o.Tag.Address} to {t.Tag.Address}");
            if (!Same(o.Tag.DataType, t.Tag.DataType)) diffs.Add($"type {o.Tag.DataType} to {t.Tag.DataType}");
            if (!Same(o.Tag.Comment, t.Tag.Comment)) diffs.Add("comment");
            if (diffs.Count > 0) s.Tags.Add(new TagChange(t.Table, t.Tag.Name, "changed", t.Tag.Address, string.Join(", ", diffs)));
        }
        foreach (var (key, o) in oldTags.Where(x => !newTags.ContainsKey(x.Key)))
            s.Tags.Add(new TagChange(o.Table, o.Tag.Name, "removed", o.Tag.Address, null));

        s.Write(previous == null);
        return s;
    }

    private void Write(bool first)
    {
        foreach (var b in Blocks.Where(b => b.Change == "new"))
            Add("add", $"{(first ? "Wrote" : "Added")} {What(b)} {b.Name}{Lang(b)}: " +
                     (b.Networks is int n ? $"{n} network{(n == 1 ? "" : "s")}." : $"{b.Lines} lines."));
        foreach (var b in Blocks.Where(b => b.Change == "changed"))
        {
            var detail = b.Networks is int n && b.NetworksBefore is int was && n != was
                ? $"now {n} network{(n == 1 ? "" : "s")} (was {was})"
                : b.Networks != null ? "the logic inside its networks changed"
                : LineChange(b.LinesAdded, b.LinesRemoved);
            Add("change", $"Changed {What(b)} {b.Name}{Lang(b)}: {detail}.");
        }
        foreach (var b in Blocks.Where(b => b.Change == "removed"))
            Add("remove", $"Removed {What(b)} {b.Name}. It is no longer in this version.");
        foreach (var g in Tags.GroupBy(t => t.Change).OrderBy(g => Array.IndexOf(new[] { "added", "changed", "removed" }, g.Key)))
        {
            var items = g.Select(t => t.Change == "changed" ? $"{t.Name} ({t.Detail})" : t.Address is { Length: > 0 } a ? $"{t.Name} ({a})" : t.Name).ToList();
            var shown = items.Count > 8 ? string.Join(", ", items.Take(8)) + $" and {items.Count - 8} more" : string.Join(", ", items);
            var tags = $"{items.Count} tag{(items.Count == 1 ? "" : "s")}";
            Add(g.Key switch { "added" => "add", "removed" => "remove", _ => "change" }, g.Key switch
            {
                "added" => first ? $"{items.Count} new tag{(items.Count == 1 ? "" : "s")}: {shown}." : $"Added {tags}: {shown}.",
                "removed" => $"Removed {tags}: {shown}.",
                _ => $"Changed {tags}: {shown}.",
            });
        }
        var same = Blocks.Where(b => b.Change == "same").Select(b => b.Name).ToList();
        if (!first && Lines.Count == 0)
            Add("same", "No changes to the code: every block and tag is the same as in the version before." +
                        (same.Count > 0 ? $" ({string.Join(", ", same)})" : ""));
        else if (same.Count > 0) Add("same", $"Unchanged: {string.Join(", ", same)}.");
        if (Lines.Count == 0) Add("same", "No blocks or tags in this version.");
    }

    private void Add(string kind, string text) => Lines.Add(new SummaryLine(kind, text));

    private static string What(BlockChange b) => b.Kind switch
    {
        "FB" => "function block", "FC" => "function", "OB" => "organization block", "UDT" => "data type",
        "DB" => b.InstanceOf != null ? "instance data block" : "data block",
        _ => b.Kind,
    };
    private static string LineChange(int added, int removed)
    {
        static string L(int n) => $"{n} line{(n == 1 ? "" : "s")}";
        return added > 0 && removed > 0 ? $"{L(added)} added, {removed} removed" : added > 0 ? $"{L(added)} added" : $"{L(removed)} removed";
    }
    private static string Lang(BlockChange b) => b.InstanceOf is { Length: > 0 } fb ? $" (for {fb})" : b.Language is { Length: > 0 } l ? $" ({l})" : "";
    private static bool Same(string? a, string? b) => string.Equals((a ?? "").Trim(), (b ?? "").Trim(), StringComparison.Ordinal);

    private static List<string> SplitLines(string text) =>
        text.Replace("\r\n", "\n").TrimEnd().Split('\n').Select(l => l.TrimEnd()).ToList();

    /// <summary>Lines added and removed between two texts (longest common subsequence; a rough count for very long files).</summary>
    public static (int Added, int Removed) Diff(List<string> a, List<string> b)
    {
        // Same start and end lines do not count
        int s = 0;
        while (s < a.Count && s < b.Count && a[s] == b[s]) s++;
        int ea = a.Count, eb = b.Count;
        while (ea > s && eb > s && a[ea - 1] == b[eb - 1]) { ea--; eb--; }
        int n = ea - s, m = eb - s;
        if (n == 0 || m == 0) return (m, n);
        if ((long)n * m > 4_000_000)
        {
            // Too long for the table: count lines that are only on one side
            var left = a.Skip(s).Take(n).GroupBy(x => x).ToDictionary(x => x.Key, x => x.Count());
            int common = 0;
            foreach (var l in b.Skip(s).Take(m))
                if (left.TryGetValue(l, out var c) && c > 0) { left[l] = c - 1; common++; }
            return (m - common, n - common);
        }
        var prev = new int[m + 1];
        var cur = new int[m + 1];
        for (int i = 1; i <= n; i++)
        {
            for (int j = 1; j <= m; j++)
                cur[j] = a[s + i - 1] == b[s + j - 1] ? prev[j - 1] + 1 : Math.Max(prev[j], cur[j - 1]);
            (prev, cur) = (cur, prev);
        }
        var lcs = prev[m];
        return (m - lcs, n - lcs);
    }

    private static Dictionary<string, (string Table, TagEntry Tag)> TagsOf(BlockPackage? p)
    {
        var all = new Dictionary<string, (string, TagEntry)>(StringComparer.OrdinalIgnoreCase);
        if (p == null) return all;
        foreach (var f in p.Files.Where(f => f.Extension == ".json"))
        {
            TagTables? t;
            try { t = JsonSerializer.Deserialize<TagTables>(f.Text); } catch (JsonException) { continue; }
            foreach (var table in t?.Tables ?? new())
                foreach (var tag in table.Tags)
                    all[table.Name + "/" + tag.Name] = (table.Name, tag);
        }
        return all;
    }
}
