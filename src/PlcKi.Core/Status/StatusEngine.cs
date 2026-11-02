using PlcKi.Core.Expressions;
using PlcKi.Core.Model;

namespace PlcKi.Core.Status;

/// <summary>One read of all model tags. Values are bool or double (times in ms), keyed by tag id.</summary>
public sealed class Snapshot
{
    public DateTimeOffset Time { get; init; } = DateTimeOffset.Now;
    public Dictionary<string, object?> Values { get; init; } = new();
    /// <summary>Tags that could not be read, with the reason.</summary>
    public Dictionary<string, string> Errors { get; init; } = new();
}

public sealed record PlcLinkInfo(string State, string Address, string? Error, DateTimeOffset? LastGoodRead, int TagCount, int TagErrors);

public sealed record ValueView(string Id, string Label, string Text, object? Raw);
public sealed record ConditionStatus(string Text, bool? Met, List<ValueView> Values);
public sealed record NextStatus(int To, string ToName, bool? Ready, List<ConditionStatus> Conditions);

public sealed record SequenceStatus(
    string Id, string Name, string? Block,
    int? Step, string StepName, string? Does, string? TimeInStep, string? Limit,
    bool Halted, List<string> HaltReasons, List<NextStatus> Next, string Explanation);

public sealed record EquipmentStatus(
    string Id, string Name, string? Kind, string? Location,
    string State, string Level, bool? Requested, bool? Commanded,
    List<ConditionStatus> Permits, List<ConditionStatus> Blocking, List<ValueView> Values, string Explanation);

public sealed record AlarmStatus(string Id, string Severity, string Text, string? Location, string? Condition, List<string> Causes, List<string> Fix);
public sealed record ExchangeStatus(string Partner, string Direction, string Label, string Value, string? Text);

public sealed record MachineStatus(
    DateTimeOffset Time, string Machine, PlcLinkInfo Connection,
    List<string> Summary, List<ValueView> Overview,
    List<SequenceStatus> Sequences, List<EquipmentStatus> Equipment,
    List<AlarmStatus> ActiveAlarms, List<ExchangeStatus> Exchange,
    Dictionary<string, string> TagErrors);

/// <summary>
/// Turns a snapshot of tag values into a plain-English picture of the machine,
/// using only the machine model. No PLC access here.
/// </summary>
public sealed class StatusEngine
{
    private readonly MachineModel _model;
    private readonly Dictionary<string, TagDef> _tags;
    private readonly Dictionary<string, Expr> _exprCache = new(StringComparer.Ordinal);

    public StatusEngine(MachineModel model)
    {
        _model = model;
        _tags = model.Tags.ToDictionary(t => t.Id, StringComparer.Ordinal);
    }

    public MachineModel Model => _model;

    public MachineStatus Evaluate(Snapshot snap, PlcLinkInfo connection)
    {
        object? Get(string id) => snap.Values.TryGetValue(id, out var v) ? v : null;
        bool hasValues = snap.Values.Count > 0;

        var sequences = _model.Sequences.Select(s => EvaluateSequence(s, Get)).ToList();
        var halted = sequences.Where(q => q.Halted).Select(q => q.Name).ToList();
        var equipment = _model.Equipment.Select(e => EvaluateEquipment(e, Get, halted)).ToList();
        var alarms = _model.Alarms
            .Where(a => Get(a.Tag) is true)
            .OrderBy(a => a.Severity == "FAULT" ? 0 : 1).ThenBy(a => a.Id, StringComparer.Ordinal)
            .Select(a => new AlarmStatus(a.Id, a.Severity, Fill(a.Text, Get), a.Location,
                a.Condition != null ? Fill(a.Condition, Get) : null,
                a.Causes.Select(c => Fill(c, Get)).ToList(), a.Fix.Select(f => Fill(f, Get)).ToList()))
            .ToList();
        var exchange = _model.Exchange
            .Select(x => new ExchangeStatus(x.Partner, x.Direction, Label(x.Tag), Fmt(x.Tag, Get), x.Text))
            .ToList();
        var overview = _model.Overview.Select(id => View(id, Get)).ToList();

        var summary = new List<string>();
        if (connection.State != "connected" && connection.State != "snapshot")
        {
            summary.Add(connection.Error is { Length: > 0 }
                ? $"Not connected to the PLC at {connection.Address}: {connection.Error}"
                : $"Not connected to the PLC at {connection.Address} ({connection.State}).");
            if (hasValues) summary.Add($"Values below are from the last good read at {connection.LastGoodRead:HH:mm:ss}.");
        }
        if (hasValues)
        {
            summary.AddRange(sequences.Select(s => s.Explanation));
            if (alarms.Count == 0) summary.Add("No alarms are active.");
            else
            {
                var faults = alarms.Count(a => a.Severity == "FAULT");
                var head = alarms.Count == 1 ? "1 alarm is active" : $"{alarms.Count} alarms are active";
                if (faults > 0) head += faults == alarms.Count ? "" : $" ({faults} fault{(faults == 1 ? "" : "s")})";
                summary.Add($"{head}: " + string.Join("; ", alarms.Select(a => $"{a.Id} {a.Text}")) + ".");
            }
            var blocked = equipment.Where(e => e.Requested == true && e.Commanded == false).ToList();
            foreach (var e in blocked) summary.Add(e.Explanation);
            var active = equipment.Where(e => e.Commanded == true && e.Level is "ok" or "warn").Select(e => $"{e.Name}: {e.State}").ToList();
            if (active.Count > 0) summary.Add("Active now: " + string.Join("; ", active) + ".");
        }
        if (snap.Errors.Count > 0)
            summary.Add($"{snap.Errors.Count} tag(s) could not be read, see the tag list.");

        return new MachineStatus(snap.Time, _model.Name, connection, summary, overview, sequences, equipment, alarms, exchange,
            new Dictionary<string, string>(snap.Errors));
    }

    private SequenceStatus EvaluateSequence(SequenceDef s, Func<string, object?> get)
    {
        var stepVal = get(s.StepTag) as double?;
        int? stepNo = stepVal is double d ? (int)Math.Round(d) : null;
        var step = stepNo is int n ? s.Steps.FirstOrDefault(x => x.No == n) : null;
        var stepName = step?.Name ?? (stepNo is int u ? $"unknown step {u}" : "unknown");
        string? timeInStep = s.StepTimeTag != null && get(s.StepTimeTag) is double ms ? ValueFormat.Duration(ms) : null;
        bool halted = s.HaltedTag != null && get(s.HaltedTag) is true;

        var reasons = new List<string>();
        if (halted)
            foreach (var h in s.HaltReasons)
                if (EvalBool(h.Expr, get) == true) reasons.Add(Fill(h.Text, get));

        var next = new List<NextStatus>();
        if (step != null)
        {
            foreach (var tr in step.Next)
            {
                var conds = tr.Conditions.Select(c => EvaluateCondition(c, get)).ToList();
                bool? ready = conds.Any(c => c.Met == false) ? false : conds.Any(c => c.Met == null) ? null : true;
                var toName = s.Steps.FirstOrDefault(x => x.No == tr.To)?.Name ?? tr.To.ToString();
                // "NOT Halted" in every transition is true to the program but only noise on screen: the halt and its
                // reasons are shown on their own. It still counts for "ready".
                var shown = tr.Conditions.Zip(conds).Where(p => !IsNotHalted(p.First.Expr, s.HaltedTag)).Select(p => p.Second).ToList();
                next.Add(new NextStatus(tr.To, toName, ready, shown));
            }
        }

        // Plain-English explanation
        var parts = new List<string>();
        if (stepNo == null) parts.Add($"{s.Name}: step unknown (tag not read).");
        else
        {
            parts.Add($"{s.Name} is in step {stepNo} \"{stepName}\"" + (timeInStep != null ? $" for {timeInStep}." : "."));
            if (step?.Does != null)
                parts.Add((halted ? "Normally it " : "It ") + LowerFirst(Fill(step.Does, get)));
            if (halted)
                parts.Add(reasons.Count > 0
                    ? "But it is halted (outputs off) because " + JoinAnd(reasons) + "."
                    : "But it is halted (outputs off).");
            else if (next.Count > 0)
            {
                var tr = next[0];
                var missing = tr.Conditions.Where(c => c.Met != true).ToList();
                if (missing.Count == 0) parts.Add($"Everything is in place to move on to step {tr.To} \"{tr.ToName}\".");
                else parts.Add($"To move on to step {tr.To} \"{tr.ToName}\" it is waiting for: " +
                               string.Join("; ", missing.Select(DescribeCondition)) + ".");
            }
        }

        return new SequenceStatus(s.Id, s.Name, s.Block, stepNo, stepName, step?.Does != null ? Fill(step.Does, get) : null,
            timeInStep, step?.Limit != null ? Fill(step.Limit, get) : null, halted, reasons, next, string.Join(" ", parts));
    }

    /// <param name="haltedSequences">Names of the sequences that are halted now: their step outputs are off, so a device
    /// that is not requested may only be off because of the halt.</param>
    private EquipmentStatus EvaluateEquipment(EquipmentDef e, Func<string, object?> get, IReadOnlyList<string> haltedSequences)
    {
        bool? requested = e.Request != null ? EvalBool(e.Request, get) : null;
        bool? commanded = e.Command != null ? EvalBool(e.Command, get) : null;
        var permits = e.Permits.Select(p => EvaluateCondition(p, get)).ToList();
        var blocking = permits.Where(p => p.Met != true).ToList();

        string state = "unknown";
        string level = "info";
        foreach (var st in e.States)
        {
            if (EvalBool(st.When, get) == true)
            {
                state = Fill(st.Text, get);
                level = st.Level;
                break;
            }
        }

        string explanation;
        if (requested == true && commanded == false)
            explanation = blocking.Count > 0
                ? $"{e.Name} is requested by the program but blocked by: " + string.Join("; ", blocking.Select(DescribeCondition)) + "."
                : $"{e.Name} is requested by the program but its output is off.";
        else if (requested == false && commanded != true)
            explanation = haltedSequences.Count > 0
                ? $"{e.Name}: {state}. Not requested now: {JoinAnd(haltedSequences.ToList())} {(haltedSequences.Count == 1 ? "is" : "are")} halted, which switches the step outputs off."
                : $"{e.Name}: {state}. Not requested in the current step.";
        else
            explanation = $"{e.Name}: {state}.";

        var values = e.Values.Select(v => View(v, get)).ToList();
        return new EquipmentStatus(e.Id, e.Name, e.Kind, e.Location, state, level, requested, commanded, permits, blocking, values, explanation);
    }

    private ConditionStatus EvaluateCondition(CondDef c, Func<string, object?> get)
    {
        var expr = Parse(c.Expr);
        bool? met;
        try { met = expr.EvalBool(get); }
        catch (ExprException) { met = null; }
        // Show the live values the condition depends on, except those already written into its text
        var inText = TextTemplate.Placeholders(c.Text).ToHashSet(StringComparer.Ordinal);
        var values = expr.Identifiers().Where(id => _tags.ContainsKey(id) && !inText.Contains(id)).Select(id => View(id, get)).ToList();
        return new ConditionStatus(Fill(c.Text, get), met, values);
    }

    private static string DescribeCondition(ConditionStatus c)
    {
        var now = c.Values.Count > 0 ? " (now " + string.Join(", ", c.Values.Select(v => $"{v.Label} {v.Text}")) + ")" : "";
        var unknown = c.Met == null ? " [unknown]" : "";
        return c.Text + now + unknown;
    }

    private bool? EvalBool(string expr, Func<string, object?> get)
    {
        try { return Parse(expr).EvalBool(get); }
        catch (ExprException) { return null; }
    }

    private Expr Parse(string expr)
    {
        lock (_exprCache)
        {
            if (!_exprCache.TryGetValue(expr, out var e))
            {
                e = ExprParser.Parse(expr);
                _exprCache[expr] = e;
            }
            return e;
        }
    }

    private string Label(string id) => _tags.TryGetValue(id, out var t) ? t.Label ?? id : id;
    private string Fmt(string id, Func<string, object?> get) => ValueFormat.Format(_tags.GetValueOrDefault(id), get(id));
    private ValueView View(string id, Func<string, object?> get) => new(id, Label(id), Fmt(id, get), get(id));
    private string Fill(string text, Func<string, object?> get) => TextTemplate.Fill(text, id => Fmt(id, get));

    private static string LowerFirst(string s) =>
        s.Length > 1 && char.IsUpper(s[0]) && !char.IsUpper(s[1]) ? char.ToLowerInvariant(s[0]) + s[1..] : s;

    private static bool IsNotHalted(string expr, string? haltedTag)
    {
        if (haltedTag == null) return false;
        var e = string.Concat(expr.Where(c => !char.IsWhiteSpace(c)));
        return e.Equals("NOT" + haltedTag, StringComparison.OrdinalIgnoreCase)
            || e.Equals("NOT(" + haltedTag + ")", StringComparison.OrdinalIgnoreCase);
    }

    private static string JoinAnd(List<string> items) => items.Count switch
    {
        0 => "",
        1 => items[0],
        _ => string.Join(", ", items.Take(items.Count - 1)) + " and " + items[^1],
    };
}
