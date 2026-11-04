using PlcKi.Core.Memory;

namespace PlcKi.Core.Reports;

/// <summary>The fault report's alarm occurrences, read from the machine memory (AlarmMemory), which keeps them on disk.</summary>
public sealed class MemoryAlarmSpans : IAlarmSpans
{
    private readonly AlarmMemory _memory;

    public MemoryAlarmSpans(AlarmMemory memory) => _memory = memory;

    public DateTimeOffset? KnownSince =>
        _memory.Occurrences(DateTimeOffset.MinValue, DateTimeOffset.MaxValue).LastOrDefault()?.Came;

    public List<AlarmSpan> Spans(DateTimeOffset from, DateTimeOffset to) =>
        _memory.Occurrences(from, to).Select(o => new AlarmSpan(o.AlarmId, o.Text, o.Severity, o.Came, o.Went)).OrderBy(s => s.Came).ToList();
}
