using PlcKi.Core.Model;

namespace PlcKi.Core.Status;

/// <summary>One value change: from this time on, the tag had this value. Seq grows by one for every change kept.</summary>
/// <param name="HadPrev">False for the first value after a connect (nothing to compare with, so it is not an edge).</param>
public sealed record FastPoint(long Seq, DateTimeOffset Time, string TagId, object? Value, object? Prev, bool HadPrev);

/// <summary>A stretch with no data (the PLC link was down).</summary>
public sealed record FastGap(DateTimeOffset From, DateTimeOffset? To);

/// <summary>
/// The last minute of value changes, much finer than the once-a-second reads: fed by a PLC subscription (the CPU sends
/// every change, sampled every 100 ms by default) or, when that is not available, by the normal reads. Only changes
/// are kept, so a quiet machine costs nearly nothing. Alarm captures are cut from it (see AlarmCaptureBook).
/// Thread-safe: the subscription writes from the driver's thread, the polling loop and the web API read.
/// </summary>
public sealed class FastRecorder
{
    private readonly object _lock = new();
    private readonly LinkedList<FastPoint> _points = new();
    private readonly Dictionary<string, object?> _last = new(StringComparer.Ordinal);
    /// <summary>The value each tag had at the start of what is kept (when its older points were dropped).</summary>
    private readonly Dictionary<string, object?> _base = new(StringComparer.Ordinal);
    private readonly List<FastGap> _gaps = new();
    private long _seq;

    public FastRecorder(TimeSpan? keep = null, int maxPoints = 300_000)
    {
        Keep = keep ?? TimeSpan.FromSeconds(90);
        MaxPoints = maxPoints;
    }

    public TimeSpan Keep { get; }
    public int MaxPoints { get; }

    /// <summary>"subscription" (the PLC sends changes), "reads" (only the normal reads) or "off".</summary>
    public string Mode { get; private set; } = "off";
    /// <summary>How finely values are sampled: the subscription cycle, or the read interval.</summary>
    public int CycleMs { get; private set; }
    /// <summary>Why the fast subscription is not used (plain words), when it is not.</summary>
    public string? Note { get; private set; }
    /// <summary>Tags the subscription covers; the reads fill in the rest.</summary>
    public int FastTags { get; private set; }
    public DateTimeOffset? LastChange { get; private set; }
    public long Notifications { get; private set; }

    public void SetMode(string mode, int cycleMs, string? note = null, int fastTags = 0)
    {
        lock (_lock) { Mode = mode; CycleMs = cycleMs; Note = note; FastTags = fastTags; }
    }

    /// <summary>Counts one message from the PLC (for the status line), whether or not anything changed.</summary>
    public void Tick() { lock (_lock) Notifications++; }

    /// <summary>Keeps the value if it differs from the tag's last one. Returns the new point, or null when nothing changed.</summary>
    public FastPoint? Add(DateTimeOffset time, string tagId, object? value)
    {
        lock (_lock)
        {
            var had = _last.TryGetValue(tagId, out var prev);
            if (had && Equals(prev, value)) return null;
            _last[tagId] = value;
            var p = new FastPoint(++_seq, time, tagId, value, had ? prev : null, had);
            _points.AddLast(p);
            if (LastChange == null || time > LastChange) LastChange = time;
            // A gap ends with the first value after it
            if (_gaps.Count > 0 && _gaps[^1].To == null) _gaps[^1] = _gaps[^1] with { To = time };
            Trim(time);
            return p;
        }
    }

    /// <summary>Adds every value of a normal read, except the tags the subscription already covers.</summary>
    public void AddSnapshot(Snapshot snap, IReadOnlySet<string>? skip = null)
    {
        foreach (var (id, v) in snap.Values)
            if (v != null && (skip == null || !skip.Contains(id)))
                Add(snap.Time, id, v);
    }

    /// <summary>The link dropped: forget the last values, so the first ones after it are not taken for changes.</summary>
    public void Break(DateTimeOffset time)
    {
        lock (_lock)
        {
            if (_last.Count == 0) return;
            _last.Clear();
            if (_gaps.Count == 0 || _gaps[^1].To != null) _gaps.Add(new FastGap(time, null));
        }
    }

    private void Trim(DateTimeOffset now)
    {
        var cutoff = now - Keep;
        while (_points.First is { } f && (f.Value.Time < cutoff || _points.Count > MaxPoints))
        {
            _base[f.Value.TagId] = f.Value.Value;
            _points.RemoveFirst();
        }
        _gaps.RemoveAll(g => g.To != null && g.To < cutoff);
    }

    /// <summary>Changes kept after the given sequence number, oldest first (for the alarm check).</summary>
    public List<FastPoint> After(long seq)
    {
        lock (_lock)
        {
            var list = new List<FastPoint>();
            for (var n = _points.Last; n != null && n.Value.Seq > seq; n = n.Previous) list.Add(n.Value);
            list.Reverse();
            return list;
        }
    }

    public long LastSeq { get { lock (_lock) return _seq; } }

    /// <summary>Changes in [from, to], ordered by time.</summary>
    public List<FastPoint> Window(DateTimeOffset from, DateTimeOffset to)
    {
        lock (_lock) return _points.Where(p => p.Time >= from && p.Time <= to).OrderBy(p => p.Time).ThenBy(p => p.Seq).ToList();
    }

    /// <summary>Each tag's value just before the given time (as far as kept).</summary>
    public Dictionary<string, object?> ValuesBefore(DateTimeOffset time)
    {
        lock (_lock)
        {
            var v = new Dictionary<string, object?>(_base, StringComparer.Ordinal);
            foreach (var p in _points.Where(p => p.Time < time).OrderBy(p => p.Time).ThenBy(p => p.Seq)) v[p.TagId] = p.Value;
            return v;
        }
    }

    public List<FastGap> Gaps(DateTimeOffset from, DateTimeOffset to)
    {
        lock (_lock) return _gaps.Where(g => g.From <= to && (g.To ?? DateTimeOffset.MaxValue) >= from).ToList();
    }

    public DateTimeOffset? Oldest { get { lock (_lock) return _points.First?.Value.Time; } }
    public int Count { get { lock (_lock) return _points.Count; } }
}
