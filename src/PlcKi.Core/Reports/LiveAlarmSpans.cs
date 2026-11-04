using PlcKi.Core.Status;

namespace PlcKi.Core.Reports;

/// <summary>
/// Alarm occurrences seen since PLC-Ki started, in memory only. A stand-in for the machine memory (which keeps them
/// on disk): once that is in, the fault report reads from it instead. Fed with each status. Thread-safe.
/// </summary>
public sealed class LiveAlarmSpans : IAlarmSpans
{
    private readonly object _lock = new();
    private readonly List<AlarmSpan> _done = new();
    private readonly Dictionary<string, AlarmSpan> _open = new(StringComparer.Ordinal);
    private readonly int _max;
    private DateTimeOffset? _since;

    public LiveAlarmSpans(int max = 20000) => _max = max;

    public DateTimeOffset? KnownSince { get { lock (_lock) return _since; } }

    public void Add(MachineStatus st)
    {
        // No values: the alarms are not known, so nothing came or went
        if (st.Connection.State is not ("connected" or "snapshot") || st.Raw == null || st.Raw.Count == 0) return;
        lock (_lock)
        {
            _since ??= st.Time;
            var now = st.ActiveAlarms.ToDictionary(a => a.Id, StringComparer.Ordinal);
            foreach (var a in st.ActiveAlarms.Where(a => !_open.ContainsKey(a.Id)))
                _open[a.Id] = new AlarmSpan(a.Id, a.Text, a.Severity, a.Since ?? st.Time, null);
            foreach (var id in _open.Keys.Where(id => !now.ContainsKey(id)).ToList())
            {
                _done.Add(_open[id] with { Went = st.Time });
                _open.Remove(id);
            }
            if (_done.Count > _max) _done.RemoveRange(0, _done.Count - _max);
        }
    }

    public List<AlarmSpan> Spans(DateTimeOffset from, DateTimeOffset to)
    {
        lock (_lock)
            return _done.Concat(_open.Values).Where(s => s.Came <= to && (s.Went ?? to) >= from).OrderBy(s => s.Came).ToList();
    }
}
