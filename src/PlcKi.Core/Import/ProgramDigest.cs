using System.Text;

namespace PlcKi.Core.Import;

/// <summary>
/// Writes one PLC's program as compact text for Claude to read at import time: the call structure, every block
/// with its interface comments and SCL source, and the full list of tags PLC-Ki can read (exact symbols).
/// </summary>
public static class ProgramDigest
{
    public static string Build(ProgramExport export, ExportPlc plc, int maxTags = 3000)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Project: {export.Project}" + (export.Archive != null ? $" (archive {export.Archive})" : ""));
        sb.AppendLine($"PLC: {plc.Name}" + (plc.Cpu != null ? $", {plc.Cpu}" : "") + (plc.IpAddresses.Count > 0 ? $", IP {string.Join(" / ", plc.IpAddresses)}" : ""));
        var problems = export.Problems.Concat(plc.Problems).ToList();
        if (problems.Count > 0) sb.AppendLine("Export problems: " + string.Join("; ", problems));
        sb.AppendLine();

        sb.AppendLine("## Call structure");
        foreach (var ob in plc.Blocks.Where(b => b.Kind == "OB").OrderBy(b => b.Number))
            WriteCalls(sb, plc, ob, "", 0, new HashSet<string>());
        sb.AppendLine();

        sb.AppendLine("## Blocks");
        var order = new[] { "OB", "FB", "FC", "GlobalDB", "InstanceDB", "ArrayDB", "UDT" };
        foreach (var b in plc.Blocks.Concat(plc.Types).OrderBy(b => Array.IndexOf(order, b.Kind)).ThenBy(b => b.Name, StringComparer.Ordinal))
            WriteBlock(sb, b);

        var tags = ProgramExport.Tags(plc).Where(t => t.ModelType != null).ToList();
        sb.AppendLine($"## Readable tags ({tags.Count}). Use these symbols exactly as written.");
        foreach (var t in tags.Take(maxTags))
            sb.AppendLine($"{t.Symbol} : {t.Datatype}" + (t.Comment != null ? $" // {t.Comment}" : ""));
        if (tags.Count > maxTags) sb.AppendLine($"... {tags.Count - maxTags} more left out.");
        WriteHmi(sb, export, plc);
        return sb.ToString();
    }

    /// <summary>
    /// The HMI panels: which values the operator sets on which screen (with the texts next to them), the buttons that
    /// set PLC values, the text lists that turn PLC numbers into words, and HMI alarms. PLC values use the exact
    /// symbols of the tag list.
    /// </summary>
    public static void WriteHmi(StringBuilder sb, ProgramExport export, ExportPlc plc, int maxChars = 40_000)
    {
        if (export.Hmis.Count == 0) return;
        var symbols = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in ProgramExport.Tags(plc)) symbols.TryAdd(ProgramExport.NormalizeSymbol(t.Symbol), t.Symbol);
        string Sym(string s) => symbols.TryGetValue(ProgramExport.NormalizeSymbol(s), out var x) ? x : s;
        var start = sb.Length;
        bool Full() => sb.Length - start > maxChars;
        foreach (var hmi in export.Hmis)
        {
            sb.AppendLine();
            sb.AppendLine($"## HMI panel {hmi.Name}" + (hmi.Type != null ? $" ({hmi.Type})" : "") + (hmi.Kind == "unified" ? ", WinCC Unified" : ", WinCC Comfort/Advanced"));
            sb.AppendLine($"{hmi.Screens.Count(s => s.Kind != "Template")} screens, {hmi.Tags.Count} HMI tags, {hmi.TextLists.Count} text lists"
                          + (hmi.Kind == "classic" ? ". The panel's own alarm list cannot be read from TIA; alarm texts come from the PLC program." : $", {hmi.Alarms.Count} alarms."));
            string Where(HmiField f)
            {
                var screen = hmi.Screens.FirstOrDefault(s => s.Name == f.Screen);
                return $"screen \"{f.Screen}\"" + (screen?.Number != null ? $" (no. {screen.Number})" : "") + (screen?.Kind == "Template" ? " (template, on every screen)" : "") + (screen?.Kind == "Popup" ? " (pop-up)" : "");
            }
            var settings = hmi.OperatorSettings().ToList();
            if (settings.Count > 0)
            {
                sb.AppendLine("Operator settings (values the operator types or switches on the panel): where > label on the screen -> PLC value");
                foreach (var f in settings)
                {
                    if (Full()) { sb.AppendLine("... more left out."); break; }
                    sb.AppendLine($"- {Where(f)} > \"{f.Label ?? f.Name}\" -> {Sym(f.PlcSymbol!)}" + (f.Unit != null ? $" [{f.Unit}]" : "")
                                  + (f.Kind != "IOField" ? $" ({f.Kind})" : "") + (f.TextList != null ? $", choices from text list {f.TextList}" : "")
                                  + (f.Authorization != null ? $", user group {f.Authorization}" : ""));
                }
            }
            var commands = hmi.OperatorCommands().ToList();
            if (commands.Count > 0 && !Full())
            {
                sb.AppendLine("Buttons and switches that change PLC values: where > text -> what it does");
                foreach (var f in commands.DistinctBy(f => (f.Screen, f.Label, f.PlcSymbol, f.Action)))
                {
                    if (Full()) { sb.AppendLine("... more left out."); break; }
                    sb.AppendLine($"- {Where(f)} > \"{f.Label ?? f.Name}\" -> {f.Action} {Sym(f.PlcSymbol!)}");
                }
            }
            // Values the panel shows as words: the text list says what each number means
            var shown = hmi.Screens.SelectMany(s => s.Fields).Where(f => f.TextList != null && f.PlcSymbol != null)
                .GroupBy(f => (f.TextList!, f.PlcSymbol!)).Select(g => g.Key).ToList();
            var lists = hmi.TextLists.ToDictionary(l => l.Name, StringComparer.OrdinalIgnoreCase);
            if (lists.Count > 0 && !Full())
            {
                sb.AppendLine("Text lists (value = text the operator sees):");
                var used = shown.Select(x => x.Item1).ToHashSet(StringComparer.OrdinalIgnoreCase);
                foreach (var l in hmi.TextLists.OrderBy(l => used.Contains(l.Name) ? 0 : 1))
                {
                    if (Full()) { sb.AppendLine("... more left out."); break; }
                    var on = shown.Where(x => x.Item1.Equals(l.Name, StringComparison.OrdinalIgnoreCase)).Select(x => Sym(x.Item2)).Distinct().ToList();
                    sb.AppendLine($"- {l.Name}" + (on.Count > 0 ? $" (shows {string.Join(", ", on.Take(4))})" : "") + ": "
                                  + string.Join("; ", l.Entries.Take(60).Select(e => $"{e.Range}={e.Text}")) + (l.Entries.Count > 60 ? "; ..." : ""));
                }
            }
            if (hmi.Alarms.Count > 0 && !Full())
            {
                sb.AppendLine("HMI alarms (id, text, trigger):");
                foreach (var a in hmi.Alarms)
                {
                    if (Full()) { sb.AppendLine("... more left out."); break; }
                    sb.AppendLine($"- {a.Id ?? a.Name}: {a.Text}" + (a.PlcSymbol != null ? $" <- {Sym(a.PlcSymbol)}" + (a.Bit != null ? $" bit {a.Bit}" : "") : a.Tag != null ? $" <- HMI tag {a.Tag}" : "")
                                  + (a.AlarmClass != null ? $" ({a.AlarmClass})" : ""));
                }
            }
        }
    }

    /// <summary>
    /// Only the part of the program that one model entry is about: the blocks named in <paramref name="words"/> or
    /// whose code uses them most (block names, DB names, member names), up to about <paramref name="maxChars"/>,
    /// and the readable tags that contain one of the words.
    /// </summary>
    public static string Focused(ProgramExport export, ExportPlc plc, IReadOnlyCollection<string> words, int maxChars = 120_000)
    {
        var keys = words.Where(w => w.Length >= 3).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var sb = new StringBuilder();
        sb.AppendLine($"Project: {export.Project}, PLC: {plc.Name}" + (plc.Cpu != null ? $", {plc.Cpu}" : ""));
        sb.AppendLine("Only the blocks that matter for this entry are listed.");
        sb.AppendLine();
        int Score(ExportBlock b)
        {
            var text = (b.SclSource ?? b.DecodedLogic ?? "") + " " + string.Join(" ", b.Interface.Select(m => m.Name));
            var score = keys.Count(k => b.Name.Equals(k, StringComparison.OrdinalIgnoreCase)) * 1000;
            foreach (var k in keys)
            {
                int i = 0, n = 0;
                while ((i = text.IndexOf(k, i, StringComparison.OrdinalIgnoreCase)) >= 0 && n < 50) { n++; i += k.Length; }
                score += n;
            }
            return score;
        }
        var ranked = plc.Blocks.Where(b => b.Kind is "OB" or "FB" or "FC" or "GlobalDB")
            .Select(b => (Block: b, Score: Score(b))).Where(x => x.Score > 0).OrderByDescending(x => x.Score).ToList();
        foreach (var (b, _) in ranked)
        {
            var one = new StringBuilder();
            WriteBlock(one, b);
            if (sb.Length + one.Length > maxChars && sb.Length > 1000) continue;
            sb.Append(one);
        }
        var tags = ProgramExport.Tags(plc).Where(t => t.ModelType != null && keys.Any(k => t.Symbol.Contains(k, StringComparison.OrdinalIgnoreCase))).Take(600).ToList();
        sb.AppendLine($"## Readable tags that match ({tags.Count}). Use these symbols exactly as written.");
        foreach (var t in tags)
            sb.AppendLine($"{t.Symbol} : {t.Datatype}" + (t.Comment != null ? $" // {t.Comment}" : ""));
        return sb.ToString();
    }

    private static void WriteCalls(StringBuilder sb, ExportPlc plc, ExportBlock b, string indent, int depth, HashSet<string> seen)
    {
        sb.AppendLine($"{indent}{b.Kind} {b.Name}" + (b.Number != null ? $" (no. {b.Number})" : "") + (b.Language != null ? $", {b.Language}" : ""));
        if (depth > 6 || !seen.Add(b.Name)) return;
        var calls = b.Calls.ToList();
        // Multi-instances declared in the interface are calls too, also when the call itself is in LAD/GRAPH
        foreach (var m in b.Interface.Where(m => m.Section is "Static" && m.UserType != null && plc.Block(m.UserType!)?.Kind == "FB"))
            if (!calls.Any(c => c.Instance == "#" + m.Name))
                calls.Add(new ExportCall("FB", m.UserType!, "#" + m.Name));
        foreach (var c in calls.DistinctBy(c => (c.Block, c.Instance)))
        {
            var target = plc.Block(c.Block);
            var label = c.Instance != null ? $"{c.Block} with instance {c.Instance}" : c.Block;
            if (target == null) { sb.AppendLine($"{indent}  calls {label} (system or library block)"); continue; }
            sb.Append($"{indent}  calls ");
            sb.AppendLine(label);
            if (target.Calls.Count > 0 || target.Interface.Any(m => m.UserType != null && plc.Block(m.UserType!)?.Kind == "FB"))
                WriteCalls(sb, plc, target, indent + "    ", depth + 1, seen);
        }
    }

    private static void WriteBlock(StringBuilder sb, ExportBlock b)
    {
        sb.Append($"### {b.Kind} {b.Name}");
        if (b.Number != null) sb.Append($" (no. {b.Number})");
        if (b.Language != null && b.Kind is not ("GlobalDB" or "InstanceDB")) sb.Append($", {b.Language}");
        if (b.InstanceOf != null) sb.Append($", instance of {b.InstanceOf}");
        sb.AppendLine();
        if (b.Title != null) sb.AppendLine($"Title: {b.Title}");
        if (b.Comment != null) sb.AppendLine($"Comment: {b.Comment}");

        if (b.Kind == "InstanceDB")
        {
            sb.AppendLine("(Members as in the FB interface; see the readable tags list for full symbols.)");
            sb.AppendLine();
            return;
        }
        if (b.Interface.Count > 0)
        {
            sb.AppendLine(b.Kind is "GlobalDB" or "UDT" ? "Members:" : "Interface:");
            foreach (var m in b.Interface) WriteMember(sb, m, "  ", b.Kind is not ("GlobalDB" or "UDT"));
        }
        var langs = b.NetworkLanguages.Distinct().ToList();
        if (b.SclSource != null)
        {
            sb.AppendLine("Source:");
            sb.AppendLine(Body(b.SclSource));
        }
        else if (b.DecodedLogic != null)
        {
            sb.AppendLine($"Logic (decoded from {string.Join(", ", langs)} networks into SCL-like text):");
            sb.AppendLine(b.DecodedLogic.Replace("\r\n", "\n").Trim());
        }
        else if (langs.Count > 0 && b.Kind is "OB" or "FB" or "FC")
            sb.AppendLine($"Networks: {string.Join(", ", langs)} (logic not included yet; use the interface comments and how the block is called).");
        sb.AppendLine();
    }

    private static void WriteMember(StringBuilder sb, ExportMember m, string indent, bool showSection)
    {
        var line = $"{indent}{(showSection ? m.Section + " " : "")}{m.Name} : {m.Datatype}";
        if (m.StartValue != null) line += $" := {m.StartValue}";
        if (m.Comment != null) line += $" // {m.Comment}";
        sb.AppendLine(line);
        // Struct members (not multi-instances, which are described by their own FB)
        // and not GRAPH runtime structures (step/transition internals), which only repeat the same fields
        if (m.Members.Count > 0 && m.UserType == null && !m.Datatype.StartsWith("G7_", StringComparison.Ordinal))
            foreach (var c in m.Members) WriteMember(sb, c, indent + "  ", false);
    }

    /// <summary>The SCL source without the interface part, which is already listed above.</summary>
    private static string Body(string scl)
    {
        var i = scl.IndexOf("\nBEGIN", StringComparison.OrdinalIgnoreCase);
        var body = i >= 0 ? scl[(i + 1)..] : scl;
        return body.Replace("\r\n", "\n").Trim();
    }
}
