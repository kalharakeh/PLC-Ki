using PlcKi.Core.Model;

namespace PlcKi.Core.Status;

/// <summary>Something that changed on the machine: a step change, an alarm, a halt, a blocked device, the PLC link.</summary>
public sealed record MachineEvent(DateTimeOffset Time, string Kind, string Text);

/// <summary>One point of a tag's history: the value from this time on.</summary>
public sealed record HistoryPoint(DateTimeOffset Time, object? Value, string Text);

/// <summary>
/// Remembers what happened recently, in memory only: the last few minutes of tag values and a list of events
/// found by comparing each status with the one before. Lets the assistant answer "what happened?" and
/// "how did the temperature change?", not only "what is it now?". Thread-safe.
/// </summary>
public sealed class MachineHistory
{
    private readonly object _lock = new();
    private readonly Queue<Snapshot> _snapshots = new();
    private readonly LinkedList<MachineEvent> _events = new();
    private readonly TimeSpan _keepValues;
    private readonly int _maxEvents;
    private MachineStatus? _prev;
    private bool _prevHadValues;

    public MachineHistory(TimeSpan? keepValues = null, int maxEvents = 1000)
    {
        _keepValues = keepValues ?? TimeSpan.FromMinutes(15);
        _maxEvents = maxEvents;
    }

    public TimeSpan KeepValues => _keepValues;

    public void Add(Snapshot snap, MachineStatus status)
    {
        bool hasValues = snap.Values.Values.Any(v => v != null);
        lock (_lock)
        {
            if (hasValues && (_snapshots.Count == 0 || _snapshots.Last().Time != snap.Time))
            {
                _snapshots.Enqueue(snap);
                var cutoff = snap.Time - _keepValues;
                while (_snapshots.Count > 0 && _snapshots.Peek().Time < cutoff) _snapshots.Dequeue();
            }

            var events = new List<MachineEvent>();
            if (_prev == null) events.Add(new MachineEvent(status.Time, "monitor", "PLC-Ki started watching the machine."));
            else events.AddRange(Diff(_prev, status, compareValues: hasValues && _prevHadValues));
            if (hasValues && !_prevHadValues) events.AddRange(Baseline(status));
            foreach (var e in events)
            {
                _events.AddLast(e);
                if (_events.Count > _maxEvents) _events.RemoveFirst();
            }
            _prev = status;
            _prevHadValues |= hasValues;
        }
    }

    /// <summary>Adds an event found elsewhere (e.g. an alarm that came and went between two reads), in time order.</summary>
    public void Note(MachineEvent e)
    {
        lock (_lock)
        {
            var n = _events.Last;
            while (n != null && n.Value.Time > e.Time) n = n.Previous;
            if (n == null) _events.AddFirst(e); else _events.AddAfter(n, e);
            if (_events.Count > _maxEvents) _events.RemoveFirst();
        }
    }

    public List<MachineEvent> Events(TimeSpan window, DateTimeOffset now)
    {
        lock (_lock) return _events.Where(e => e.Time >= now - window).ToList();
    }

    /// <summary>The kept reads in the window, oldest first (a copy).</summary>
    public List<Snapshot> Snapshots(TimeSpan window, DateTimeOffset now)
    {
        lock (_lock) return _snapshots.Where(s => s.Time >= now - window).ToList();
    }

    public DateTimeOffset? OldestValueTime
    {
        get { lock (_lock) return _snapshots.Count > 0 ? _snapshots.Peek().Time : null; }
    }

    /// <summary>
    /// The values of one tag over the window, as change points (the value from that time on).
    /// If it changed more often than maxPoints, keeps evenly spread points plus the first and last.
    /// </summary>
    public List<HistoryPoint> Series(TagDef tag, TimeSpan window, DateTimeOffset now, int maxPoints = 40)
    {
        List<Snapshot> snaps;
        lock (_lock) snaps = _snapshots.Where(s => s.Time >= now - window).ToList();

        var points = new List<HistoryPoint>();
        object? last = null;
        bool first = true;
        foreach (var s in snaps)
        {
            var v = s.Values.GetValueOrDefault(tag.Id);
            if (first || !Equals(v, last))
                points.Add(new HistoryPoint(s.Time, v, ValueFormat.Format(tag, v)));
            last = v;
            first = false;
        }
        if (points.Count <= maxPoints) return points;
        var keep = new List<HistoryPoint>(maxPoints);
        for (int i = 0; i < maxPoints; i++)
            keep.Add(points[(int)Math.Round(i * (points.Count - 1) / (double)(maxPoints - 1))]);
        return keep;
    }

    /// <summary>Number value statistics of one tag over the window (null if not a number or no data).</summary>
    public (double Min, double Max, double Avg)? Stats(TagDef tag, TimeSpan window, DateTimeOffset now)
    {
        List<double> xs;
        lock (_lock)
            xs = _snapshots.Where(s => s.Time >= now - window)
                .Select(s => s.Values.GetValueOrDefault(tag.Id)).OfType<double>().ToList();
        return xs.Count == 0 ? null : (xs.Min(), xs.Max(), xs.Average());
    }

    /// <summary>The state found on the first good read, so the event list starts with where the machine was.</summary>
    private static List<MachineEvent> Baseline(MachineStatus b)
    {
        var t = b.Time;
        var list = new List<MachineEvent>();
        foreach (var s in b.Sequences.Where(s => s.Step != null))
            list.Add(new MachineEvent(t, "step", $"{s.Name} was in step {s.Step} \"{s.StepName}\"" +
                (s.TimeInStep != null ? $" for {s.TimeInStep} already" : "") + " when watching started." +
                (s.Halted ? " It was halted" + Because(s.HaltReasons) + "." : "")));
        foreach (var a in b.ActiveAlarms)
            list.Add(new MachineEvent(t, "alarm", $"{a.Id} was already active when watching started: {a.Text}."));
        foreach (var e in b.Equipment.Where(IsBlocked))
            list.Add(new MachineEvent(t, "blocked", e.Explanation));
        return list;
    }

    public static List<MachineEvent> Diff(MachineStatus a, MachineStatus b, bool compareValues = true)
    {
        var t = b.Time;
        var list = new List<MachineEvent>();

        if (a.Connection.State != b.Connection.State)
            list.Add(new MachineEvent(t, "link", b.Connection.State switch
            {
                "connected" => $"Connected to the PLC at {b.Connection.Address}.",
                "snapshot" => "Reading values from a test file.",
                "connecting" => "Connecting to the PLC...",
                "notset" => "No PLC address is set yet.",
                _ => $"Lost the PLC connection ({b.Connection.State})" +
                     (b.Connection.Error is { Length: > 0 } err ? $": {err}" : "."),
            }));
        if (!compareValues) return list;

        foreach (var s in b.Sequences)
        {
            var p = a.Sequences.FirstOrDefault(x => x.Id == s.Id);
            if (p == null) continue;
            if (p.Step != s.Step && s.Step != null)
                list.Add(new MachineEvent(t, "step",
                    (p.Step != null
                        ? $"{s.Name}: step {p.Step} \"{p.StepName}\" -> step {s.Step} \"{s.StepName}\"" +
                          (p.TimeInStep != null ? $" (after about {p.TimeInStep} in step {p.Step})" : "")
                        : $"{s.Name}: now in step {s.Step} \"{s.StepName}\"") + "."));
            if (!p.Halted && s.Halted)
                list.Add(new MachineEvent(t, "halt", $"{s.Name} halted in step {s.Step}" + Because(s.HaltReasons) + "."));
            else if (p.Halted && !s.Halted)
                list.Add(new MachineEvent(t, "halt", $"{s.Name} is no longer halted."));
            else if (p.Halted && s.Halted && !p.HaltReasons.SequenceEqual(s.HaltReasons) && s.HaltReasons.Count > 0)
                list.Add(new MachineEvent(t, "halt", $"{s.Name} still halted, now" + Because(s.HaltReasons) + "."));
        }

        var before = a.ActiveAlarms.Select(x => x.Id).ToHashSet();
        var now = b.ActiveAlarms.Select(x => x.Id).ToHashSet();
        foreach (var x in b.ActiveAlarms.Where(x => !before.Contains(x.Id)))
            list.Add(new MachineEvent(t, "alarm", $"{x.Id} came: {x.Text} ({x.Severity})."));
        foreach (var x in a.ActiveAlarms.Where(x => !now.Contains(x.Id)))
            list.Add(new MachineEvent(t, "alarm", $"{x.Id} went: {x.Text}."));

        foreach (var e in b.Equipment)
        {
            var p = a.Equipment.FirstOrDefault(x => x.Id == e.Id);
            if (p == null) continue;
            if (!IsBlocked(p) && IsBlocked(e))
                list.Add(new MachineEvent(t, "blocked", e.Explanation));
            else if (IsBlocked(p) && !IsBlocked(e))
                list.Add(new MachineEvent(t, "blocked", $"{e.Name} is no longer blocked: {e.State}."));
            if (p.Commanded != true && e.Commanded == true)
                list.Add(new MachineEvent(t, "output", $"{e.Name} switched on."));
            else if (p.Commanded == true && e.Commanded == false)
                list.Add(new MachineEvent(t, "output", $"{e.Name} switched off."));
            if (p.Level != e.Level && e.Level is "fault" or "warn")
                list.Add(new MachineEvent(t, "state", $"{e.Name}: {e.State}."));
        }
        return list;
    }

    private static bool IsBlocked(EquipmentStatus e) => e.BlockedBy != null;

    private static string Because(List<string> reasons) =>
        reasons.Count == 0 ? "" : " because " + string.Join(", ", reasons);
}
