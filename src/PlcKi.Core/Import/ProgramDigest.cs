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
        sb.AppendLine($"PLC: {plc.Name}" + (plc.Cpu != null ? $", {plc.Cpu}" : "") + (plc.IpAddresses.Count > 0 ? $", IP {string.Join(" / ", plc.IpAddresses)}" : "")
                      + (plc.AmsNetId != null ? $", AMS Net ID {plc.AmsNetId}" : ""));
        sb.AppendLine($"Brand: {PlcBrands.Describe(plc.Brand)}");
        if (BrandNotes(plc.Brand) is { } notes) sb.AppendLine(notes);
        var problems = export.Problems.Concat(plc.Problems).ToList();
        if (problems.Count > 0) sb.AppendLine("Export problems: " + string.Join("; ", problems));
        sb.AppendLine();

        sb.AppendLine("## Call structure");
        foreach (var ob in plc.Blocks.Where(b => b.Kind is "OB" or "Task").OrderBy(b => b.Number).ThenBy(b => b.Name, StringComparer.Ordinal))
            WriteCalls(sb, plc, ob, "", 0, new HashSet<string>());
        sb.AppendLine();

        sb.AppendLine("## Blocks");
        var order = new[] { "OB", "Task", "PRG", "FB", "FC", "GlobalDB", "GVL", "InstanceDB", "ArrayDB", "UDT", "ENUM" };
        foreach (var b in plc.Blocks.Concat(plc.Types).OrderBy(b => Array.IndexOf(order, b.Kind)).ThenBy(b => b.Name, StringComparer.Ordinal))
            WriteBlock(sb, b);

        var tags = ProgramExport.Tags(plc).Where(t => t.ModelType != null).ToList();
        sb.AppendLine($"## Readable tags ({tags.Count}). Use these symbols exactly as written.");
        foreach (var t in tags.Take(maxTags))
            sb.AppendLine($"{t.Symbol} : {t.Datatype}" + (t.Comment != null ? $" // {t.Comment}" : ""));
        if (tags.Count > maxTags) sb.AppendLine($"... {tags.Count - maxTags} more left out.");
        return sb.ToString();
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
        var ranked = plc.Blocks.Where(b => b.Kind is "OB" or "FB" or "FC" or "GlobalDB" or "PRG" or "GVL")
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
            if (!calls.Any(c => c.Instance == "#" + m.Name || c.Instance == m.Name))
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
        if (b.InstanceOf != null) sb.Append(b.Kind == "InstanceDB" ? $", instance of {b.InstanceOf}" : $", extends {b.InstanceOf}");
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
            sb.AppendLine(b.Kind is "GlobalDB" or "UDT" or "GVL" ? "Members:" : b.Kind == "ENUM" ? "Values:" : "Interface:");
            foreach (var m in b.Interface) WriteMember(sb, m, "  ", b.Kind is not ("GlobalDB" or "UDT" or "GVL" or "ENUM"));
        }
        var langs = b.NetworkLanguages.Distinct().ToList();
        if (!string.IsNullOrWhiteSpace(b.SclSource))
        {
            sb.AppendLine("Source:");
            sb.AppendLine(Body(b.SclSource));
        }
        else if (b.DecodedLogic != null)
        {
            sb.AppendLine($"Logic (decoded from {string.Join(", ", langs)} networks into SCL-like text):");
            sb.AppendLine(b.DecodedLogic.Replace("\r\n", "\n").Trim());
        }
        else if (langs.Count > 0 && b.Kind is "OB" or "FB" or "FC" or "PRG")
            sb.AppendLine($"Networks: {string.Join(", ", langs)} (logic not included yet; use the interface comments and how the block is called).");
        sb.AppendLine();
    }

    private static void WriteMember(StringBuilder sb, ExportMember m, string indent, bool showSection)
    {
        var line = $"{indent}{(showSection ? m.Section + " " : "")}{m.Name}" + (m.Address != null ? $" AT {m.Address}" : "") + (m.Datatype.Length > 0 ? $" : {m.Datatype}" : "");
        if (m.StartValue != null) line += $" := {m.StartValue}";
        if (m.Comment != null) line += $" // {m.Comment}";
        sb.AppendLine(line);
        // Struct members (not multi-instances, which are described by their own FB)
        // and not GRAPH runtime structures (step/transition internals), which only repeat the same fields
        if (m.Members.Count > 0 && m.UserType == null && !m.Datatype.StartsWith("G7_", StringComparison.Ordinal))
            foreach (var c in m.Members) WriteMember(sb, c, indent + "  ", false);
    }

    /// <summary>What Claude needs to know to read a program of this brand; null for Siemens (the import prompt's default).</summary>
    public static string? BrandNotes(string brand) => brand switch
    {
        PlcBrands.Beckhoff => """
            Notes for this brand:
            - The program is IEC 61131-3 Structured Text, read it like SCL. Tasks call PROGRAMs; there are no OBs or data blocks.
              PROGRAM (PRG) variables and global variable lists (GVL) hold the data; function block instances live inside them.
            - Symbols are TwinCAT ADS paths without quotes: MAIN.fbPump.bRunning, GVL_IO.bStartButton, MAIN.aValves[2].bOpen.
            - "AT %I*" / "AT %Q*" variables are linked to EtherCAT terminals in the TwinCAT I/O tree: they are the field inputs and outputs.
            - Only Structured Text is included as source. SFC, LD, FBD and CFC bodies are named by language but not decoded yet:
              use the interface comments and how the block is called for those.
            - Enum values are read as their integer number; the enum lists each name with its number.
            """,
        _ => null,
    };

    /// <summary>The SCL source without the interface part, which is already listed above.</summary>
    private static string Body(string scl)
    {
        var i = scl.IndexOf("\nBEGIN", StringComparison.OrdinalIgnoreCase);
        var body = i >= 0 ? scl[(i + 1)..] : scl;
        return body.Replace("\r\n", "\n").Trim();
    }
}
