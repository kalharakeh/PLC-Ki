using PlcKi.Core.Model;

namespace PlcKi.Core.Reports;

/// <summary>An alarm that was on from Came to Went (null: still on). Comes from the machine memory, or from the live alarms until that exists.</summary>
public sealed record AlarmSpan(string AlarmId, string Text, string Severity, DateTimeOffset Came, DateTimeOffset? Went);

/// <summary>Where the fault report gets alarm occurrences from.</summary>
public interface IAlarmSpans
{
    List<AlarmSpan> Spans(DateTimeOffset from, DateTimeOffset to);
    /// <summary>The oldest time it knows alarms for (null: none yet), so a report can say "alarms since …".</summary>
    DateTimeOffset? KnownSince { get; }
}

/// <summary>A stretch of time in one state. State: running, idle, down, nolink, off (PLC-Ki was not running).</summary>
public sealed record StateSpan(DateTimeOffset From, DateTimeOffset To, string State, string? Why, string? Alarm)
{
    public double Minutes => (To - From).TotalMinutes;
}

/// <summary>Minutes in each state.</summary>
public sealed record StateTotals(double Running, double Idle, double Down, double NoLink, double Off)
{
    public double Watched => Running + Idle + Down;
}

/// <summary>Why the machine was not running: a reason, how often, and how long in total.</summary>
public sealed record LossRow(string State, string Why, string? Alarm, int Times, double Minutes);

public sealed record FaultRow(string Id, string Text, string Severity, string? Location, int Count, double Minutes, double LongestMinutes,
    DateTimeOffset Last, bool StillOn, List<string> Causes, List<string> Fix);

/// <param name="Availability">Running time / planned production time.</param>
/// <param name="Performance">Ideal cycle time x parts / running time (null: no ideal cycle time).</param>
/// <param name="Quality">Good parts / all parts (null: no reject counter).</param>
/// <param name="Value">Availability x Performance x Quality, with an unknown part counted as 100 % (see Notes).</param>
public sealed record OeeResult(double? Availability, double? Performance, double? Quality, double? Value,
    double PlannedMinutes, double RunMinutes, double PlannedStopMinutes);

/// <param name="From">"counter" when a part counter tag is set, else "cycles" (each finished cycle is one part).</param>
public sealed record PartsResult(int Total, int Good, int Bad, string From, bool BadKnown, int Cycles, double? AvgCycleSeconds,
    double? IdealCycleSeconds, bool IdealEstimated);

/// <param name="Stops">How often the machine went down (a fault stopped it).</param>
/// <param name="MttrMinutes">Mean time to repair: down minutes per stop.</param>
/// <param name="MtbfMinutes">Mean time between failures: running minutes per stop.</param>
public sealed record MaintResult(int Stops, double? MttrMinutes, double? MtbfMinutes);

public sealed record ShiftReport(string Machine, string Day, string Shift, int ShiftIndex, DateTimeOffset From, DateTimeOffset To,
    bool InProgress, StateTotals Totals, OeeResult Oee, PartsResult Parts, MaintResult Maint,
    List<StateSpan> Timeline, List<LossRow> Losses, List<FaultRow> Faults, List<string> Notes);

/// <summary>One shift's headline numbers, for the week view.</summary>
public sealed record ShiftRow(string Day, string Shift, int ShiftIndex, DateTimeOffset From, bool InProgress, double? Oee, double? Availability,
    double? Performance, double? Quality, int Parts, double DownMinutes, double WatchedMinutes, string? TopLoss);

public sealed record FaultReport(string Machine, DateTimeOffset From, DateTimeOffset To, StateTotals Totals, MaintResult Maint,
    List<LossRow> Losses, List<FaultRow> Faults, List<ShiftRow> Shifts, DateTimeOffset? AlarmsKnownSince, List<string> Notes);

/// <summary>
/// Turns the production log (and the alarm occurrences) into the shift report with OEE, and the fault summary
/// for maintenance. Times are the server's local clock, as the shifts are.
/// </summary>
public sealed class ReportBuilder
{
    /// <summary>No log line for this long means PLC-Ki was not running (it writes at least one a minute).</summary>
    public static readonly TimeSpan Gap = TimeSpan.FromSeconds(150);

    private readonly MachineModel _model;
    private readonly ProductionLog _log;
    private readonly IAlarmSpans? _alarms;
    private readonly Func<ReportSettings> _settings;

    public ReportBuilder(MachineModel model, ProductionLog log, IAlarmSpans? alarms, Func<ReportSettings> settings)
    {
        _model = model;
        _log = log;
        _alarms = alarms;
        _settings = settings;
    }

    // ---------------------------------------------------------------- shifts and their times

    public static (DateTimeOffset From, DateTimeOffset To) Window(DateOnly day, ShiftDef s)
    {
        var start = ShiftDef.ParseTime(s.Start) ?? TimeSpan.Zero;
        var end = ShiftDef.ParseTime(s.End) ?? TimeSpan.Zero;
        var d0 = day.ToDateTime(TimeOnly.MinValue);
        var a = d0 + start;
        var b = (end <= start ? d0.AddDays(1) : d0) + end;
        return (Local(a), Local(b));
    }

    private static DateTimeOffset Local(DateTime t) => new(t, TimeZoneInfo.Local.GetUtcOffset(t));

    /// <summary>The shift that contains <paramref name="now"/>: its day (the day it started) and index. Null when no shift covers that time.</summary>
    public static (DateOnly Day, int Index)? ShiftAt(ReportSettings cfg, DateTimeOffset now)
    {
        var today = DateOnly.FromDateTime(now.ToLocalTime().DateTime);
        foreach (var day in new[] { today, today.AddDays(-1) })
            for (int i = 0; i < cfg.Shifts.Count; i++)
            {
                var (a, b) = Window(day, cfg.Shifts[i]);
                if (now >= a && now < b) return (day, i);
            }
        return null;
    }

    /// <summary>The last shift that has ended before <paramref name="now"/>.</summary>
    public static (DateOnly Day, int Index)? LastShift(ReportSettings cfg, DateTimeOffset now)
    {
        var today = DateOnly.FromDateTime(now.ToLocalTime().DateTime);
        (DateOnly, int)? best = null;
        DateTimeOffset bestEnd = DateTimeOffset.MinValue;
        foreach (var day in new[] { today, today.AddDays(-1), today.AddDays(-2) })
            for (int i = 0; i < cfg.Shifts.Count; i++)
            {
                var (_, b) = Window(day, cfg.Shifts[i]);
                if (b <= now && b > bestEnd) { bestEnd = b; best = (day, i); }
            }
        return best;
    }

    // ---------------------------------------------------------------- state over time

    /// <summary>The machine's states between from and to (clipped to now), with "off" where PLC-Ki was not watching.</summary>
    public static List<StateSpan> Timeline(List<ProdRecord> records, DateTimeOffset from, DateTimeOffset to, DateTimeOffset now)
    {
        var end = to < now ? to : now;
        var raw = new List<StateSpan>();
        string? cur = null, why = null, alarm = null;
        DateTimeOffset curFrom = default, last = default;
        bool any = false;

        void Close(DateTimeOffset at)
        {
            if (cur != null && at > curFrom) raw.Add(new StateSpan(curFrom, at, cur, why, alarm));
            cur = null;
        }

        foreach (var r in records)
        {
            if (any && r.T - last > Gap)
            {
                var stop = last + ProductionLog.AliveEvery;
                if (stop > r.T) stop = r.T;
                Close(stop);
                raw.Add(new StateSpan(stop, r.T, "off", "PLC-Ki was not running.", null));
            }
            if (r.K == "start") Close(r.T);
            if (r.K == "state" && r.S != null)
            {
                Close(r.T);
                cur = r.S; why = r.Why; alarm = r.Alarm; curFrom = r.T;
            }
            last = r.T;
            any = true;
        }
        if (any)
        {
            var stop = now - last > Gap ? last + ProductionLog.AliveEvery : now;
            Close(stop);
            if (stop < end) raw.Add(new StateSpan(stop, end, "off", "PLC-Ki was not running.", null));
        }

        // Clip to the window; before the first record it was not watched either
        var list = new List<StateSpan>();
        foreach (var s in raw)
        {
            var a = s.From < from ? from : s.From;
            var b = s.To > end ? end : s.To;
            if (b > a) list.Add(s with { From = a, To = b });
        }
        if (end > from)
        {
            var first = list.Count > 0 ? list[0].From : end;
            if (first > from) list.Insert(0, new StateSpan(from, first, "off", "PLC-Ki was not watching yet.", null));
        }
        // Join neighbours with the same state and reason
        var merged = new List<StateSpan>();
        foreach (var s in list)
        {
            if (merged.Count > 0 && merged[^1].State == s.State && merged[^1].Why == s.Why && merged[^1].To == s.From)
                merged[^1] = merged[^1] with { To = s.To };
            else merged.Add(s);
        }
        return merged;
    }

    public static StateTotals Totals(List<StateSpan> spans)
    {
        double M(string st) => Math.Round(spans.Where(s => s.State == st).Sum(s => s.Minutes), 2);
        return new StateTotals(M("running"), M("idle"), M("down"), M("nolink"), M("off"));
    }

    public static List<LossRow> Losses(List<StateSpan> spans, int max = 10) =>
        spans.Where(s => s.State is "idle" or "down" or "nolink")
            .GroupBy(s => (s.State, Why: s.Why ?? "", s.Alarm))
            .Select(g => new LossRow(g.Key.State, g.Key.Why, g.Key.Alarm, g.Count(), Math.Round(g.Sum(s => s.Minutes), 2)))
            .OrderByDescending(x => x.Minutes).Take(max).ToList();

    public static MaintResult Maint(List<StateSpan> spans, StateTotals t)
    {
        int stops = 0;
        string? prev = null;
        foreach (var s in spans)
        {
            if (s.State == "down" && prev != "down") stops++;
            if (s.State != "off") prev = s.State;
        }
        return new MaintResult(stops, stops > 0 ? Math.Round(t.Down / stops, 1) : null, stops > 0 ? Math.Round(t.Running / stops, 1) : null);
    }

    /// <summary>The machine's states over any window (the timeline strip on Plant), from the production log.</summary>
    public List<StateSpan> States(DateTimeOffset from, DateTimeOffset to, DateTimeOffset now) =>
        Timeline(_log.Records(from, to < now ? to : now), from, to, now);

    // ---------------------------------------------------------------- reports

    public ShiftReport Shift(DateOnly day, int index, DateTimeOffset now)
    {
        var cfg = _settings();
        index = Math.Clamp(index, 0, Math.Max(0, cfg.Shifts.Count - 1));
        var shift = cfg.Shifts.Count > 0 ? cfg.Shifts[index] : new ShiftDef { Name = "Day", Start = "00:00", End = "00:00" };
        var (from, to) = Window(day, shift);
        var records = _log.Records(from, to < now ? to : now);
        var notes = new List<string>();

        var timeline = Timeline(records, from, to, now);
        var totals = Totals(timeline);
        var (parts, partNotes) = Parts(records, from, to, now, cfg);
        notes.AddRange(partNotes);

        // Planned stops (breaks) are taken from the idle time: a machine that ran through its break lost nothing
        double plannedStop = Math.Min(shift.PlannedStopMinutes * Fraction(from, to, now), totals.Idle);
        double planned = Math.Max(0, totals.Watched - plannedStop);
        double? avail = planned > 0 ? Math.Min(1, totals.Running / planned) : null;
        double? perf = null;
        if (parts.IdealCycleSeconds is { } ideal && totals.Running > 0)
        {
            var p = ideal * parts.Total / (totals.Running * 60);
            if (p > 1.05) notes.Add($"More parts than the ideal cycle time of {ideal:0.#} s allows: the ideal cycle time is probably too long.");
            perf = Math.Min(1, p);
        }
        double? qual = parts.BadKnown && parts.Total > 0 ? (double)parts.Good / parts.Total : null;
        double? oee = avail is { } a ? a * (perf ?? 1) * (qual ?? 1) : null;
        if (avail != null && perf == null) notes.Add("Performance is not known yet (no ideal cycle time and too few cycles seen), so OEE counts it as 100 %.");
        if (avail != null && qual == null) notes.Add("No reject counter is set, so OEE counts quality as 100 %.");
        if (totals.Off > 1) notes.Add($"PLC-Ki was not watching for {Mins(totals.Off)} of this shift; that time is left out.");
        if (totals.NoLink > 1) notes.Add($"No PLC connection for {Mins(totals.NoLink)}; that time is left out of OEE.");

        var oeeRes = new OeeResult(R(avail), R(perf), R(qual), R(oee), Math.Round(planned, 1), Math.Round(totals.Running, 1), Math.Round(plannedStop, 1));
        var faults = Faults(from, to < now ? to : now);
        if (_alarms != null && (_alarms.KnownSince == null || _alarms.KnownSince > from))
            notes.Add(_alarms.KnownSince == null ? "No alarms recorded yet." : $"Alarms are known since {_alarms.KnownSince.Value.ToLocalTime():d MMM HH:mm}, so earlier ones are not listed.");
        return new ShiftReport(_model.Name, day.ToString("yyyy-MM-dd"), shift.Name, index, from, to, now < to, totals, oeeRes, parts,
            Maint(timeline, totals), timeline, Losses(timeline), faults, notes);
    }

    /// <summary>Every shift from <paramref name="days"/> days ago up to now, with headline numbers, plus the fault summary over the whole time.</summary>
    public FaultReport Faults(int days, DateTimeOffset now)
    {
        var cfg = _settings();
        // A rolling window, the same as History's: 24 h is the last 24 hours, 1 year the last 365 days
        days = Math.Clamp(days, 1, 366);
        var from = now.AddDays(-days);
        var today = DateOnly.FromDateTime(now.ToLocalTime().DateTime);
        // The shift table lists the shifts of the last month at most, so a year stays quick to make
        var firstDay = DateOnly.FromDateTime(from.ToLocalTime().DateTime);
        var shiftsCut = firstDay < today.AddDays(-30);
        if (shiftsCut) firstDay = today.AddDays(-30);
        var records = _log.Records(from, now);
        var timeline = Timeline(records, from, now, now);
        var totals = Totals(timeline);

        var rows = new List<ShiftRow>();
        for (var d = firstDay; d <= today; d = d.AddDays(1))
            for (int i = 0; i < cfg.Shifts.Count; i++)
            {
                var (a, b) = Window(d, cfg.Shifts[i]);
                if (a >= now) continue;
                var r = Shift(d, i, now);
                if (r.Totals.Watched + r.Totals.NoLink < 0.5) continue;    // not watched at all
                var top = r.Losses.FirstOrDefault();
                rows.Add(new ShiftRow(r.Day, r.Shift, i, r.From, r.InProgress, r.Oee.Value, r.Oee.Availability, r.Oee.Performance, r.Oee.Quality,
                    r.Parts.Total, r.Totals.Down, r.Totals.Watched, top?.Why));
            }

        var notes = new List<string>();
        var known = _alarms?.KnownSince;
        if (_alarms == null) notes.Add("Alarm history is not available, so only the downtime reasons are listed.");
        else if (known == null || known > from) notes.Add(known == null ? "No alarms recorded yet." : $"Alarms are known since {known.Value.ToLocalTime():d MMM HH:mm}.");
        if (totals.Off > 1) notes.Add($"PLC-Ki was not watching for {Mins(totals.Off)} of this time.");
        if (shiftsCut) notes.Add("The shift table lists the shifts of the last 31 days.");
        return new FaultReport(_model.Name, from, now, totals, Maint(timeline, totals), Losses(timeline, 15), Faults(from, now),
            rows.OrderByDescending(r => r.From).ToList(), known, notes);
    }

    private List<FaultRow> Faults(DateTimeOffset from, DateTimeOffset to)
    {
        if (_alarms == null) return new();
        var spans = _alarms.Spans(from, to).Where(s => s.Came <= to && (s.Went ?? to) >= from).ToList();
        var defs = _model.Alarms.ToDictionary(a => a.Id, StringComparer.Ordinal);
        return spans.GroupBy(s => s.AlarmId).Select(g =>
        {
            double Overlap(AlarmSpan s) => Math.Max(0, ((s.Went ?? to) < to ? (s.Went ?? to) : to).Subtract(s.Came > from ? s.Came : from).TotalMinutes);
            var def = defs.GetValueOrDefault(g.Key);
            var lastOne = g.OrderBy(s => s.Came).Last();
            return new FaultRow(g.Key, lastOne.Text, lastOne.Severity, def?.Location, g.Count(), Math.Round(g.Sum(Overlap), 2),
                Math.Round(g.Max(Overlap), 2), lastOne.Came, g.Any(s => s.Went == null), def?.Causes ?? new(), def?.Fix ?? new());
        })
        .OrderByDescending(f => f.Severity == "FAULT").ThenByDescending(f => f.Minutes).ThenByDescending(f => f.Count).ToList();
    }

    private (PartsResult, List<string>) Parts(List<ProdRecord> records, DateTimeOffset from, DateTimeOffset to, DateTimeOffset now, ReportSettings cfg)
    {
        var notes = new List<string>();
        var inWin = records.Where(r => r.T >= from && r.T < to).ToList();
        var counts = inWin.Where(r => r.K == "count").ToList();
        var cycles = inWin.Where(r => r.K == "cycle" && r.Sec > 0).ToList();
        bool goodKnown = counts.Any(r => r.Good != null);
        bool badKnown = counts.Any(r => r.Bad != null);
        int bad = counts.Sum(r => r.Bad ?? 0);
        int good = goodKnown ? counts.Sum(r => r.Good ?? 0) : Math.Max(0, cycles.Count - bad);
        int total = good + bad;
        if (!goodKnown)
            notes.Add(cycles.Count > 0 || _model.Sequences.Count > 0
                ? "Parts are counted as finished sequence cycles (no part counter is set)."
                : "No part counter is set and the model has no sequence, so parts are not counted.");

        double? avg = cycles.Count > 0 ? Math.Round(cycles.Average(c => c.Sec!.Value), 1) : null;
        double? ideal = cfg.IdealCycleSeconds;
        bool estimated = false;
        if (ideal == null)
        {
            // The fastest cycles seen in the last week (10th percentile), so one odd short cycle does not set it
            var week = _log.Records(now.AddDays(-7), now).Where(r => r.K == "cycle" && r.Sec > 0).Select(r => r.Sec!.Value).OrderBy(x => x).ToList();
            if (week.Count >= 5)
            {
                ideal = Math.Round(week[(int)Math.Floor((week.Count - 1) * 0.1)], 1);
                estimated = true;
            }
        }
        return (new PartsResult(total, good, bad, goodKnown ? "counter" : "cycles", badKnown, cycles.Count, avg, ideal, estimated), notes);
    }

    /// <summary>The part of the shift that has passed (for prorating planned stops in a running shift).</summary>
    private static double Fraction(DateTimeOffset from, DateTimeOffset to, DateTimeOffset now) =>
        now >= to ? 1 : now <= from ? 0 : (now - from).TotalMinutes / (to - from).TotalMinutes;

    private static double? R(double? x) => x is { } v ? Math.Round(v, 4) : null;
    private static string Mins(double m) => m < 90 ? $"{Math.Round(m)} min" : $"{m / 60:0.#} h";
}
