using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace PlcKi.Core.Knowledge;

/// <summary>
/// One Siemens system or diagnostic message (CPU, PROFINET, module, safety, drive, communication) with its
/// plain-English meaning, likely causes, what to check and how to fix it. General knowledge, not from the
/// machine's own program.
/// </summary>
public sealed class DiagnosticEntry
{
    public string Id { get; set; } = "";
    public string Category { get; set; } = "";
    public string Title { get; set; } = "";
    public List<string> AppliesTo { get; set; } = new();
    /// <summary>Exact identifiers: error OBs (OB80), drive fault numbers (F07900), raw values (32767).</summary>
    public List<string> Codes { get; set; } = new();
    /// <summary>Words and phrases seen in the diagnostic buffer, on displays or said by people.</summary>
    public List<string> Keywords { get; set; } = new();
    public string Meaning { get; set; } = "";
    public List<string> Causes { get; set; } = new();
    public List<string> Checks { get; set; } = new();
    public List<string> Fixes { get; set; } = new();
    public string? CpuReaction { get; set; }
    public string? Safety { get; set; }
}

public sealed class DiagnosticFile
{
    public string Name { get; set; } = "";
    public string Version { get; set; } = "";
    public string About { get; set; } = "";
    public List<DiagnosticEntry> Entries { get; set; } = new();
}

public sealed record DiagnosticMatch(DiagnosticEntry Entry, int Score);

/// <summary>
/// The Siemens diagnostics knowledge base (knowledge/siemens-diagnostics.json, built into this assembly) and a
/// forgiving lookup: message wording differs between firmware versions and languages, so it matches codes
/// exactly and text by keywords and shared words.
/// </summary>
public sealed class SiemensDiagnostics
{
    private const string ResourceName = "PlcKi.Core.Knowledge.siemens-diagnostics.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static readonly Lazy<SiemensDiagnostics> BuiltIn = new(() =>
    {
        using var s = typeof(SiemensDiagnostics).Assembly.GetManifestResourceStream(ResourceName)
                      ?? throw new InvalidOperationException($"Resource {ResourceName} is missing from PlcKi.Core.");
        return Load(s);
    });

    /// <summary>The knowledge base shipped with PLC-Ki.</summary>
    public static SiemensDiagnostics Default => BuiltIn.Value;

    private static readonly Lazy<SiemensDiagnostics> BuiltInDevices = new(() =>
    {
        const string name = "PlcKi.Core.Knowledge.device-faults.json";
        using var s = typeof(SiemensDiagnostics).Assembly.GetManifestResourceStream(name)
                      ?? throw new InvalidOperationException($"Resource {name} is missing from PlcKi.Core.");
        return Load(s);
    });

    /// <summary>Fault codes of other devices next to the PLC (FANUC robots, IO-Link events), same format and lookup.</summary>
    public static SiemensDiagnostics Devices => BuiltInDevices.Value;

    private static readonly HashSet<string> StopWords = new(StringComparer.Ordinal)
    {
        "the", "and", "for", "what", "why", "does", "mean", "means", "how", "fix", "this", "that", "with", "from",
        "is", "are", "was", "has", "have", "its", "it's", "can", "not", "plc", "cpu", "error", "fault", "alarm",
        "message", "shows", "showing", "says", "our", "there", "when", "after", "about", "into", "get", "got",
    };

    // F07900, A07910, F1910, OB80, 16#8000, 32767, SRVO-062, 0x8C10 ...
    private static readonly Regex CodeLike = new(@"\b(?:[FAC]\d{4,5}|OB\s?\d{1,3}|16#[0-9A-F]{2,4}|-?\d{5}|[A-Z]{3,5}-\d{3}|0x[0-9A-F]{4})\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public DiagnosticFile File { get; }

    private SiemensDiagnostics(DiagnosticFile file) => File = file;

    public IReadOnlyList<DiagnosticEntry> Entries => File.Entries;

    public static SiemensDiagnostics Load(Stream json) =>
        new(JsonSerializer.Deserialize<DiagnosticFile>(json, JsonOptions)
            ?? throw new InvalidDataException("Empty Siemens diagnostics file."));

    public static SiemensDiagnostics Load(string path)
    {
        using var s = System.IO.File.OpenRead(path);
        return Load(s);
    }

    public DiagnosticEntry? Get(string id) =>
        File.Entries.FirstOrDefault(e => string.Equals(e.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Best matches for a message text, fault code, OB number or description, highest score first.
    /// A code that matches exactly always wins; otherwise keyword phrases count most, then shared words.
    /// </summary>
    public List<DiagnosticMatch> Search(string query, string? category = null, int max = 3)
    {
        if (string.IsNullOrWhiteSpace(query)) return new();
        var q = Normalize(query);
        var codes = CodeLike.Matches(query).Select(m => NormalizeCode(m.Value)).ToHashSet(StringComparer.Ordinal);
        var words = Words(q);

        var results = new List<DiagnosticMatch>();
        foreach (var e in File.Entries)
        {
            if (category != null && !string.Equals(e.Category, category, StringComparison.OrdinalIgnoreCase)) continue;
            int score = 0;

            foreach (var c in e.Codes)
                if (codes.Contains(NormalizeCode(c)) || q == Normalize(c)) score += 100;

            foreach (var k in e.Keywords)
            {
                var kn = Normalize(k);
                if (kn.Length == 0) continue;
                if (ContainsPhrase(q, kn)) score += 6 * kn.Split(' ').Length + 4;
                else if (q.Length >= 4 && ContainsPhrase(kn, q)) score += 5;
            }

            var title = Normalize(e.Title);
            if (q.Length >= 4 && title.Contains(q)) score += 8;

            // Shared words with title and keywords (strong) and meaning (weak)
            var strong = Words(title + " " + string.Join(" ", e.Keywords.Select(Normalize)));
            var weak = Words(Normalize(e.Meaning));
            foreach (var w in words)
            {
                if (strong.Contains(w)) score += 3;
                else if (weak.Contains(w)) score += 1;
            }

            if (score >= 4) results.Add(new DiagnosticMatch(e, score));
        }

        return results.OrderByDescending(r => r.Score).ThenBy(r => r.Entry.Id, StringComparer.Ordinal)
            .Take(Math.Clamp(max, 1, 10)).ToList();
    }

    private static string Normalize(string s)
    {
        var lower = s.ToLowerInvariant().Replace('_', ' ');
        // Keep letters, digits, # + / - (16#7FFF, l+, i/o, e-stop); everything else becomes a space
        var chars = lower.Select(c => char.IsLetterOrDigit(c) || c is '#' or '+' or '/' or '-' ? c : ' ').ToArray();
        return Regex.Replace(new string(chars), @"\s+", " ").Trim();
    }

    // "F7900" and "F07900" are the same drive fault; "OB 80" is OB80
    private static string NormalizeCode(string c)
    {
        var s = c.ToUpperInvariant().Replace(" ", "");
        var m = Regex.Match(s, @"^([FAC])0*(\d+)$");
        return m.Success ? m.Groups[1].Value + m.Groups[2].Value : s;
    }

    private static bool ContainsPhrase(string text, string phrase) =>
        Regex.IsMatch(text, $@"(^|[\s]){Regex.Escape(phrase)}($|[\s])");

    private static HashSet<string> Words(string normalized) =>
        normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(w => w.Length >= 3 && !StopWords.Contains(w))
            .ToHashSet(StringComparer.Ordinal);
}
