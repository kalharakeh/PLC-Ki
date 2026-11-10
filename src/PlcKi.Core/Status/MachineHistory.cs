using PlcKi.Core.Model;

namespace PlcKi.Core.Status;

/// <summary>
/// Something that changed on the machine: a step change, an alarm, a halt, a blocked device, the PLC link.
/// The optional fields say what it is about, so a page can link to it and the shift log can filter by it.
/// </summary>
public sealed record MachineEvent(DateTimeOffset Time, string Kind, string Text)
{
    /// <summary>The alarm id (alarm events).</summary>
    public string? Alarm { get; init; }
    /// <summary>The sequence id (step and halt events).</summary>
    public string? Seq { get; init; }
    /// <summary>The step it is in now (step and halt events).</summary>
    public int? Step { get; init; }
    /// <summary>The device id (blocked, output, state events).</summary>
    public string? Device { get; init; }
    /// <summary>The tag symbol (values written or held).</summary>
    public string? Tag { get; init; }
}

/// <summary>One point of a tag's history: the value from this time on.</summary>
public sealed record HistoryPoint(DateTimeOffset Time, object? Value, string Text);

/// <summary>
/// Remembers what happened recently, in memory only: the last few minutes of tag values and a list of events
/// found by comparing each status with the one before. Lets the assistant answer "what happened?" and
/// "how did the temperature change?", not only "what is it now?". Every new event also goes to <see cref="Recorded"/>,
/// where the EventLog keeps it on disk. Thread-safe.
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
    // The CPU's last known mode: true = not running (STOP...), false = RUN, null = not known yet. A failed mode read
    // changes nothing here, so a flaky read does not log "back in RUN".
    private bool? _cpuStopped;

    public MachineHistory(TimeSpan? keepValues = null, int maxEvents = 1000)
    {
        _keepValues = keepValues ?? TimeSpan.FromMinutes(15);
        _maxEvents = maxEvents;
    }

    /// <summary>Called with each batch of new events, after they are added (the disk log listens here).</summary>
    public Action<IReadOnlyList<MachineEvent>>? Recorded { get; set; }

    public TimeSpan KeepValues => _keepValues;
    /// <summary>At most this many events are kept (the oldest go first).</summary>
    public int MaxEvents => _maxEvents;

    public void Add(Snapshot snap, MachineStatus status)
    {
        bool hasValues = snap.Values.Values.Any(v => v != null);
        List<MachineEvent> events;
        lock (_lock)
        {
            if (hasValues && (_snapshots.Count == 0 || _snapshots.Last().Time != snap.Time))
            {
                _snapshots.Enqueue(snap);
                var cutoff = snap.Time - _keepValues;
                while (_snapshots.Count > 0 && _snapshots.Peek().Time < cutoff) _snapshots.Dequeue();
            }

            events = new List<MachineEvent>();
            if (_prev == null) events.Add(new MachineEvent(status.Time, "monitor", "PLC-Ki started watching the machine."));
            else events.AddRange(Diff(_prev, status, compareValues: hasValues && _prevHadValues));
            if (hasValues && !_prevHadValues) events.AddRange(Baseline(status));
            if (CpuEvent(status) is { } ce) events.Add(ce);
            foreach (var e in events)
            {
                _events.AddLast(e);
                if (_events.Count > _maxEvents) _events.RemoveFirst();
            }
            _prev = status;
            _prevHadValues |= hasValues;
        }
        if (events.Count > 0) Recorded?.Invoke(events);
    }

    /// <summary>"The PLC went to STOP" when the CPU leaves RUN, "The PLC is back in RUN" when it comes back.</summary>
    private MachineEvent? CpuEvent(MachineStatus b)
    {
        if (b.Cpu is not { } cpu || !(cpu.Stopped || cpu.Running)) return null;
        var was = _cpuStopped;
        _cpuStopped = cpu.Stopped;
        if (was == cpu.Stopped || (was == null && !cpu.Stopped)) return null;
        var word = cpu.Word ?? "STOP";
        var t = b.Time;
        if (was == null) return new MachineEvent(t, "cpu", $"The PLC was in {word} when watching started: the program is not running.");
        return cpu.Stopped
            ? new MachineEvent(t, "cpu", $"The PLC went to {word}: the program stopped running" + (cpu.Switch is { Length: > 0 } sw ? $" (mode switch at {sw})." : "."))
            : new MachineEvent(t, "cpu", "The PLC is back in RUN: the program is running again.");
    }

    /// <summary>Adds an event that did not come from comparing two reads (a value someone changed from PLC-Ki, an alarm that
    /// came and went between two reads), in time order.</summary>
    public void Note(MachineEvent e)
    {
        lock (_lock)
        {
            var n = _events.Last;
            while (n != null && n.Value.Time > e.Time) n = n.Previous;
            if (n == null) _events.AddFirst(e); else _events.AddAfter(n, e);
            if (_events.Count > _maxEvents) _events.RemoveFirst();
        }
        Recorded?.Invoke(new[] { e });
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
                (s.Halted ? " It was halted" + Because(s.HaltReasons) + "." : "")) { Seq = s.Id, Step = s.Step });
        foreach (var a in b.ActiveAlarms)
            list.Add(new MachineEvent(t, "alarm", $"{a.Id} was already active when watching started: {a.Text}.") { Alarm = a.Id });
        foreach (var e in b.Equipment.Where(IsBlocked))
            list.Add(new MachineEvent(t, "blocked", e.Explanation) { Device = e.Id });
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
                        : $"{s.Name}: now in step {s.Step} \"{s.StepName}\"") + ".") { Seq = s.Id, Step = s.Step });
            if (!p.Halted && s.Halted)
                list.Add(new MachineEvent(t, "halt", $"{s.Name} halted in step {s.Step}" + Because(s.HaltReasons) + ".") { Seq = s.Id, Step = s.Step });
            else if (p.Halted && !s.Halted)
                list.Add(new MachineEvent(t, "halt", $"{s.Name} is no longer halted.") { Seq = s.Id, Step = s.Step });
            else if (p.Halted && s.Halted && !p.HaltReasons.SequenceEqual(s.HaltReasons) && s.HaltReasons.Count > 0)
                list.Add(new MachineEvent(t, "halt", $"{s.Name} still halted, now" + Because(s.HaltReasons) + ".") { Seq = s.Id, Step = s.Step });
        }

        var before = a.ActiveAlarms.Select(x => x.Id).ToHashSet();
        var now = b.ActiveAlarms.Select(x => x.Id).ToHashSet();
        foreach (var x in b.ActiveAlarms.Where(x => !before.Contains(x.Id)))
            list.Add(new MachineEvent(t, "alarm", $"{x.Id} came: {x.Text} ({x.Severity}).") { Alarm = x.Id });
        foreach (var x in a.ActiveAlarms.Where(x => !now.Contains(x.Id)))
            list.Add(new MachineEvent(t, "alarm", $"{x.Id} went: {x.Text}.") { Alarm = x.Id });

        // Going into or out of STOP clears or brings back every "blocked" at once: that is the CPU mode, logged on its own
        bool cpuChanged = (a.Cpu?.Stopped ?? false) != (b.Cpu?.Stopped ?? false);
        foreach (var e in b.Equipment)
        {
            var p = a.Equipment.FirstOrDefault(x => x.Id == e.Id);
            if (p == null) continue;
            if (!cpuChanged && !IsBlocked(p) && IsBlocked(e))
                list.Add(new MachineEvent(t, "blocked", e.Explanation) { Device = e.Id });
            else if (!cpuChanged && IsBlocked(p) && !IsBlocked(e))
                list.Add(new MachineEvent(t, "blocked", $"{e.Name} is no longer blocked: {e.State}.") { Device = e.Id });
            if (p.Commanded != true && e.Commanded == true)
                list.Add(new MachineEvent(t, "output", $"{e.Name} switched on.") { Device = e.Id });
            else if (p.Commanded == true && e.Commanded == false)
                list.Add(new MachineEvent(t, "output", $"{e.Name} switched off.") { Device = e.Id });
            if (p.Level != e.Level && e.Level is "fault" or "warn")
                list.Add(new MachineEvent(t, "state", $"{e.Name}: {e.State}.") { Device = e.Id });
        }
        return list;
    }

    private static bool IsBlocked(EquipmentStatus e) => e.BlockedBy != null;

    private static string Because(List<string> reasons) =>
        reasons.Count == 0 ? "" : " because " + string.Join(", ", reasons);
}
