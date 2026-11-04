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
    List<ConditionStatus> Permits, List<ConditionStatus> Blocking, List<ValueView> Values, string Explanation,
    string? BlockedBy = null);

/// <param name="Since">When PLC-Ki first saw the alarm on (server time).</param>
/// <param name="AtStart">The alarm was already on at the first read, so when it really came is not known.</param>
public sealed record AlarmStatus(string Id, string Severity, string Text, string? Location, string? Condition, List<string> Causes, List<string> Fix,
    DateTimeOffset? Since = null, bool AtStart = false, List<ConditionStatus>? Checks = null, bool ChecksAtAlarm = false);
/// <param name="Valid">FALSE when the value keeps the machine from working (see Problem); null when the model has no rule.</param>
public sealed record SettingStatus(string Id, string Label, string Value, string? Text, bool? Valid, string? Problem);
public sealed record ExchangeStatus(string Partner, string Direction, string Label, string Value, string? Text);

public sealed record MachineStatus(
    DateTimeOffset Time, string Machine, PlcLinkInfo Connection,
    List<string> Summary, List<ValueView> Overview,
    List<SequenceStatus> Sequences, List<EquipmentStatus> Equipment,
    List<AlarmStatus> ActiveAlarms, List<ExchangeStatus> Exchange,
    Dictionary<string, string> TagErrors, List<SettingStatus>? Settings = null)
{
    /// <summary>The raw values of this read, by tag id, for machine memory (not sent to the pages).</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public IReadOnlyDictionary<string, object?>? Raw { get; init; }
}

/// <summary>
/// Turns a snapshot of tag values into a plain-English picture of the machine,
/// using only the machine model. No PLC access here.
/// </summary>
public sealed class StatusEngine
{
    private readonly MachineModel _model;
    private readonly Dictionary<string, TagDef> _tags;
    private readonly Dictionary<string, Expr> _exprCache = new(StringComparer.Ordinal);
    // When each active alarm came, so the page can show the first-out first. Kept while the alarm stays on.
    private readonly Dictionary<string, (DateTimeOffset Since, bool AtStart)> _alarmSince = new(StringComparer.Ordinal);
    private bool _hadValues;
    // The values of the last read before each alarm came, for its checks: by the time someone looks, the machine has
    // often moved on (the robot went home), so the values now would not show what failed
    private readonly Dictionary<string, Dictionary<string, object?>> _alarmValues = new(StringComparer.Ordinal);
    private Dictionary<string, object?>? _prevValues;

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
        var on = _model.Alarms.Where(a => Get(a.Tag) is true).ToList();
        var since = TrackAlarms(on.Select(a => a.Id), snap.Time, hasValues);
        // First-out first: by when each alarm came. Alarms that were already on at the first read have no known
        // order between them, so those come first (they are older than anything seen coming), faults before warnings.
        var alarms = on
            .Select(a => (Def: a, When: since.TryGetValue(a.Id, out var w) ? w : (Since: snap.Time, AtStart: true)))
            .OrderBy(x => x.When.AtStart ? 0 : 1)
            .ThenBy(x => x.When.AtStart ? DateTimeOffset.MinValue : x.When.Since)
            .ThenBy(x => x.Def.Severity == "FAULT" ? 0 : 1)
            .ThenBy(x => x.Def.Id, NaturalComparer.Instance)
            .Select(x => new AlarmStatus(x.Def.Id, x.Def.Severity, Fill(x.Def.Text, Get), x.Def.Location,
                x.Def.Condition != null ? Fill(x.Def.Condition, Get) : null,
                x.Def.Causes.Select(c => Fill(c, Get)).ToList(), x.Def.Fix.Select(f => Fill(f, Get)).ToList(),
                x.When.Since, x.When.AtStart, ChecksOf(x.Def, x.When.AtStart, snap, out var atAlarm), atAlarm))
            .ToList();
        if (hasValues) _prevValues = new Dictionary<string, object?>(snap.Values, StringComparer.Ordinal);
        var settings = _model.Settings.Select(st => new SettingStatus(st.Tag, Label(st.Tag), Fmt(st.Tag, Get),
                st.Text != null ? Fill(st.Text, Get) : null,
                st.Valid != null && hasValues ? EvalBool(st.Valid, Get) : null,
                st.Problem != null ? Fill(st.Problem, Get) : null))
            .ToList();
        var exchange = _model.Exchange
            .Select(x => new ExchangeStatus(x.Partner, x.Direction, Label(x.Tag), Fmt(x.Tag, Get), x.Text))
            .ToList();
        var overview = _model.Overview.Select(id => View(id, Get)).ToList();

        var summary = new List<string>();
        if (connection.State != "connected" && connection.State != "snapshot")
        {
            summary.Add(connection.State == "notset" ? "No PLC is set yet, so there are no live values."
                : connection.Error is { Length: > 0 }
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
            var blocked = equipment.Where(e => e.BlockedBy != null).ToList();
            foreach (var e in blocked) summary.Add(e.Explanation);
            foreach (var st in settings.Where(st => st.Valid == false))
                summary.Add($"Setting {st.Label} is {st.Value}: " + (st.Problem ?? "the machine cannot work with this value") + ".");
            var active = equipment.Where(e => e.Commanded == true && e.Level is "ok" or "warn").Select(e => $"{e.Name}: {e.State}").ToList();
            if (active.Count > 0) summary.Add("Active now: " + string.Join("; ", active) + ".");
        }
        if (snap.Errors.Count > 0)
            summary.Add($"{snap.Errors.Count} tag(s) could not be read, see the tag list.");

        return new MachineStatus(snap.Time, _model.Name, connection, summary, overview, sequences, equipment, alarms, exchange,
            new Dictionary<string, string>(snap.Errors), settings) { Raw = new Dictionary<string, object?>(snap.Values) };
    }

    /// <summary>An alarm's checks, on the values of the read before it came (kept while it stays on). An alarm that was
    /// already on at the first read has no such values: then the values now.</summary>
    private List<ConditionStatus> ChecksOf(AlarmDef a, bool atStart, Snapshot snap, out bool atAlarm)
    {
        atAlarm = false;
        if (a.Checks.Count == 0) return new();
        Dictionary<string, object?>? values;
        lock (_alarmValues)
        {
            foreach (var id in _alarmValues.Keys.Where(k => !_alarmSince.ContainsKey(k)).ToList()) _alarmValues.Remove(id);
            if (!_alarmValues.TryGetValue(a.Id, out values) && !atStart && _prevValues != null)
                _alarmValues[a.Id] = values = _prevValues;
        }
        if (values == null) return a.Checks.Select(c => EvaluateCondition(c, id => snap.Values.TryGetValue(id, out var v) ? v : null)).ToList();
        atAlarm = true;
        return a.Checks.Select(c => EvaluateCondition(c, id => values.TryGetValue(id, out var v) ? v : null)).ToList();
    }

    /// <summary>Remembers when each alarm came on. Only reads with values count: before the first one nothing is known.</summary>
    private Dictionary<string, (DateTimeOffset Since, bool AtStart)> TrackAlarms(IEnumerable<string> activeIds, DateTimeOffset time, bool hasValues)
    {
        lock (_alarmSince)
        {
            if (hasValues)
            {
                var active = activeIds.ToHashSet(StringComparer.Ordinal);
                foreach (var id in _alarmSince.Keys.Where(k => !active.Contains(k)).ToList()) _alarmSince.Remove(id);
                foreach (var id in active)
                    if (!_alarmSince.ContainsKey(id)) _alarmSince[id] = (time, !_hadValues);
                _hadValues = true;
            }
            return new Dictionary<string, (DateTimeOffset Since, bool AtStart)>(_alarmSince, StringComparer.Ordinal);
        }
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
                parts.Add(halted ? "Normally it " + LowerFirst(Fill(step.Does, get)) : ShouldDo(Fill(step.Does, get)));
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
        if (requested == true && commanded == false && blocking.Count > 0)
            explanation = $"{e.Name} is requested by the program but blocked by: " + string.Join("; ", blocking.Select(DescribeCondition)) + ".";
        else if (requested == true && commanded == false)
            explanation = $"{e.Name}: {state}. Its output is off now, but every interlock is met.";
        else if (requested == false && commanded != true)
            explanation = haltedSequences.Count > 0
                ? $"{e.Name}: {state}. Not requested now: {JoinAnd(haltedSequences.ToList())} {(haltedSequences.Count == 1 ? "is" : "are")} halted, which switches the step outputs off."
                : $"{e.Name}: {state}. Not requested in the current step.";
        else
            explanation = $"{e.Name}: {state}.";

        // Blocked: requested, output off, and an interlock is missing. With every interlock met the output is off
        // for another reason (a pulsed start bit, a held job number or setpoint), so it is not shown as blocked.
        string? blockedBy = requested == true && commanded == false && blocking.Count > 0
            ? "needs " + JoinAnd(blocking.Select(NeedText).ToList())
            : null;

        var values = e.Values.Select(v => View(v, get)).ToList();
        return new EquipmentStatus(e.Id, e.Name, e.Kind, e.Location, state, level, requested, commanded, permits, blocking, values, explanation, blockedBy);
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

    /// <summary>"tank level above 20 % (blades covered), now Level 15 %" for a condition that is not met.</summary>
    private static string NeedText(ConditionStatus c)
    {
        var text = LowerFirst(c.Text);
        if (c.Met == null) return text + " (no live value to check)";
        return c.Values.Count > 0 ? text + ", now " + string.Join(", ", c.Values.Select(v => $"{v.Label} {v.Text}")) : text;
    }

    /// <summary>"Runs agitator M201 at 1200 rpm" -> "In this step it should run agitator M201 at 1200 rpm."</summary>
    public static string ShouldDo(string does)
    {
        var d = does.Trim().TrimEnd('.');
        var sp = d.IndexOf(' ');
        var first = sp > 0 ? d[..sp] : d;
        var rest = sp > 0 ? d[sp..] : "";
        var verb = BaseVerb(first);
        if (verb == null) return $"What this step does: {does.Trim().TrimEnd('.')}.";
        // "... and heats with E301": later verbs of the same sentence, but only well-known ones (never "and valves")
        rest = System.Text.RegularExpressions.Regex.Replace(rest, @"\b(and|then|or)\s+([a-z]+s)\b", m =>
            CommonVerbs.Contains(m.Groups[2].Value) ? m.Groups[1].Value + " " + BaseVerb(char.ToUpperInvariant(m.Groups[2].Value[0]) + m.Groups[2].Value[1..]) : m.Value);
        return $"In this step it should {verb}{rest}.";
    }

    private static readonly HashSet<string> CommonVerbs = new(StringComparer.Ordinal)
    {
        "runs", "heats", "cools", "opens", "closes", "starts", "stops", "fills", "drains", "doses", "mixes", "pumps", "waits",
        "keeps", "holds", "moves", "sends", "counts", "reports", "goes", "checks", "sets", "resets", "switches", "turns",
    };

    /// <summary>"Runs" -> "run", "Mixes" -> "mix", "Carries" -> "carry". Null when the word does not look like such a verb.</summary>
    private static string? BaseVerb(string word)
    {
        if (word.Length < 3 || !char.IsUpper(word[0]) || !word.All(char.IsLetter) || !word.EndsWith('s')) return null;
        var w = word.ToLowerInvariant();
        string[] irregular = ["is", "has", "does", "goes"];
        string[] bases = ["be", "have", "do", "go"];
        var i = Array.IndexOf(irregular, w);
        if (i >= 0) return bases[i];
        if (w.EndsWith("ies")) return w[..^3] + "y";
        if (w.EndsWith("sses") || w.EndsWith("xes") || w.EndsWith("zes") || w.EndsWith("ches") || w.EndsWith("shes")) return w[..^2];
        if (w.EndsWith("ss") || w.EndsWith("us")) return null;     // "Process", "Status": not a verb in the -s form
        return w[..^1];
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

/// <summary>Sorts "A9" before "A10": runs of digits compare as numbers.</summary>
public sealed class NaturalComparer : IComparer<string>
{
    public static readonly NaturalComparer Instance = new();

    public int Compare(string? x, string? y)
    {
        if (x == null || y == null) return string.CompareOrdinal(x, y);
        int i = 0, j = 0;
        while (i < x.Length && j < y.Length)
        {
            if (char.IsDigit(x[i]) && char.IsDigit(y[j]))
            {
                int si = i, sj = j;
                while (i < x.Length && char.IsDigit(x[i])) i++;
                while (j < y.Length && char.IsDigit(y[j])) j++;
                var a = x[si..i].TrimStart('0');
                var b = y[sj..j].TrimStart('0');
                if (a.Length != b.Length) return a.Length.CompareTo(b.Length);
                var c = string.CompareOrdinal(a, b);
                if (c != 0) return c;
            }
            else
            {
                var c = char.ToUpperInvariant(x[i]).CompareTo(char.ToUpperInvariant(y[j]));
                if (c != 0) return c;
                i++; j++;
            }
        }
        return (x.Length - i).CompareTo(y.Length - j);
    }
}
