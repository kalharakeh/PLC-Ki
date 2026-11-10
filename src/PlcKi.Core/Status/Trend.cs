using PlcKi.Core.Model;

namespace PlcKi.Core.Status;

/// <summary>One value over time for a chart. Points are [time in ms since 1970, value]; a gap is a null value.</summary>
public sealed record TrendSeries(string Id, string Label, string? Unit, string Type, List<double?[]> Points);

/// <summary>A stretch of time a sequence spent in one step.</summary>
public sealed record StepSpan(long From, long To, int Step);

public sealed record SequenceSpans(string Id, string Name, List<StepSpan> Spans);

/// <summary>An alarm that came or went, for a mark on the chart.</summary>
public sealed record TrendMark(long Time, string Id, bool Came, string Text);

/// <param name="Source">"memory" (the live buffer) or "disk" (the saved values).</param>
/// <param name="ResolutionSec">Disk only: how long one slice is, in seconds.</param>
/// <param name="SavedFrom">Disk only: when the oldest saved values start, in ms.</param>
public sealed record TrendData(long From, long To, int KeepMinutes, List<TrendSeries> Series, List<SequenceSpans> Steps, List<TrendMark> Alarms,
    List<object> Choices, string Source = "memory", int? ResolutionSec = null, long? SavedFrom = null);

/// <summary>
/// The charts' data, from the reads PLC-Ki already keeps in memory (MachineHistory): numeric values,
/// which step each sequence was in, and when alarms came and went. Read-only.
/// </summary>
public static class Trend
{
    static readonly HashSet<string> Numeric = new(StringComparer.OrdinalIgnoreCase) { "Real", "LReal", "Int", "DInt" };

    /// <summary>The ids of the numeric tags of the model: the ones the ValueLog saves.</summary>
    public static List<string> NumericIds(MachineModel m) => m.Tags.Where(t => Numeric.Contains(t.Type)).Select(t => t.Id).ToList();

    /// <summary>The values charted when none are asked for: measured numbers from the overview and the devices.</summary>
    public static List<TagDef> DefaultTags(MachineModel m, int max = 8)
    {
        var steps = m.Sequences.SelectMany(s => new[] { s.StepTag, s.StepTimeTag }).Where(x => x != null).ToHashSet();
        var ids = m.Overview.Concat(m.Equipment.SelectMany(e => e.Values)).Distinct();
        var tags = ids.Select(id => m.Tags.FirstOrDefault(t => t.Id == id))
            .Where(t => t != null && Numeric.Contains(t.Type) && !steps.Contains(t.Id))
            .Cast<TagDef>()
            // Measured values: real numbers, or whole numbers with a unit. Counters and recipe numbers can be added by hand.
            .Where(t => t.Type is "Real" or "LReal" || !string.IsNullOrEmpty(t.Unit));
        return tags.Take(max).ToList();
    }

    public static TrendData Build(MachineModel m, MachineHistory h, TimeSpan window, DateTimeOffset now, IEnumerable<string>? tagIds = null, int buckets = 300)
    {
        var snaps = h.Snapshots(window, now);
        long to = now.ToUnixTimeMilliseconds(), from = (now - window).ToUnixTimeMilliseconds();
        // Just started watching: the time axis starts at the first value, not at an empty stretch
        if (snaps.Count > 0) from = Math.Min(Math.Max(from, snaps[0].Time.ToUnixTimeMilliseconds()), to - 10_000);

        var tags = tagIds?.Select(id => m.Tags.FirstOrDefault(t => t.Id == id)).Where(t => t != null && Numeric.Contains(t.Type)).Cast<TagDef>().Take(16).ToList()
                   ?? DefaultTags(m);
        var series = tags.Select(t => new TrendSeries(t.Id, t.Label ?? t.Id, t.Unit, t.Type, Points(snaps, t.Id, from, to, buckets))).ToList();

        var steps = new List<SequenceSpans>();
        foreach (var q in m.Sequences)
        {
            var spans = new List<StepSpan>();
            foreach (var s in snaps)
            {
                long t = s.Time.ToUnixTimeMilliseconds();
                int? step = s.Values.GetValueOrDefault(q.StepTag) is double d ? (int)d : null;
                var last = spans.Count > 0 ? spans[^1] : null;
                if (step == null) continue;
                if (last != null && last.Step == step) spans[^1] = last with { To = t };
                else
                {
                    if (last != null) spans[^1] = last with { To = t };
                    spans.Add(new StepSpan(t, t, step.Value));
                }
            }
            if (spans.Count > 0) spans[^1] = spans[^1] with { To = Math.Max(spans[^1].To, snaps[^1].Time.ToUnixTimeMilliseconds()) };
            steps.Add(new SequenceSpans(q.Id, q.Name, spans));
        }

        var alarms = h.Events(window, now).Where(e => e.Kind == "alarm").Select(e =>
        {
            var id = e.Text.Split(' ', 2)[0];
            bool came = !e.Text.Contains(" went");
            return new TrendMark(e.Time.ToUnixTimeMilliseconds(), id, came, e.Text);
        }).ToList();

        var choices = m.Tags.Where(t => Numeric.Contains(t.Type)).Select(t => (object)new { t.Id, label = t.Label ?? t.Id, t.Unit }).ToList();
        return new TrendData(from, to, (int)Math.Round(h.KeepValues.TotalMinutes), series, steps, alarms, choices);
    }

    /// <summary>
    /// The same charts from the values saved on disk (ValueLog): a window that reaches back past the live buffer, a day, a week.
    /// Each slice gives its lowest and highest value; the step bands come from the last step of each slice, so a step shorter
    /// than a slice does not show; the alarm marks come from the event files.
    /// </summary>
    public static TrendData BuildFromDisk(MachineModel m, MachineHistory h, ValueLog values, EventLog? events, DateTimeOffset from, DateTimeOffset to,
        IEnumerable<string>? tagIds = null, int buckets = 300)
    {
        var tags = tagIds?.Select(id => m.Tags.FirstOrDefault(t => t.Id == id)).Where(t => t != null && Numeric.Contains(t.Type)).Cast<TagDef>().Take(16).ToList()
                   ?? DefaultTags(m);
        var ids = tags.Select(t => t.Id).Concat(m.Sequences.Select(q => q.StepTag).Where(x => !string.IsNullOrEmpty(x))).Distinct().ToList();
        var range = values.Read(from, to, ids, buckets);
        long half = (long)(range.BucketMs / 2);

        // The time axis starts at the first value, so a week with two days of data does not show five empty ones
        long first = long.MaxValue;
        foreach (var arr in range.Series.Values)
            for (int i = 0; i < arr.Length; i++)
                if (arr[i] != null) { first = Math.Min(first, range.BucketStart(i)); break; }
        long axisFrom = first == long.MaxValue ? range.From : Math.Min(Math.Max(range.From, first), range.To - 10_000);

        var series = tags.Select(t => new TrendSeries(t.Id, t.Label ?? t.Id, t.Unit, t.Type, DiskPoints(range, t.Id, axisFrom, half))).ToList();

        var steps = new List<SequenceSpans>();
        foreach (var q in m.Sequences)
        {
            var spans = new List<StepSpan>();
            if (!string.IsNullOrEmpty(q.StepTag) && range.Series.TryGetValue(q.StepTag, out var arr))
                for (int i = 0; i < arr.Length; i++)
                {
                    if (arr[i] is not { } b) continue;
                    long t0 = range.BucketStart(i), t1 = range.BucketStart(i + 1);
                    int step = (int)b.Last;
                    if (spans.Count > 0 && spans[^1].Step == step && t0 - spans[^1].To <= (long)range.BucketMs) spans[^1] = spans[^1] with { To = t1 };
                    else spans.Add(new StepSpan(t0, t1, step));
                }
            steps.Add(new SequenceSpans(q.Id, q.Name, spans));
        }

        var alarms = (events?.Read(DateTimeOffset.FromUnixTimeMilliseconds(axisFrom), to, new HashSet<string> { "alarm" }) ?? new())
            .Select(e => new TrendMark(e.Time.ToUnixTimeMilliseconds(), e.Text.Split(' ', 2)[0], !e.Text.Contains(" went"), e.Text)).ToList();

        var choices = m.Tags.Where(t => Numeric.Contains(t.Type)).Select(t => (object)new { t.Id, label = t.Label ?? t.Id, t.Unit }).ToList();
        long? saved = values.OldestDay() is { } d ? new DateTimeOffset(d.ToDateTime(TimeOnly.MinValue)).ToUnixTimeMilliseconds() : null;
        return new TrendData(axisFrom, range.To, (int)Math.Round(h.KeepValues.TotalMinutes), series, steps, alarms, choices, "disk",
            (int)Math.Round(range.BucketMs / 1000), saved);
    }

    /// <summary>The lowest and highest of each slice as two points in time order; an empty slice after data is a gap (null).</summary>
    static List<double?[]> DiskPoints(ValueRange range, string id, long axisFrom, long half)
    {
        var points = new List<double?[]>();
        if (!range.Series.TryGetValue(id, out var arr)) return points;
        bool gap = false, any = false;
        for (int i = 0; i < arr.Length; i++)
        {
            long t0 = range.BucketStart(i);
            if (t0 + half * 2 < axisFrom) continue;
            if (arr[i] is not { } b)
            {
                if (any && !gap) { points.Add(new double?[] { t0, null }); gap = true; }
                continue;
            }
            gap = false; any = true;
            points.Add(new double?[] { t0 + half / 2, b.Min });
            if (b.Max != b.Min) points.Add(new double?[] { t0 + half / 2 + half, b.Max });
        }
        return points;
    }

    /// <summary>The lowest and highest value in each time slice, in time order, so a short spike still shows.</summary>
    static List<double?[]> Points(List<Snapshot> snaps, string id, long from, long to, int buckets)
    {
        var raw = snaps.Select(s => (T: s.Time.ToUnixTimeMilliseconds(), V: s.Values.GetValueOrDefault(id) as double?)).ToList();
        if (raw.Count <= buckets * 2) return raw.Select(p => new double?[] { p.T, p.V }).ToList();
        var outp = new List<double?[]>();
        double size = (to - from) / (double)buckets;
        foreach (var g in raw.GroupBy(p => (int)((p.T - from) / size)).OrderBy(g => g.Key))
        {
            var vals = g.Where(p => p.V != null).ToList();
            if (vals.Count == 0) { outp.Add(new double?[] { g.First().T, null }); continue; }
            var lo = vals.MinBy(p => p.V!.Value); var hi = vals.MaxBy(p => p.V!.Value);
            foreach (var p in new[] { lo, hi }.Distinct().OrderBy(p => p.T)) outp.Add(new double?[] { p.T, p.V });
        }
        return outp;
    }
}
