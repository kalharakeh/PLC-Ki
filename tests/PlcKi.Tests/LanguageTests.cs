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

    // ---- the server's texts run through the German entries and rules, the way PK.srv does (the entry first, then the first rule that fits) ----

    private static List<(Regex Re, string To)> RulesWithTarget()
    {
        var js = Read(Path.Combine(Www(), "js", "i18n-de.js"));
        var list = new List<(Regex, string)>();
        foreach (Match m in Regex.Matches(js, @"^\s*\[/(?<re>.+)/(?<f>[a-z]*),\s*""(?<to>(?:[^""\\]|\\.)*)""\],?\s*$", RegexOptions.Multiline))
            list.Add((new Regex(m.Groups["re"].Value, m.Groups["f"].Value.Contains('i') ? RegexOptions.IgnoreCase : RegexOptions.None), JsonSerializer.Deserialize<string>("\"" + m.Groups["to"].Value + "\"")!));
        return list;
    }

    /// <summary>The German of a whole server text, or null when there is no entry or rule. $T1, $C1, $D1, $W1 and $I1 only insert the group as it is here.</summary>
    private static string? Srv(string text)
    {
        var de = Dictionary();
        if (de.TryGetValue(text, out var exact)) return exact;
        foreach (var (re, to) in RulesWithTarget())
        {
            var m = re.Match(text);
            if (m.Success) return Regex.Replace(to, @"\$[TCDWI]?(\d)", g => m.Groups[int.Parse(g.Groups[1].Value)].Value);
        }
        return null;
    }

    // A word that only an English sentence has: a German text of ours must not keep it
    private static readonly Regex English = new(@"(the|was|were|with|without|failed|could not|compile|error|warning|refused|download|check|then|import|not found)", RegexOptions.IgnoreCase);

    [Theory]
    // History insight (AlarmMemory.Insight): the frame is German, the technicians' own words stay
    [InlineData("Happened 3 times in the last 30 days (12 times in all).", new[] { "3", "12" })]
    [InlineData("Happened twice in the last 30 days.", new[] { "30" })]
    [InlineData("Last time (3 Mar) it was fixed by: replaced the filter.", new[] { "3 Mar", "replaced the filter" })]
    [InlineData("Most often noted cause: dirty sensor (4 times).", new[] { "dirty sensor", "4" })]
    [InlineData("Usually comes in Step 20 (5 of 7 times).", new[] { "5", "7" })]
    // generator summary (PackageSummary)
    [InlineData("Wrote function block FB_Mixer: 4 networks.", new[] { "FB_Mixer", "4" })]
    [InlineData("Changed function block FB_Mixer: now 6 networks (was 4).", new[] { "FB_Mixer", "6", "4" })]
    [InlineData("Added 2 tags: XV101_Open, XV101_Close.", new[] { "XV101_Open", "XV101_Close", "2" })]
    // job log (develop, generate): paths, tag names and TIA's own words stay
    [InlineData("=== V3: import and compile on the TIA PC ===", new[] { "V3" })]
    [InlineData("TIA report for V3: 1 refused file(s), 2 error(s), 0 warning(s).", new[] { "V3", "1", "2", "0" })]
    [InlineData("Download to PLCSIM failed: PLCSIM is not running", new[] { "PLCSIM is not running" })]
    [InlineData("PLC before download: Run, 2 active alarm(s).", new[] { "Run", "2" })]
    // drive texts (Telegrams, DeviceDiagnostics)
    [InlineData("Telegram 1", new[] { "1" })]
    [InlineData("Drive block FB_Drive reports an error (DiagId 16#8001).", new[] { "FB_Drive", "16#8001" })]
    // TIA import errors (ImportReport.AsProblems): our frame is German, TIA's own words stay as they came
    [InlineData("TIA refused FB_GenValve.xml: Block FB_GenValve is already in the project", new[] { "FB_GenValve.xml", "Block FB_GenValve is already in the project" })]
    [InlineData("Compile error in FB_GenValve: Tag \"XV201_Open\" not defined", new[] { "FB_GenValve", "Tag \"XV201_Open\" not defined" })]
    [InlineData("Compile warning in OB1: Network 3 is never reached", new[] { "OB1", "Network 3 is never reached" })]
    [InlineData("While running on PLCSIM: Alarm came up: Motor overload (Fault)", new[] { "Motor overload (Fault)" })]
    [InlineData("While running on PLCSIM: The CPU is in Stop instead of RUN.", new[] { "Stop" })]
    [InlineData("Lost the connection to the PLC: timed out", new[] { "timed out" })]
    public void A_server_text_comes_out_German_with_its_values_kept(string english, string[] kept)
    {
        var de = Srv(english);
        Assert.True(de != null, "no German entry or rule for: " + english);
        Assert.NotEqual(english, de);
        foreach (var k in kept) Assert.True(de!.Contains(k), $"'{k}' is lost: {english}  →  {de}");
    }

    [Fact]
    public void The_TIA_import_problems_the_server_builds_come_out_German()
    {
        var report = new PlcKi.Core.Generate.ImportReport { Download = "PLCSIM Advanced instance not found", CompileState = "Error" };
        report.FileErrors.Add(new PlcKi.Core.Generate.ReportFileError { File = "FB_A.xml", Error = "Duplicate block name" });
        report.Messages.Add(new PlcKi.Core.Generate.ReportMessage { State = "Error", Path = "FB_A", Description = "Tag X is not defined" });
        report.Messages.Add(new PlcKi.Core.Generate.ReportMessage { State = "Warning", Path = "OB1", Description = "Unused variable" });
        report.Runtime.Add("The CPU is in Stop instead of RUN.");
        var problems = report.AsProblems();
        Assert.Equal(5, problems.Count);
        foreach (var p in problems)
        {
            var de = Srv(p);
            Assert.True(de != null && de != p, "no German for: " + p);
            Assert.False(English.IsMatch(de!.Split(':')[0]), "the frame is still English: " + de);
        }
        Assert.Contains("Tag X is not defined", Srv(problems[1]));      // TIA's own words stay
        Assert.Contains("PLCSIM Advanced instance not found", Srv(problems[3]));
    }

    [Theory]
    [InlineData("The TIA project is open in TIA Portal, or a hidden TIA still holds it.", "already been opened by user")]
    [InlineData("The TIA project was not found on the TIA PC.", "project not found")]
    [InlineData("Check that TIA Portal and the TIA helper run on the TIA PC", "something else")]
    public void The_import_hint_comes_out_German_as_a_whole(string startsWith, string tiaSaid)
    {
        var hint = PlcKi.Core.Generate.ImportReport.ImportHint(tiaSaid);
        Assert.StartsWith(startsWith, hint);
        var de = Srv(hint);
        Assert.True(de != null && de != hint, "no German for: " + hint);
        Assert.DoesNotMatch(English, de!.Replace("taskkill /im Siemens.Automation.Portal.exe /f", ""));
    }

    [Theory]
    [InlineData("fail-safe program")]
    [InlineData("PLCSIM not running")]
    public void The_download_hint_comes_out_German(string tiaSaid)
    {
        var hint = PlcKi.Server.Generate.DevelopCommand.DownloadHint(tiaSaid);
        var de = Srv(hint);
        Assert.True(de != null && de != hint, "no German for: " + hint);
    }
}
