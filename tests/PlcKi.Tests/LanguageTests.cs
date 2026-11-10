using System.Text.Json;
using System.Text.RegularExpressions;
using PlcKi.Plant;
using Xunit;

/// <summary>
/// EN/DE (review 49): every text the pages write with PK.t("…") has a German entry in wwwroot/js/i18n-de.js, so new text
/// cannot silently stay English; the server's own messages have a German entry or rule too.
/// </summary>
public class LanguageTests
{
    private static string Root()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        return dir.Parent!.Parent!.Parent!.Parent!.Parent!.FullName;     // tests/PlcKi.Tests/bin/<Config>/net8.0 -> repo
    }

    private static string Www() => Path.Combine(Root(), "src", "PlcKi.Server", "wwwroot");
    private static string Read(string path) => File.ReadAllText(path).Replace("\r\n", "\n");

    private static readonly JsonDocumentOptions Loose = new() { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip };

    /// <summary>The German dictionary: the object inside I18N.add({ … }).</summary>
    private static Dictionary<string, string> Dictionary()
    {
        var js = Read(Path.Combine(Www(), "js", "i18n-de.js"));
        var start = js.IndexOf("\nI18N.add({\n", StringComparison.Ordinal) + 1;      // not the example in the header comment
        var end = js.IndexOf("\n});", start, StringComparison.Ordinal);
        Assert.True(start > 0 && end > start, "i18n-de.js must hold I18N.add({ … }); with the opening and the closing on their own lines.");
        var body = js.Substring(start + "I18N.add(".Length, end - start - "I18N.add(".Length + 2);
        using var doc = JsonDocument.Parse(body, Loose);
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var p in doc.RootElement.EnumerateObject()) map[p.Name] = p.Value.GetString() ?? "";
        return map;
    }

    /// <summary>The rules for server messages: [/regex/, "German"], one per line, in the order they are tried.</summary>
    private static List<Regex> Rules()
    {
        var js = Read(Path.Combine(Www(), "js", "i18n-de.js"));
        var list = new List<Regex>();
        foreach (Match m in Regex.Matches(js, @"^\s*\[/(?<re>.+)/(?<f>[a-z]*),\s*""(?<to>(?:[^""\\]|\\.)*)""\],?\s*$", RegexOptions.Multiline))
            list.Add(new Regex(m.Groups["re"].Value, m.Groups["f"].Value.Contains('i') ? RegexOptions.IgnoreCase : RegexOptions.None));
        return list;
    }

    private static string Unescape(string literal)
    {
        var quote = literal[0];
        var inner = literal.Substring(1, literal.Length - 2);
        if (quote == '"') return JsonSerializer.Deserialize<string>(literal)!;
        var sb = new System.Text.StringBuilder();
        for (var i = 0; i < inner.Length; i++)
        {
            if (inner[i] != '\\' || i == inner.Length - 1) { sb.Append(inner[i]); continue; }
            var c = inner[++i];
            sb.Append(c switch { 'n' => '\n', 't' => '\t', 'u' when i + 4 < inner.Length => (char)Convert.ToInt32(inner.Substring(i + 1, 4), 16), _ => c });
            if (c == 'u') i += 4;
        }
        return sb.ToString();
    }

    // PK.t("…"), PK.T("…"), I18N.t("…"), I18N.T("…"): the first argument is a string literal (a call with a variable
    // translates a text that was marked in a table with PK.T, so it is not read here).
    private static readonly Regex Call = new(@"(?<![\w$.])(?:PK|I18N)\.[tT]\(\s*(?<lit>""(?:[^""\\\n]|\\.)*""|'(?:[^'\\\n]|\\.)*'|`(?:[^`\\]|\\.)*`)(?<next>\s*[,)+]?)", RegexOptions.Compiled);
    private static readonly Regex NonLiteral = new(@"(?<![\w$.])(?:PK|I18N)\.[tT]\(\s*[""'`]", RegexOptions.Compiled);

    private static IEnumerable<string> PageScripts() =>
        Directory.GetFiles(Path.Combine(Www(), "js"), "*.js").Where(f => !Path.GetFileName(f).StartsWith("i18n", StringComparison.Ordinal))
            .Append(Path.Combine(Www(), "login.js"));

    private static string Line(string text, int index) => (text.Take(index).Count(c => c == '\n') + 1).ToString();

    [Fact]
    public void Every_PK_t_text_has_a_German_entry()
    {
        var de = Dictionary();
        var missing = new SortedSet<string>(StringComparer.Ordinal);
        var bad = new List<string>();
        foreach (var file in PageScripts())
        {
            var text = Read(file);
            var name = Path.GetFileName(file);
            var literals = Call.Matches(text);
            foreach (Match m in literals)
            {
                var lit = m.Groups["lit"].Value;
                var next = m.Groups["next"].Value.Trim();
                if (next == "+" || (lit[0] == '`' && lit.Contains("${"))) { bad.Add($"{name}:{Line(text, m.Index)} the text must be one plain string literal, not built with + or ${{}}: use PK.t(\"… {{name}}\", {{ name }})"); continue; }
                var key = Unescape(lit);
                if (!de.ContainsKey(key)) missing.Add($"{name}:{Line(text, m.Index)}  {key}");
            }
            // A call with a quote right after the bracket that the pattern above did not read as a whole literal
            var read = literals.Select(m => m.Index).ToHashSet();
            foreach (Match m in NonLiteral.Matches(text))
                if (!read.Contains(m.Index)) bad.Add($"{name}:{Line(text, m.Index)} unreadable text in PK.t(…): keep it on one line, as one literal");
        }
        Assert.True(bad.Count == 0, "Texts the test cannot read:\n" + string.Join("\n", bad.Take(30)));
        Assert.True(missing.Count == 0, $"{missing.Count} text(s) have no German entry in wwwroot/js/i18n-de.js:\n" + string.Join("\n", missing.Take(40)));
    }

    [Fact]
    public void The_static_page_text_has_a_German_entry()
    {
        var de = Dictionary();
        var missing = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var file in new[] { "index.html", "login.html" })
        {
            var html = Read(Path.Combine(Www(), file));
            html = Regex.Replace(html, @"<(script|style)\b.*?</\1>", "", RegexOptions.Singleline);
            html = Regex.Replace(html, @"<!--.*?-->", "", RegexOptions.Singleline);
            foreach (Match m in Regex.Matches(html, @">([^<>]+)<"))
            {
                var t = System.Net.WebUtility.HtmlDecode(m.Groups[1].Value).Trim();
                if (t.Length > 0 && Regex.IsMatch(t, "[A-Za-z]{2}") && t != "PLC-Ki" && !de.ContainsKey(t)) missing.Add($"{file}: {t}");
            }
            foreach (Match m in Regex.Matches(html, @"\s(?:title|aria-label|placeholder|alt)=""([^""]+)"""))
            {
                var t = System.Net.WebUtility.HtmlDecode(m.Groups[1].Value).Trim();
                if (t != "PLC-Ki" && Regex.IsMatch(t, "[A-Za-z]{2}") && !de.ContainsKey(t)) missing.Add($"{file}: {t}");
            }
        }
        Assert.True(missing.Count == 0, $"{missing.Count} text(s) in the fixed page have no German entry:\n" + string.Join("\n", missing.Take(40)));
    }

    [Fact]
    public void A_German_text_keeps_the_same_placeholders_and_forms()
    {
        var wrong = new List<string>();
        static string Holes(string s) => string.Join(",", Regex.Matches(s, @"\{(\w+)\}").Select(m => m.Groups[1].Value).Distinct().OrderBy(x => x, StringComparer.Ordinal));
        foreach (var (en, de) in Dictionary())
        {
            if (string.IsNullOrWhiteSpace(de)) wrong.Add($"empty German text for: {en}");
            else if (Holes(en) != Holes(de)) wrong.Add($"{{names}} differ: {en}  →  {de}");
            else if (en.Contains('|') != de.Contains('|') && Holes(en).Contains('n')) wrong.Add($"singular|plural differs: {en}  →  {de}");
        }
        Assert.True(wrong.Count == 0, string.Join("\n", wrong.Take(30)));
    }

    // What the server says in its own words and the page shows (refusals, validation errors): a German entry, or a rule
    private static readonly Regex ServerText = new(@"(?:error\s*=\s*|new\s+(?:PlantException|AskException|JobException)\()\s*""(?<s>(?:[^""\\]|\\.)*)""", RegexOptions.Compiled);

    [Fact]
    public void The_servers_own_messages_have_a_German_entry_or_rule()
    {
        var de = Dictionary();
        var rules = Rules();
        var missing = new SortedSet<string>(StringComparer.Ordinal);
        var src = Path.Combine(Root(), "src");
        foreach (var file in Directory.GetFiles(src, "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar) || file.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar)) continue;
            foreach (Match m in ServerText.Matches(Read(file)))
            {
                // an interpolated string ($"…") has a rule instead; its text starts after the $, which the pattern does not take
                if (m.Value.Contains('$')) continue;
                string s;
                try { s = JsonSerializer.Deserialize<string>("\"" + m.Groups["s"].Value + "\"")!; } catch (JsonException) { continue; }
                if (!de.ContainsKey(s) && !rules.Any(r => r.IsMatch(s))) missing.Add($"{Path.GetFileName(file)}: {s}");
            }
        }
        Assert.True(missing.Count == 0, $"{missing.Count} server message(s) have no German entry or rule in i18n-de.js:\n" + string.Join("\n", missing.Take(40)));
    }

    [Fact]
    public void The_language_is_en_or_de_and_German_pages_use_the_German_format_until_one_is_chosen()
    {
        Assert.Equal(new[] { "en", "de" }, SettingsStore.Languages);
        Assert.Contains("language", PreferenceStore.Keys);
    }
}
