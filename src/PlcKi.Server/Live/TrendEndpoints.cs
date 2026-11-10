using PlcKi.Core.Reports;
using PlcKi.Core.Model;
using PlcKi.Core.Status;

namespace PlcKi.Server.Live;

/// <summary>
/// GET /api/trend: values over time for the charts. A window the live buffer covers comes from memory as before; a longer one,
/// or one that reaches back before PLC-Ki started (after a restart), comes from the saved values (ValueLog). Read-only.
/// Parameters: minutes, hours or days back from now; or from and to as ms since 1970. tags is a comma list of tag ids.
/// </summary>
public static class TrendEndpoints
{
    public static readonly TimeSpan Longest = TimeSpan.FromDays(3650);
    static readonly TimeSpan Cached = TimeSpan.FromSeconds(5);
    static readonly object Lock = new();
    static readonly Dictionary<string, (DateTimeOffset At, TrendData Data)> Cache = new();

    public static void Map(WebApplication app)
    {
        app.MapGet("/api/trend", (MachineHistory history, MachineModel m, ValueLog values, EventLog events,
            double? minutes, double? hours, double? days, long? from, long? to, string? tags) =>
        {
            var ids = string.IsNullOrWhiteSpace(tags) ? null : tags.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            return Results.Ok(Get(m, history, values, events, DateTimeOffset.Now, minutes, hours, days, from, to, ids));
        });
    }

    /// <summary>The cycle list behind the chips "This cycle" and "Compare with the previous cycle".</summary>
    public static void MapCycles(WebApplication app)
    {
        // GET /api/trend/cycles: the running cycle (if any) and the finished ones, newest first, with start and end in ms.
        app.MapGet("/api/trend/cycles", (PlcKi.Server.Reports.ReportService reports, MachineModel m, ValueLog values) =>
        {
            var now = DateTimeOffset.Now;
            var cfg = reports.Settings;
            var list = Cycles.List(m, cfg, reports.Log.Records(now.AddDays(-7), now), values, now);
            return Results.Ok(new
            {
                sequence = Cycles.Sequence(m, cfg)?.Name,
                cycles = list.Select(c => new { start = c.Start, end = c.End, sec = c.Sec, running = c.Running }),
            });
        });
    }

    public static TrendData Get(MachineModel m, MachineHistory h, ValueLog? values, EventLog? events, DateTimeOffset now,
        double? minutes, double? hours, double? days, long? from, long? to, IReadOnlyCollection<string>? ids)
    {
        var end = to is { } t ? DateTimeOffset.FromUnixTimeMilliseconds(Math.Clamp(t, 0, now.ToUnixTimeMilliseconds())) : now;
        TimeSpan window = from is { } f ? end - DateTimeOffset.FromUnixTimeMilliseconds(Math.Max(0, f))
            : days is { } d ? TimeSpan.FromDays(d)
            : hours is { } hr ? TimeSpan.FromHours(hr)
            : TimeSpan.FromMinutes(minutes ?? h.KeepValues.TotalMinutes);
        var shortest = from != null ? TimeSpan.FromSeconds(5) : TimeSpan.FromMinutes(1);       // a cycle can be short
        if (window < shortest) window = shortest;
        if (window > Longest) window = Longest;
        var start = end - window;

        // The live buffer, when it holds all of the window up to now
        bool live = to == null && window <= h.KeepValues;
        bool covers = live && h.OldestValueTime is { } oldest && oldest <= start + TimeSpan.FromSeconds(5);
        bool saved = values?.OldestDay() != null;
        if (!saved || covers || (live && !SavedReaches(values!, start)))
            return Trend.Build(m, h, live ? window : TimeSpan.FromMinutes(Math.Min(window.TotalMinutes, h.KeepValues.TotalMinutes)), now, ids) with { SavedFrom = SavedFrom(values) };

        var key = string.Join('|', window.Ticks / TimeSpan.TicksPerSecond, to, ids == null ? "" : string.Join(',', ids));
        lock (Lock)
            if (Cache.TryGetValue(key, out var hit) && now - hit.At < Cached) return hit.Data;
        var data = Trend.BuildFromDisk(m, h, values!, events, start, end, ids);
        lock (Lock)
        {
            if (Cache.Count > 32) Cache.Clear();
            Cache[key] = (now, data);
        }
        return data;
    }

    /// <summary>When the oldest saved values start (the chart offers the longer windows as far back as this), ms since 1970.</summary>
    static long? SavedFrom(ValueLog? values) =>
        values?.OldestDay() is { } d ? new DateTimeOffset(d.ToDateTime(TimeOnly.MinValue)).ToUnixTimeMilliseconds() : null;

    /// <summary>Whether the saved values go back as far as the start of the window (a day file for that day or an earlier one).</summary>
    static bool SavedReaches(ValueLog values, DateTimeOffset start) =>
        values.OldestDay() is { } d && d <= DateOnly.FromDateTime(start.LocalDateTime);
}
