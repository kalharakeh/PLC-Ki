using System.Text;
using System.Text.RegularExpressions;
using UglyToad.PdfPig;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;

namespace PlcKi.Core.Knowledge;

/// <summary>
/// Reads a document once into text pages and the fault codes in it: PDF (text layer, page by page), CSV/TSV fault
/// tables (code; text; cause; remedy) and plain text. Scanned PDFs have no text layer; they are kept and marked.
/// </summary>
public static class DocumentReader
{
    private const int TextPageChars = 3000;
    private const int MaxFaults = 20000;

    public static DocumentIndex Read(string path, DocumentInfo info)
    {
        var idx = new DocumentIndex { Info = info };
        try
        {
            var ext = Path.GetExtension(path).ToLowerInvariant();
            switch (ext)
            {
                case ".pdf": idx.Pages = PdfPages(path); break;
                case ".csv" or ".tsv":
                    var (pages, faults) = FaultTable(File.ReadAllText(path));
                    idx.Pages = pages;
                    idx.Faults = faults;
                    break;
                default: idx.Pages = Chunk(File.ReadAllText(path)); break;
            }
            if (ext != ".csv" && ext != ".tsv") idx.Faults = FindFaultCodes(idx.Pages);

            info.Pages = idx.Pages.Count;
            info.FaultCodes = idx.Faults.Count;
            var chars = idx.Pages.Sum(p => p.Trim().Length);
            if (ext == ".pdf" && chars < 20 * Math.Max(1, idx.Pages.Count) / 4 && chars < 200)
            {
                info.State = "notext";
                info.Note = "This PDF has no text in it (a scan or a picture), so PLC-Ki cannot search it yet. " +
                            "A PDF exported from the program that made it (EPLAN, Word, the maker's website) has text.";
            }
            else
            {
                info.State = "ready";
                info.Note = null;
            }
        }
        catch (Exception ex)
        {
            info.State = "failed";
            info.Note = "Could not read this file: " + ex.Message;
            idx.Pages = new();
            idx.Faults = new();
        }
        return idx;
    }

    private static List<string> PdfPages(string path)
    {
        var pages = new List<string>();
        using var doc = PdfDocument.Open(path);
        foreach (var page in doc.GetPages())
        {
            string text;
            try { text = ContentOrderTextExtractor.GetText(page); }
            catch (Exception) { text = page.Text; }
            pages.Add(text ?? "");
        }
        return pages;
    }

    private static List<string> Chunk(string text)
    {
        var pages = new List<string>();
        var sb = new StringBuilder();
        foreach (var line in text.Replace("\r\n", "\n").Split('\n'))
        {
            if (sb.Length + line.Length > TextPageChars && sb.Length > 0) { pages.Add(sb.ToString()); sb.Clear(); }
            sb.AppendLine(line);
        }
        if (sb.Length > 0 || pages.Count == 0) pages.Add(sb.ToString());
        return pages;
    }

    // ---- fault tables (CSV) ----

    private static readonly string[] CodeCols = { "code", "fault", "error", "number", "nr", "no", "id", "alarm", "message number", "fehler", "fehlernummer", "meldung" };
    private static readonly string[] TextCols = { "text", "title", "message", "description", "name", "meaning", "fault text", "bezeichnung", "meldetext", "beschreibung" };
    private static readonly string[] CauseCols = { "cause", "causes", "reason", "ursache" };
    private static readonly string[] FixCols = { "remedy", "fix", "action", "solution", "corrective action", "what to do", "abhilfe", "maßnahme", "massnahme" };

    /// <summary>A fault list exported to CSV: one fault per row. The header names the columns; without one the
    /// first column is the code and the second the text.</summary>
    public static (List<string> Pages, List<FaultCode> Faults) FaultTable(string text)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n').Where(l => l.Trim().Length > 0).ToList();
        if (lines.Count == 0) return (new() { "" }, new());
        char sep = new[] { '\t', ';', ',' }.OrderByDescending(c => lines.Take(5).Sum(l => l.Count(x => x == c))).First();
        var rows = lines.Select(l => SplitCsv(l, sep)).ToList();

        int Col(string[] names, List<string> header) =>
            header.FindIndex(h => names.Contains(h.Trim().ToLowerInvariant().TrimEnd(':')));
        var head = rows[0];
        int code = Col(CodeCols, head), title = Col(TextCols, head), cause = Col(CauseCols, head), fix = Col(FixCols, head);
        bool hasHeader = code >= 0 || title >= 0;
        if (!hasHeader) { code = 0; title = rows[0].Count > 1 ? 1 : -1; }
        if (code < 0) code = title == 0 ? 1 : 0;
        if (title < 0) title = code == 0 ? 1 : 0;

        var faults = new List<FaultCode>();
        var pageText = new List<string>();
        for (int i = hasHeader ? 1 : 0; i < rows.Count && faults.Count < MaxFaults; i++)
        {
            var r = rows[i];
            string Cell(int c) => c >= 0 && c < r.Count ? r[c].Trim() : "";
            var f = new FaultCode
            {
                Code = Cell(code),
                Title = Cell(title),
                Cause = Cell(cause) is { Length: > 0 } c1 ? c1 : null,
                Fix = Cell(fix) is { Length: > 0 } c2 ? c2 : null,
            };
            var rest = r.Select((v, j) => (v, j)).Where(x => x.j != code && x.j != title && x.j != cause && x.j != fix && x.v.Trim().Length > 0)
                .Select(x => (hasHeader && x.j < head.Count ? head[x.j].Trim() + ": " : "") + x.v.Trim()).ToList();
            if (rest.Count > 0) f.Details = string.Join("; ", rest);
            if (f.Code.Length == 0 || f.Code.Length > 30) continue;
            faults.Add(f);
            pageText.Add($"{f.Code} {f.Title}" + (f.Cause != null ? $"\nCause: {f.Cause}" : "") + (f.Fix != null ? $"\nRemedy: {f.Fix}" : "") +
                         (f.Details != null ? "\n" + f.Details : ""));
        }
        // Pages of about 25 rows each, so search hits point at a few faults, not the whole list
        var pages = pageText.Chunk(25).Select(c => string.Join("\n\n", c)).ToList();
        for (int p = 0; p < pages.Count; p++)
            foreach (var f in faults.Skip(p * 25).Take(25)) f.Page = p + 1;
        if (pages.Count == 0) pages.Add("");
        return (pages, faults);
    }

    private static List<string> SplitCsv(string line, char sep)
    {
        var cells = new List<string>();
        var sb = new StringBuilder();
        bool quoted = false;
        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (quoted)
            {
                if (c == '"' && i + 1 < line.Length && line[i + 1] == '"') { sb.Append('"'); i++; }
                else if (c == '"') quoted = false;
                else sb.Append(c);
            }
            else if (c == '"') quoted = true;
            else if (c == sep) { cells.Add(sb.ToString()); sb.Clear(); }
            else sb.Append(c);
        }
        cells.Add(sb.ToString());
        return cells;
    }

    // ---- fault codes in manuals ----

    // A line that starts with a code and goes on with its text, as list manuals print them:
    //   F07801 Motor overcurrent        SRVO-062 BZAL alarm (Group:1 Axis:2)       A0123: Pressure too low
    // Codes need letters before the digits (or 0x / 16#), so chapter and page numbers do not count.
    private static readonly Regex FaultLine = new(
        @"^\s*(?<code>(?:[A-Z]{1,5}-?\d{2,6}|0x[0-9A-F]{3,8}|16#[0-9A-F]{2,8}))(?:\s*\([^)]{1,12}\))?\s*(?:[:\-–]\s*|\s+)(?<title>[\p{L}\d\(].{3,160})$",
        RegexOptions.Compiled);
    private static readonly Regex SectionWord = new(@"^\s*(Reaction|Acknowledge|Acknowledgement|Cause|Remedy|Fix|Action|Solution|Ursache|Abhilfe|Reaktion|Quittierung)\s*:", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Fault codes listed in a manual. A code family (the letters in front, e.g. F, A, SRVO) only counts when it
    /// appears at least 5 times, so words like "DB100 is used for ..." in running text are not taken as codes.
    /// For each code the text after it (reaction, cause, remedy) is kept, up to the next code.
    /// </summary>
    public static List<FaultCode> FindFaultCodes(IReadOnlyList<string> pages)
    {
        var found = new List<FaultCode>();
        for (int p = 0; p < pages.Count; p++)
        {
            FaultCode? cur = null;
            var details = new StringBuilder();
            void Close()
            {
                if (cur == null) return;
                cur.Details = Tidy(details.ToString(), 1200);
                cur.Cause = Section(cur.Details, "cause|ursache");
                cur.Fix = Section(cur.Details, "remedy|fix|action|solution|abhilfe");
                found.Add(cur);
                cur = null;
                details.Clear();
            }
            foreach (var raw in pages[p].Replace("\r\n", "\n").Split('\n'))
            {
                var line = raw.TrimEnd();
                var m = FaultLine.Match(line);
                if (m.Success && !SectionWord.IsMatch(line) && !Regex.IsMatch(m.Groups["title"].Value, @"^\d+(\.\d+)*\s*$"))
                {
                    Close();
                    cur = new FaultCode { Code = m.Groups["code"].Value, Title = Tidy(m.Groups["title"].Value, 160), Page = p + 1 };
                    continue;
                }
                if (cur != null && details.Length < 1500) details.AppendLine(line);
            }
            Close();
        }

        // Keep code families that look like a real list
        static string Family(string code) => Regex.Match(code.ToUpperInvariant(), @"^(0X|16#|[A-Z]+)").Value;
        var families = found.GroupBy(f => Family(f.Code)).Where(g => g.Count() >= 5).Select(g => g.Key).ToHashSet();
        // Index pages and the table of contents list a code with a page number only: keep the one with the most text
        return found.Where(f => families.Contains(Family(f.Code)))
            .GroupBy(f => DocumentLibrary.NormalizeCode(f.Code))
            .Select(g => g.OrderByDescending(f => (f.Details ?? "").Length + (Regex.IsMatch(f.Title, @"\.{4,}|\s\d+$") ? -1000 : 0)).First())
            .Take(MaxFaults).ToList();
    }

    private static string? Section(string? text, string names)
    {
        if (text == null) return null;
        var m = Regex.Match(text, $@"(?:^|\n)\s*(?:{names})\s*:\s*(?<t>[\s\S]*?)(?=\n\s*(?:Reaction|Acknowledge|Acknowledgement|Cause|Remedy|Fix|Action|Solution|Ursache|Abhilfe|Reaktion|Quittierung|Note|See also)\s*:|$)",
            RegexOptions.IgnoreCase);
        return m.Success && m.Groups["t"].Value.Trim().Length > 0 ? Tidy(m.Groups["t"].Value, 600) : null;
    }

    private static string Tidy(string s, int max)
    {
        var t = Regex.Replace(s, @"[ \t]+", " ");
        t = Regex.Replace(t, @"\n\s*\n+", "\n").Trim();
        return t.Length <= max ? t : t[..max].TrimEnd() + " …";
    }
}
