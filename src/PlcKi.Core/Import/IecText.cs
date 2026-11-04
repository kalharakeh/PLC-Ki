using System.Text;
using System.Text.RegularExpressions;

namespace PlcKi.Core.Import;

/// <summary>A comment found in IEC 61131-3 text: the line it starts on and whether code comes before it on that line.</summary>
public sealed record IecComment(int Line, string Text, bool AfterCode);

/// <summary>
/// Reads IEC 61131-3 Structured Text declarations (VAR ... END_VAR, STRUCT, enums), as written by
/// Beckhoff TwinCAT and CODESYS. Brand-neutral: only the standard syntax, no vendor files.
/// </summary>
public static class IecText
{
    /// <summary>
    /// Blanks out comments (// and (* *)), strings and pragmas ({attribute ...}) but keeps every newline,
    /// so positions and line numbers in the result match the original text.
    /// </summary>
    public static string Clean(string text, List<IecComment>? comments = null)
    {
        var sb = new StringBuilder(text.Length);
        int line = 1, i = 0;
        bool codeOnLine = false;
        void Blank(int from, int to)
        {
            for (int k = from; k < to; k++)
            {
                if (text[k] == '\n') { sb.Append('\n'); line++; codeOnLine = false; }
                else sb.Append(' ');
            }
        }
        while (i < text.Length)
        {
            var c = text[i];
            if (c == '/' && i + 1 < text.Length && text[i + 1] == '/')
            {
                var end = text.IndexOf('\n', i);
                if (end < 0) end = text.Length;
                comments?.Add(new IecComment(line, text[(i + 2)..end].TrimStart('/').Trim(), codeOnLine));
                Blank(i, end);
                i = end;
            }
            else if (c == '(' && i + 1 < text.Length && text[i + 1] == '*')
            {
                var end = text.IndexOf("*)", i + 2, StringComparison.Ordinal);
                end = end < 0 ? text.Length : end + 2;
                var body = text[(i + 2)..Math.Max(i + 2, end - 2)];
                comments?.Add(new IecComment(line, Regex.Replace(body.Trim(), @"\s*\n\s*", " "), codeOnLine));
                Blank(i, end);
                i = end;
            }
            else if (c == '{')
            {
                var end = text.IndexOf('}', i);
                end = end < 0 ? text.Length : end + 1;
                Blank(i, end);
                i = end;
            }
            else if (c is '\'' or '"')
            {
                // keep the quotes so the statement stays valid, blank the content
                var end = i + 1;
                while (end < text.Length && text[end] != c) end += text[end] == '$' ? 2 : 1;
                end = Math.Min(end + 1, text.Length);
                sb.Append(c);
                Blank(i + 1, end - 1);
                if (end - 1 > i) sb.Append(c);
                codeOnLine = true;
                i = end;
            }
            else
            {
                sb.Append(c);
                if (c == '\n') { line++; codeOnLine = false; }
                else if (!char.IsWhiteSpace(c)) codeOnLine = true;
                i++;
            }
        }
        return sb.ToString();
    }

    private static readonly Regex SectionRx = new(
        @"\G\s*(?<kw>VAR_INPUT|VAR_OUTPUT|VAR_IN_OUT|VAR_GLOBAL|VAR_TEMP|VAR_STAT|VAR_INST|VAR_EXTERNAL|VAR_CONFIG|VAR)\b(?<mods>(\s+(CONSTANT|RETAIN|PERSISTENT|NON_RETAIN))*)",
        RegexOptions.IgnoreCase);
    private static readonly Regex EndRx = new(@"\G\s*(END_VAR|END_STRUCT|END_UNION)\b", RegexOptions.IgnoreCase);
    private static readonly Regex DeclRx = new(
        @"^(?<names>[A-Za-z_]\w*(\s*,\s*[A-Za-z_]\w*)*)\s*(AT\s+(?<at>%[\w.*]+))?\s*:\s*(?<rest>.+)$",
        RegexOptions.Singleline | RegexOptions.IgnoreCase);

    /// <summary>
    /// Every variable declared in VAR sections (or between STRUCT and END_STRUCT) of <paramref name="text"/>.
    /// <paramref name="functionLocals"/>: plain VAR belongs to a FUNCTION or METHOD and only lives while it runs (Temp).
    /// </summary>
    public static List<ExportMember> Declarations(string text, bool functionLocals = false)
    {
        var comments = new List<IecComment>();
        var clean = Clean(text, comments);
        var lineStarts = new List<int> { 0 };
        for (int k = 0; k < clean.Length; k++) if (clean[k] == '\n') lineStarts.Add(k + 1);
        int LineOf(int pos) { var l = lineStarts.BinarySearch(pos); return (l >= 0 ? l : ~l - 1) + 1; }

        var result = new List<ExportMember>();
        string? section = null;
        int pos = 0;
        var structStart = Regex.Match(clean, @"\b(STRUCT|UNION)\b", RegexOptions.IgnoreCase);
        var sectionStart = Regex.Match(clean, @"\bVAR\w*\b", RegexOptions.IgnoreCase);
        if (structStart.Success && (!sectionStart.Success || structStart.Index < sectionStart.Index))
        {
            section = "Static";
            pos = structStart.Index + structStart.Length;
        }
        int lastDeclLine = 0;
        while (pos < clean.Length)
        {
            if (section == null)
            {
                var s = SectionRx.Match(clean, pos);
                if (!s.Success)
                {
                    // skip to the next VAR keyword (header text such as FUNCTION_BLOCK FB_X EXTENDS ...)
                    var next = Regex.Match(clean[pos..], @"\bVAR\w*\b", RegexOptions.IgnoreCase);
                    if (!next.Success) break;
                    pos += next.Index;
                    continue;
                }
                var kw = s.Groups["kw"].Value.ToUpperInvariant();
                var mods = s.Groups["mods"].Value.ToUpperInvariant();
                section = mods.Contains("CONSTANT") ? "Constant" : kw switch
                {
                    "VAR_INPUT" => "Input",
                    "VAR_OUTPUT" => "Output",
                    "VAR_IN_OUT" => "InOut",
                    "VAR_TEMP" => "Temp",
                    "VAR_EXTERNAL" or "VAR_CONFIG" => "External",
                    "VAR" when functionLocals => "Temp",
                    _ => "Static",
                };
                pos = s.Index + s.Length;
                lastDeclLine = LineOf(pos);
                continue;
            }
            var e = EndRx.Match(clean, pos);
            if (e.Success) { section = null; pos = e.Index + e.Length; continue; }

            // one declaration, up to the next ';' outside brackets (or an END_ keyword)
            int depth = 0, k = pos;
            for (; k < clean.Length; k++)
            {
                var ch = clean[k];
                if (ch is '(' or '[') depth++;
                else if (ch is ')' or ']') depth--;
                else if (ch == ';' && depth <= 0) break;
                else if (depth <= 0 && (ch == 'E' || ch == 'e') && EndRx.Match(clean, k).Success && (k == 0 || !char.IsLetterOrDigit(clean[k - 1]))) break;
            }
            var stmt = clean[pos..k].Trim();
            var startPos = pos + (clean[pos..k].Length - clean[pos..k].TrimStart().Length);
            var endLine = LineOf(Math.Max(pos, k - 1));
            var startLine = LineOf(startPos);
            pos = k < clean.Length && clean[k] == ';' ? k + 1 : k;
            if (stmt.Length == 0) continue;
            var m = DeclRx.Match(stmt);
            if (!m.Success) continue;

            var (type, init) = SplitInit(m.Groups["rest"].Value);
            // a comment on the declaration's last line wins; else comment lines right above it
            var comment = comments.FirstOrDefault(c => c.AfterCode && c.Line == endLine)?.Text;
            if (string.IsNullOrEmpty(comment))
            {
                var above = comments.Where(c => !c.AfterCode && c.Line < startLine && c.Line > lastDeclLine).Select(c => c.Text).Where(t => t.Length > 0).ToList();
                if (above.Count > 0) comment = string.Join(" ", above);
            }
            lastDeclLine = endLine;
            foreach (var name in m.Groups["names"].Value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
                result.Add(new ExportMember
                {
                    Name = name,
                    Section = section,
                    Datatype = Regex.Replace(type, @"\s+", " "),
                    Comment = string.IsNullOrWhiteSpace(comment) ? null : comment,
                    StartValue = init,
                    Address = m.Groups["at"].Success ? m.Groups["at"].Value : null,
                });
        }
        return result;
    }

    /// <summary>"INT := 5" -> ("INT", "5"); "FB_X(a := 1)" -> ("FB_X", null).</summary>
    private static (string Type, string? Init) SplitInit(string rest)
    {
        int depth = 0;
        for (int i = 0; i < rest.Length - 1; i++)
        {
            var ch = rest[i];
            if (ch is '(' or '[') depth++;
            else if (ch is ')' or ']') depth--;
            else if (ch == ':' && rest[i + 1] == '=' && depth == 0)
                return (StripCtorArgs(rest[..i].Trim()), rest[(i + 2)..].Trim());
        }
        return (StripCtorArgs(rest.Trim()), null);
    }

    /// <summary>Removes FB_init arguments: "FB_Axis(nAxis := 1)" -> "FB_Axis". Keeps STRING(80) and ARRAY[..] as they are.</summary>
    private static string StripCtorArgs(string type)
    {
        var p = type.IndexOf('(');
        if (p <= 0) return type;
        var head = type[..p].Trim();
        return head.Equals("STRING", StringComparison.OrdinalIgnoreCase) || head.Equals("WSTRING", StringComparison.OrdinalIgnoreCase) ? type : head;
    }

    /// <summary>The names of an enum type: "TYPE E_Mode : (Idle := 0, Auto := 10) INT; END_TYPE" -> Idle=0, Auto=10.</summary>
    public static List<(string Name, string? Value)> EnumValues(string text)
    {
        var clean = Clean(text);
        var open = clean.IndexOf('(');
        var close = open < 0 ? -1 : clean.IndexOf(')', open);
        if (open < 0 || close < 0) return new();
        return clean[(open + 1)..close].Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Split(":=", 2, StringSplitOptions.TrimEntries))
            .Where(p => Regex.IsMatch(p[0], @"^[A-Za-z_]\w*$"))
            .Select(p => (p[0], p.Length > 1 ? p[1] : (string?)null)).ToList();
    }

    /// <summary>
    /// Maps an IEC elementary type to a type the machine model reads, or null if PLC-Ki cannot use it.
    /// Same buckets as the Siemens mapping: small integers read as Int, 32-bit as DInt.
    /// </summary>
    public static string? ModelType(string datatype) => datatype.Trim().ToUpperInvariant() switch
    {
        "BOOL" or "BIT" => "Bool",
        "SINT" or "USINT" or "INT" or "UINT" or "BYTE" or "WORD" => "Int",
        "DINT" or "UDINT" or "DWORD" => "DInt",
        "REAL" => "Real",
        "LREAL" => "LReal",
        "TIME" => "Time",
        _ => null,
    };

    /// <summary>The members of the standard IEC function blocks that matter for troubleshooting (timers, edges, counters).</summary>
    public static IReadOnlyList<ExportMember>? StandardBlock(string type) => type.Trim().ToUpperInvariant() switch
    {
        "TON" or "TOF" or "TP" => new[]
        {
            Std("IN", "Input", "BOOL", "timer input"), Std("PT", "Input", "TIME", "preset time"),
            Std("Q", "Output", "BOOL", "timer output"), Std("ET", "Output", "TIME", "elapsed time"),
        },
        "R_TRIG" or "F_TRIG" => new[] { Std("CLK", "Input", "BOOL", "signal"), Std("Q", "Output", "BOOL", "edge detected") },
        "CTU" or "CTD" => new[]
        {
            Std("PV", "Input", "INT", "preset value"), Std("Q", "Output", "BOOL", "preset reached"), Std("CV", "Output", "INT", "count"),
        },
        "RS" or "SR" => new[] { Std("Q1", "Output", "BOOL", "latched output") },
        _ => null,
    };

    private static ExportMember Std(string name, string section, string type, string comment) =>
        new() { Name = name, Section = section, Datatype = type, Comment = comment };
}
