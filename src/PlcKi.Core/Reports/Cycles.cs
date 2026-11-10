using PlcKi.Core.Model;
using PlcKi.Core.Status;

namespace PlcKi.Core.Reports;

/// <summary>One run of the cycle sequence: from leaving its first step to coming back to it (ms since 1970).</summary>
public sealed record CycleSpan(long Start, long End, double Sec, bool Running);

/// <summary>
/// The machine's cycles for the trend page's "This cycle" and "Compare with the previous cycle" (concept view 6; PLC-Ki
/// has no batch of its own, so the cycle is the unit): the finished ones from the production log's cycle records, and
/// the one running now, found from the saved step values. Read-only.
/// </summary>
public static class Cycles
{
    /// <summary>The running cycle is looked for this far back at most.</summary>
    static readonly TimeSpan LookBack = TimeSpan.FromHours(6);

    /// <summary>Finished cycles of the cycle sequence, newest first. A cycle record is written when the sequence is back at its first step.</summary>
    public static List<CycleSpan> Finished(IEnumerable<ProdRecord> records, string seqId, int max = 20) =>
        records.Where(r => r.K == "cycle" && r.Sec is > 0 && (r.Seq == null || r.Seq == seqId))
            .OrderByDescending(r => r.T)
            .Take(max)
            .Select(r => { long end = r.T.ToUnixTimeMilliseconds(); return new CycleSpan(end - (long)(r.Sec!.Value * 1000), end, r.Sec.Value, false); })
            .ToList();

    /// <summary>The sequence whose cycles are shown: the one set on the Reports page, else the first of the model.</summary>
    public static SequenceDef? Sequence(MachineModel m, ReportSettings cfg) =>
        string.IsNullOrEmpty(cfg.CycleSequence) ? m.Sequences.FirstOrDefault() : m.Sequences.FirstOrDefault(s => s.Id == cfg.CycleSequence);

    /// <summary>
    /// The cycles to offer, newest first: the running one (if the sequence is out of its first step now, found by walking
    /// back through the saved step values to when it left) and then the finished ones. Only cycles the saved values cover.
    /// </summary>
    public static List<CycleSpan> List(MachineModel m, ReportSettings cfg, IEnumerable<ProdRecord> records, ValueLog? values, DateTimeOffset now, int max = 20)
    {
        var seq = Sequence(m, cfg);
        if (seq == null || string.IsNullOrEmpty(seq.StepTag)) return new();
        var list = Finished(records, seq.Id, max);
        if (values?.OldestDay() is not { } oldest) return new();
        long saved = new DateTimeOffset(oldest.ToDateTime(TimeOnly.MinValue)).ToUnixTimeMilliseconds();
        list = list.Where(c => c.Start >= saved).ToList();

        var since = list.Count > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(list[0].End) : now - LookBack;
        if (since < now - LookBack) since = now - LookBack;
        if (RunningSince(m, seq, values, since, now) is { } start)
            list.Insert(0, new CycleSpan(start, now.ToUnixTimeMilliseconds(), (now.ToUnixTimeMilliseconds() - start) / 1000.0, true));
        return list;
    }

    /// <summary>When the cycle running now left the first step (to the resolution of the saved values); null when the sequence is at its first step or it cannot be told.</summary>
    static long? RunningSince(MachineModel m, SequenceDef seq, ValueLog values, DateTimeOffset since, DateTimeOffset now)
    {
        var home = ProductionLog.HomeStep(m, seq.Id);
        if (home == null || now - since < TimeSpan.FromSeconds(5)) return null;
        var range = values.Read(since, now, new[] { seq.StepTag! }, 600);
        if (!range.Series.TryGetValue(seq.StepTag!, out var steps)) return null;
        int last = Array.FindLastIndex(steps, b => b != null);
        if (last < 0 || (int)steps[last]!.Value.Last == home) return null;       // at its first step now (or nothing saved)
        int i = last;
        while (i > 0 && steps[i - 1] is { } b && (int)b.Last != home && (int)b.Min != home) i--;
        return Math.Max(range.BucketStart(i), since.ToUnixTimeMilliseconds());
    }
}
