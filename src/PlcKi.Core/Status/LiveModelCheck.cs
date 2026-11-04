using PlcKi.Core.Model;

namespace PlcKi.Core.Status;

/// <summary>Something the live machine did that the model does not explain.</summary>
/// <param name="Kind">device, step, transition, tag or setting.</param>
/// <param name="Target">What to fix: "equipment:XV1", "sequence:Robot", "alarm:A1.7", "setting:Tol" or "tag:Level".</param>
/// <param name="Severity">"wrong" when the model contradicts the machine, "check" when it only looks suspicious.</param>
public sealed record ModelFinding(string Kind, string Target, string Severity, string Title, string Detail, int Count, DateTimeOffset LastSeen);

public sealed record ModelCheckReport(DateTimeOffset? Since, int Reads, TimeSpan Watched, int StepChanges,
    List<ModelFinding> Findings, List<string> Seen);

/// <summary>
/// Watches the live status next to the model and collects what does not fit: an output that is on while the model
/// says an interlock is missing, a device shown blocked while the machine keeps stepping, a step or a step change the
/// model does not know, a transition whose conditions look met but the step does not move, tags that cannot be read.
/// Read-only; it only looks at what the polling loop already reads.
/// </summary>
public sealed class LiveModelCheck
{
    private readonly MachineModel _model;
    private readonly object _lock = new();
    private readonly Dictionary<string, Hit> _hits = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int?> _lastStep = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTimeOffset> _blockedSince = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _stepChangesAtBlock = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (string Key, DateTimeOffset Since)> _ready = new(StringComparer.Ordinal);
    private readonly HashSet<string> _stepsSeen = new(StringComparer.Ordinal);
    private readonly HashSet<string> _devicesOn = new(StringComparer.Ordinal);
    private MachineStatus? _prev;
    private DateTimeOffset? _since, _last;
    private int _reads, _stepChanges;

    /// <summary>A device blocked this long while the steps keep changing is reported.</summary>
    public TimeSpan BlockedLimit { get; init; } = TimeSpan.FromMinutes(2);
    /// <summary>A transition whose conditions all look met for this long without the step moving is reported.</summary>
    public TimeSpan ReadyLimit { get; init; } = TimeSpan.FromSeconds(30);

    public LiveModelCheck(MachineModel model) => _model = model;

    private sealed class Hit
    {
        public required ModelFinding First;
        public int Count;
        public DateTimeOffset Last;
        public string Detail = "";
    }

    public void Add(MachineStatus s)
    {
        if (s.Connection.State is not ("connected" or "snapshot")) { lock (_lock) _prev = null; return; }
        lock (_lock)
        {
            var now = s.Time;
            _since ??= now;
            _last = now;
            _reads++;

            foreach (var (id, why) in s.TagErrors)
                Note("tag", "tag:" + id, "wrong", $"Tag {id} cannot be read", why, now);

            foreach (var q in s.Sequences) CheckSequence(q, s, now);
            foreach (var e in s.Equipment) CheckDevice(e, now);
            foreach (var st in s.Settings ?? new())
                if (st.Valid == false)
                    Note("setting", "setting:" + st.Id, "check", $"Setting {st.Label} is {st.Value}",
                        (st.Problem ?? "The model says the machine cannot work with this value") + ".", now);
            _prev = s;
        }
    }

    /// <summary>How many steps a move may skip (each shorter than one read) before it counts as unexplained.</summary>
    public int MaxSkippedSteps { get; set; } = 3;

    private static bool Reachable(SequenceDef def, int from, int to, int maxBetween)
    {
        var next = def.Steps.ToDictionary(x => x.No, x => x.Next.Select(n => n.To).ToList());
        var level = new HashSet<int> { from };
        var seen = new HashSet<int> { from };
        for (int hop = 0; hop <= maxBetween; hop++)
        {
            var nl = new HashSet<int>();
            foreach (var n in level)
                foreach (var t in next.GetValueOrDefault(n) ?? new())
                {
                    if (t == to) return true;
                    if (seen.Add(t)) nl.Add(t);
                }
            level = nl;
        }
        return false;
    }

    private static bool Same(object? a, object? b) => a is double x && b is double y ? Math.Abs(x - y) < 1e-9 : Equals(a, b);

    private void CheckSequence(SequenceStatus q, MachineStatus s, DateTimeOffset now)
    {
        var def = _model.Sequences.FirstOrDefault(d => d.Id == q.Id);
        if (def == null) return;
        if (q.Step is int step)
        {
            _stepsSeen.Add(q.Id + ":" + step);
            if (!def.Steps.Any(x => x.No == step))
                Note("step", "sequence:" + q.Id, "wrong", $"{q.Name}: step {step} is not in the model",
                    $"The PLC reported step {step}, but the model has no such step.", now);
        }
        _lastStep.TryGetValue(q.Id, out var before);
        if (before is int a && q.Step is int b && a != b)
        {
            _stepChanges++;
            var from = def.Steps.FirstOrDefault(x => x.No == a);
            var direct = from != null && from.Next.Any(t => t.To == b);
            // A move the model explains through steps in between (a short step lasted less than one read) is fine
            if (from != null && def.Steps.Any(x => x.No == b) && !direct && !Reachable(def, a, b, MaxSkippedSteps))
                Note("transition", "sequence:" + q.Id, "wrong", $"{q.Name}: step {a} went to step {b}",
                    $"The model has no transition from step {a} \"{from.Name}\" to step {b}, not even through other steps.", now);
            // The conditions the model had for this move in the last read before it: any that looked not met, while
            // nothing it reads changed since, is suspect. If a value changed, the moment it was met fell between two reads.
            if (_prev != null && direct && s.Raw != null)
            {
                var pq = _prev.Sequences.FirstOrDefault(x => x.Id == q.Id);
                var tr = pq?.Next.FirstOrDefault(n => n.To == b);
                if (tr != null)
                    foreach (var c in tr.Conditions.Where(c => c.Met == false && c.Values.Count > 0 && c.Values.All(v => Same(v.Raw, s.Raw.GetValueOrDefault(v.Id)))))
                        Note("transition", "sequence:" + q.Id, "check", $"{q.Name}: moved from step {a} to {b} while \"{c.Text}\" was not met",
                            "The values in this condition were the same before and after the move, so the program must use a different condition here.", now,
                            minCount: 2);
            }
            _ready.Remove(q.Id);
        }
        _lastStep[q.Id] = q.Step;

        // Every condition of a way out looks met, but the step does not move (and it is not halted)
        var ready = !q.Halted ? q.Next.FirstOrDefault(n => n.Conditions.Count > 0 && n.Conditions.All(c => c.Met == true)) : null;
        if (ready == null) _ready.Remove(q.Id);
        else
        {
            var key = q.Step + ">" + ready.To;
            if (!_ready.TryGetValue(q.Id, out var r) || r.Key != key) _ready[q.Id] = (key, now);
            else if (now - r.Since is var waited && waited >= ReadyLimit)
                Note("transition", "sequence:" + q.Id, "check", $"{q.Name}: step {q.Step} does not move on to {ready.To}",
                    $"Every condition the model has for it looked met for {(int)waited.TotalSeconds} s. A condition is probably missing in the model.", now);
        }
    }

    private void CheckDevice(EquipmentStatus e, DateTimeOffset now)
    {
        if (e.Commanded == true) _devicesOn.Add(e.Id);
        // Output on while the model says an interlock is missing: that interlock is not between request and output
        if (e.Commanded == true)
            foreach (var p in e.Permits.Where(p => p.Met == false))
                Note("device", "equipment:" + e.Id, "wrong", $"{e.Name}: output on while \"{p.Text}\" was not met",
                    "The model lists this as an interlock, but the output was on without it.", now, minCount: 3);
        // Output on while the model says the program does not ask for it
        if (e.Commanded == true && e.Requested == false)
            Note("device", "equipment:" + e.Id, "check", $"{e.Name}: output on without the model's request",
                "The model's request was off while the output was on. The request in the model is probably wrong (or the device was run by hand).", now, minCount: 3);

        // Shown blocked for a long time while the machine keeps stepping
        if (e.BlockedBy != null)
        {
            if (!_blockedSince.ContainsKey(e.Id)) { _blockedSince[e.Id] = now; _stepChangesAtBlock[e.Id] = _stepChanges; }
            else if (now - _blockedSince[e.Id] >= BlockedLimit && _stepChanges - _stepChangesAtBlock[e.Id] >= 3)
                Note("device", "equipment:" + e.Id, "check", $"{e.Name} shown blocked while the machine keeps running",
                    $"Blocked for {(int)(now - _blockedSince[e.Id]).TotalMinutes} min ({e.BlockedBy}) while the steps changed {_stepChanges - _stepChangesAtBlock[e.Id]} times.", now);
        }
        else _blockedSince.Remove(e.Id);
    }

    private void Note(string kind, string target, string severity, string title, string detail, DateTimeOffset now, int minCount = 1)
    {
        var key = kind + "|" + target + "|" + title;
        if (!_hits.TryGetValue(key, out var h))
            _hits[key] = h = new Hit { First = new ModelFinding(kind, target, severity, title, detail, 0, now) };
        h.Count++;
        h.Last = now;
        h.Detail = detail;
        // minCount: only report what happened more than once (single reads can catch a value mid-change)
        if (h.Count < minCount) h.First = h.First with { Severity = "pending" };
        else if (h.First.Severity == "pending") h.First = h.First with { Severity = severity };
    }

    public ModelCheckReport Report()
    {
        lock (_lock)
        {
            var findings = _hits.Values.Where(h => h.First.Severity != "pending")
                .Select(h => h.First with { Detail = h.Detail, Count = h.Count, LastSeen = h.Last })
                .OrderBy(f => f.Severity == "wrong" ? 0 : 1).ThenByDescending(f => f.LastSeen).ToList();
            var seen = new List<string>();
            foreach (var q in _model.Sequences)
            {
                var n = q.Steps.Count(s => _stepsSeen.Contains(q.Id + ":" + s.No));
                seen.Add($"{q.Name}: {n} of {q.Steps.Count} steps seen");
            }
            if (_model.Equipment.Count > 0)
                seen.Add($"Devices: {_devicesOn.Count} of {_model.Equipment.Count} seen switched on");
            return new ModelCheckReport(_since, _reads, _since != null && _last != null ? _last.Value - _since.Value : TimeSpan.Zero,
                _stepChanges, findings, seen);
        }
    }

    /// <summary>Start over, e.g. after the model was corrected.</summary>
    public void Reset()
    {
        lock (_lock)
        {
            _hits.Clear(); _lastStep.Clear(); _blockedSince.Clear(); _stepChangesAtBlock.Clear();
            _ready.Clear(); _stepsSeen.Clear(); _devicesOn.Clear();
            _prev = null; _since = _last = null; _reads = _stepChanges = 0;
        }
    }
}
