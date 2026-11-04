using PlcKi.Core.Model;

namespace PlcKi.Core.Import;

/// <summary>
/// Checks a machine model against the program it claims to describe: every tag symbol must exist in the export
/// and have a type PLC-Ki can read. Tag types are taken from the export, not from the model, because the export
/// is the truth. Then runs the model's own validation (ids, expressions, placeholders).
/// </summary>
public static class ModelCheck
{
    /// <returns>The problems found; empty when the model fits the program.</returns>
    public static List<string> Check(MachineModel model, ExportPlc plc)
    {
        var problems = new List<string>();
        var known = new Dictionary<string, ExportTag>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in ProgramExport.Tags(plc)) known.TryAdd(ProgramExport.NormalizeSymbol(t.Symbol), t);

        foreach (var tag in model.Tags)
        {
            if (string.IsNullOrWhiteSpace(tag.Symbol)) continue;   // Validate reports it
            if (!known.TryGetValue(ProgramExport.NormalizeSymbol(tag.Symbol), out var real))
            {
                var hint = Closest(tag.Symbol, known.Values.Select(v => v.Symbol));
                problems.Add($"Tag '{tag.Id}': symbol {tag.Symbol} does not exist in the program" + (hint != null ? $" (did you mean {hint}?)" : "") + ".");
                continue;
            }
            if (real.ModelType == null)
            {
                problems.Add($"Tag '{tag.Id}': {real.Symbol} is {real.Datatype}, which PLC-Ki cannot read. Use an elementary member instead.");
                continue;
            }
            tag.Symbol = real.Symbol;
            tag.Type = real.ModelType;
        }

        problems.AddRange(CheckTransitions(model, plc));

        try { model.Validate(); }
        catch (InvalidDataException ex)
        {
            problems.AddRange(ex.Message.Split('\n').Skip(1).Select(l => l.Trim()).Where(l => l.Length > 0));
        }
        if (model.Tags.Count == 0) problems.Add("The model has no tags.");
        return problems;
    }

    /// <summary>
    /// For sequences run by a GRAPH block: the step changes the block's connections allow (branches and jumps
    /// followed) against the ones in the model. A missing one makes the live page show a step change it cannot explain.
    /// </summary>
    public static List<string> CheckTransitions(MachineModel model, ExportPlc plc)
    {
        var problems = new List<string>();
        var symbols = model.Tags.ToDictionary(t => t.Id, t => t.Symbol);
        foreach (var seq in model.Sequences)
        {
            var graph = GraphBlockOf(seq, symbols.GetValueOrDefault(seq.StepTag), plc);
            if (graph == null || graph.GraphTransitions.Count == 0) continue;
            var inProgram = graph.GraphTransitions.ToHashSet();
            var inModel = seq.Steps.SelectMany(s => s.Next.Select(n => (s.No, n.To))).ToHashSet();
            var missing = inProgram.Where(e => !inModel.Contains(e)).OrderBy(e => e.From).ThenBy(e => e.To).ToList();
            var extra = inModel.Where(e => !inProgram.Contains(e)).OrderBy(e => e.Item1).ThenBy(e => e.Item2).ToList();
            static string List(IEnumerable<(int, int)> es) => string.Join(", ", es.Take(30).Select(e => $"{e.Item1} -> {e.Item2}"));
            if (missing.Count > 0)
                problems.Add($"Sequence '{seq.Id}': {graph.Name} has {missing.Count} step change{(missing.Count == 1 ? "" : "s")} the model is missing: {List(missing)}{(missing.Count > 30 ? ", ..." : "")}. " +
                    "Add each one (and any missing step) with the condition of its transition, as listed in the block's 'Transition' lines.");
            if (extra.Count > 0)
                problems.Add($"Sequence '{seq.Id}': the model has step change{(extra.Count == 1 ? "" : "s")} that {graph.Name} does not have: {List(extra)}. Remove or correct them.");
        }
        return problems;
    }

    /// <summary>The GRAPH FB behind a sequence whose step tag is GRAPH's S_NO: the FB of that instance DB, else the GRAPH FB named in its block text.</summary>
    private static ExportBlock? GraphBlockOf(SequenceDef seq, string? stepSymbol, ExportPlc plc)
    {
        static bool IsGraph(ExportBlock? b) => b != null && (b.Language == "GRAPH" || b.NetworkLanguages.Contains("GRAPH"));
        // Only when the model reads GRAPH's own step number (S_NO): a step tag the program fills itself may number the steps differently
        if (stepSymbol == null || !stepSymbol.TrimEnd().EndsWith(".S_NO", StringComparison.OrdinalIgnoreCase)) return null;
        {
            var norm = ProgramExport.NormalizeSymbol(stepSymbol);
            var db = norm.StartsWith('"') && norm.IndexOf('"', 1) > 0 ? norm[1..norm.IndexOf('"', 1)] : norm.Split('.')[0];
            if (plc.Block(db) is { InstanceOf: { } fb } && plc.Block(fb) is { } b && IsGraph(b)) return b;
        }
        if (seq.Block != null)
            return plc.Blocks.Where(IsGraph).OrderByDescending(b => b.Name.Length)
                .FirstOrDefault(b => seq.Block.Contains(b.Name, StringComparison.OrdinalIgnoreCase));
        return null;
    }

    /// <summary>The known symbol that shares the longest tail with the given one, as a hint for a typo.</summary>
    private static string? Closest(string symbol, IEnumerable<string> candidates)
    {
        var parts = ProgramExport.NormalizeSymbol(symbol).Split('.');
        var last = parts[^1];
        var same = candidates.Where(c => c.EndsWith("." + last, StringComparison.OrdinalIgnoreCase)).ToList();
        if (same.Count == 0) return null;
        return same.OrderByDescending(c => CommonTail(ProgramExport.NormalizeSymbol(c).Split('.'), parts)).First();
    }

    private static int CommonTail(string[] a, string[] b)
    {
        int n = 0;
        while (n < a.Length && n < b.Length && string.Equals(a[^(n + 1)], b[^(n + 1)], StringComparison.OrdinalIgnoreCase)) n++;
        return n;
    }
}

/// <summary>
/// Compares a generated model with a reference one (for the demo: the hand-written model), by what they read
/// and what they describe. Used to measure the model builder, not at run time.
/// </summary>
public static class ModelCompare
{
    public sealed record Result(List<string> Lines, double TagCoverage, double AlarmCoverage, int ReferenceSteps, int MatchedSteps, int ReferenceDevices, int MatchedDevices);

    public static Result Compare(MachineModel generated, MachineModel reference)
    {
        var lines = new List<string>();
        var genSymbols = generated.Tags.Select(t => ProgramExport.NormalizeSymbol(t.Symbol)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var refSymbols = reference.Tags.Select(t => ProgramExport.NormalizeSymbol(t.Symbol)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var tagHit = refSymbols.Count(genSymbols.Contains);
        double tagCov = refSymbols.Count == 0 ? 1 : tagHit / (double)refSymbols.Count;
        lines.Add($"Tags: generated {generated.Tags.Count}, reference {reference.Tags.Count}; {tagHit} of the reference symbols are read ({tagCov:P0}).");
        var missingTags = reference.Tags.Where(t => !genSymbols.Contains(ProgramExport.NormalizeSymbol(t.Symbol))).Select(t => t.Symbol).ToList();
        if (missingTags.Count > 0) lines.Add("  Not read: " + string.Join(", ", missingTags.Take(20)) + (missingTags.Count > 20 ? ", ..." : ""));

        string SymbolOf(MachineModel m, string id) =>
            m.Tags.FirstOrDefault(t => t.Id == id) is { } t ? ProgramExport.NormalizeSymbol(t.Symbol) : id;
        var genAlarms = generated.Alarms.Select(a => SymbolOf(generated, a.Tag)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var alarmHit = reference.Alarms.Count(a => genAlarms.Contains(SymbolOf(reference, a.Tag)));
        double alarmCov = reference.Alarms.Count == 0 ? 1 : alarmHit / (double)reference.Alarms.Count;
        lines.Add($"Alarms: generated {generated.Alarms.Count}, reference {reference.Alarms.Count}; {alarmHit} matched by alarm bit ({alarmCov:P0}).");
        var genAlarmWithFix = generated.Alarms.Count(a => a.Fix.Count > 0 && a.Causes.Count > 0);
        lines.Add($"  With causes and fix: {genAlarmWithFix} of {generated.Alarms.Count}.");

        int refSteps = 0, matchedSteps = 0;
        foreach (var rs in reference.Sequences)
        {
            var gs = generated.Sequences.FirstOrDefault(s => SymbolOf(generated, s.StepTag) == SymbolOf(reference, rs.StepTag));
            refSteps += rs.Steps.Count;
            if (gs == null) { lines.Add($"Sequence '{rs.Name}': not found (no sequence uses its step tag)."); continue; }
            var gNos = gs.Steps.Select(s => s.No).ToHashSet();
            var hit = rs.Steps.Count(s => gNos.Contains(s.No));
            matchedSteps += hit;
            var refEdges = rs.Steps.SelectMany(s => s.Next.Select(n => (s.No, n.To))).ToHashSet();
            var genEdges = gs.Steps.SelectMany(s => s.Next.Select(n => (s.No, n.To))).ToHashSet();
            lines.Add($"Sequence '{rs.Name}': {hit} of {rs.Steps.Count} steps found, {refEdges.Count(genEdges.Contains)} of {refEdges.Count} transitions; " +
                      $"halted tag {(gs.HaltedTag != null && SymbolOf(generated, gs.HaltedTag) == SymbolOf(reference, rs.HaltedTag ?? "") ? "matches" : "differs")}.");
        }

        int matchedDev = 0;
        foreach (var rd in reference.Equipment)
        {
            var refCmd = rd.Command != null ? SymbolOf(reference, rd.Command) : null;
            var gd = generated.Equipment.FirstOrDefault(d => string.Equals(d.Id, rd.Id, StringComparison.OrdinalIgnoreCase))
                     ?? generated.Equipment.FirstOrDefault(d => d.Command != null && refCmd != null && SymbolOf(generated, d.Command) == refCmd);
            if (gd == null) { lines.Add($"Device {rd.Id}: not found."); continue; }
            matchedDev++;
            lines.Add($"Device {rd.Id}: found as {gd.Id}, {gd.Permits.Count} permits (reference {rd.Permits.Count}), {gd.States.Count} states (reference {rd.States.Count}).");
        }
        lines.Add($"Exchange signals: generated {generated.Exchange.Count}, reference {reference.Exchange.Count}.");
        return new Result(lines, tagCov, alarmCov, refSteps, matchedSteps, reference.Equipment.Count, matchedDev);
    }
}
