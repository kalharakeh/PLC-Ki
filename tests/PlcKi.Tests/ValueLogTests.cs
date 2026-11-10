using PlcKi.Core.Status;
using PlcKi.Core.Storage;
using PlcKi.Server.Live;
using PlcKi.Server.Storage;
using Xunit;

/// <summary>Values on disk: the full-rate and minute files, thinning, reading a range back, the limits and the trend API.</summary>
public class ValueLogTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "plcki-values-" + Guid.NewGuid().ToString("N"));
    public ValueLogTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private static readonly string[] Ids = { "Temp", "Level", "Step" };
    private string Machine => ValueLog.FolderFor(_dir, "helium");
    private ValueLog NewLog() => new(Machine, Ids);

    private static DateTimeOffset L(int day, int h, int m = 0, int s = 0)
    {
        var d = new DateTime(2026, 11, day, h, m, s);
        return new DateTimeOffset(d, TimeZoneInfo.Local.GetUtcOffset(d));
    }

    private static Snapshot Snap(DateTimeOffset t, double temp, double level = 50, double step = 10) =>
        new() { Time = t, Values = { ["Temp"] = temp, ["Level"] = level, ["Step"] = step, ["Pump"] = true } };

    private static string[] Lines(string f) => File.ReadAllLines(f);

    [Fact]
    public void The_full_file_has_the_whole_set_each_minute_and_only_the_changes_between()
    {
        var log = NewLog();
        log.Add(Snap(L(10, 8, 0, 0), 20.0));
        log.Add(Snap(L(10, 8, 0, 1), 20.5));             // Temp changed, Level and Step did not
        log.Add(Snap(L(10, 8, 0, 2), 20.5));             // nothing changed: no line
        log.Add(Snap(L(10, 8, 1, 0), 20.5));             // a new minute: the whole set again
        var lines = Lines(Path.Combine(Machine, "full", "2026-11-10.jsonl"));
        Assert.Equal(3, lines.Length);
        Assert.Contains("\"k\":1", lines[0]);
        Assert.Contains("\"Level\":50", lines[0]);
        Assert.DoesNotContain("Level", lines[1]);
        Assert.Contains("\"Temp\":20.5", lines[1]);
        Assert.Contains("\"k\":1", lines[2]);
        Assert.DoesNotContain("Pump", lines[0]);          // only numbers are saved
    }

    [Fact]
    public void A_minute_is_thinned_to_lowest_highest_and_last()
    {
        var log = NewLog();
        for (int s = 0; s < 60; s++) log.Add(Snap(L(10, 8, 0, s), s == 30 ? 80 : 20 + s * 0.1));
        Assert.False(File.Exists(Path.Combine(Machine, "2026-11-10.jsonl")));       // the minute is still open
        log.Add(Snap(L(10, 8, 1, 0), 25));
        var line = Lines(Path.Combine(Machine, "2026-11-10.jsonl")).Single();
        Assert.Contains("\"Temp\":[20,80,25.9]", line);
        Assert.Contains("\"Step\":[10,10,10]", line);
    }

    [Fact]
    public void A_stop_writes_the_open_minute_and_a_restart_starts_with_a_whole_set()
    {
        var a = NewLog();
        a.Add(Snap(L(10, 8, 0, 5), 21));
        a.Flush();
        Assert.Single(Lines(Path.Combine(Machine, "2026-11-10.jsonl")));
        var b = NewLog();
        b.Add(Snap(L(10, 8, 0, 40), 22));
        Assert.Contains("\"k\":1", Lines(Path.Combine(Machine, "full", "2026-11-10.jsonl")).Last());
    }

    [Fact]
    public void A_window_in_the_middle_of_a_day_knows_the_values_held_from_before_it()
    {
        var log = NewLog();
        for (int m = 0; m < 30; m++)
            for (int s = 0; s < 60; s += 10)
                log.Add(Snap(L(10, 8, m, s), 20, level: m < 15 ? 50 : 60));     // Level changes once, at 08:15
        var range = new ValueLog(Machine, Ids).Read(L(10, 8, 10), L(10, 8, 25), new[] { "Level", "Temp" }, 15);
        Assert.Equal("full", range.Tier);
        var level = range.Series["Level"];
        Assert.Equal(50, level[0]!.Value.Last);          // held from before the window
        Assert.Equal(60, level[^1]!.Value.Last);
        Assert.All(level, b => Assert.NotNull(b));
    }

    [Fact]
    public void A_value_that_does_not_change_holds_between_lines_but_a_silence_is_a_gap()
    {
        var log = NewLog();
        for (int s = 0; s < 300; s++) log.Add(Snap(L(10, 8, 0).AddSeconds(s), 20));          // constant: a line only every minute
        for (int s = 0; s < 120; s++) log.Add(Snap(L(10, 8, 10).AddSeconds(s), 20));         // after 5 minutes without PLC-Ki
        var temp = log.Read(L(10, 8, 0), L(10, 8, 12), new[] { "Temp" }, 144).Series["Temp"];   // 5-second slices
        Assert.All(temp.Take(60), b => Assert.NotNull(b));                                    // 08:00 to 08:05 held
        Assert.All(temp.Skip(66).Take(20), b => Assert.Null(b));                              // 08:05:30 to 08:07: nothing
        Assert.NotNull(temp[^1]);
        // Just after the last line, inside the minute it still holds
        Assert.NotNull(log.Read(L(10, 8, 11, 50), L(10, 8, 12), new[] { "Temp" }, 10).Series["Temp"].Last());
    }

    [Fact]
    public void A_long_window_reads_the_minute_files_and_keeps_a_spike()
    {
        var log = NewLog();
        for (int day = 4; day <= 10; day++)
            for (int h = 0; h < 24; h++)
                for (int m = 0; m < 60; m += 5)
                    log.Add(Snap(L(day, h, m), day == 7 && h == 12 && m == 30 ? 99 : 20));
        log.Flush();
        var range = log.Read(L(4, 0), L(10, 23, 59), new[] { "Temp" }, 100);
        Assert.Equal("thin", range.Tier);
        var temp = range.Series["Temp"];
        Assert.Equal(99, temp.Where(b => b != null).Max(b => b!.Value.Max));
        Assert.Equal(20, temp.Where(b => b != null).Min(b => b!.Value.Min));
        Assert.Equal(1440 * 7 / 100.0 * 60_000, range.BucketMs, 0.5 * 60_000);
    }

    [Fact]
    public void A_restarted_log_reads_what_the_old_one_wrote_and_skips_a_cut_off_line()
    {
        var log = NewLog();
        for (int s = 0; s < 120; s++) log.Add(Snap(L(10, 8, 0).AddSeconds(s), 20 + s));
        log.Flush();
        File.AppendAllText(Path.Combine(Machine, "full", "2026-11-10.jsonl"), "{\"t\":17900000000");
        File.AppendAllText(Path.Combine(Machine, "2026-11-10.jsonl"), "{\"t\":17900000000,\"v\":{\"Te");
        var fresh = NewLog();
        var full = fresh.Read(L(10, 8), L(10, 8, 2), new[] { "Temp" }, 10);
        Assert.Equal("full", full.Tier);
        Assert.Equal(139, full.Series["Temp"].Where(b => b != null).Max(b => b!.Value.Max));
        var thin = fresh.Read(L(10, 0), L(10, 23), new[] { "Temp" }, 10);
        Assert.Equal("thin", thin.Tier);
        Assert.Equal(20, thin.Series["Temp"].Where(b => b != null).Min(b => b!.Value.Min));
    }

    [Fact]
    public void A_trend_from_disk_after_a_restart_has_the_values_gaps_steps_and_few_points()
    {
        var log = NewLog();
        for (int h = 0; h < 6; h++)
            for (int m = 0; m < 60; m++)
            {
                if (h == 3) continue;                                  // PLC-Ki was off for an hour
                log.Add(Snap(L(10, h, m), 20 + m % 7, step: h < 2 ? 10 : 30));
            }
        log.Flush();
        var model = TestData.Model;
        var history = new MachineHistory();                            // memory is empty: a restart
        var data = TrendEndpoints.Get(model, history, new ValueLog(Machine, Ids), null, L(10, 6, 0), null, 6, null, null, null, new[] { "Temp" });
        Assert.Equal("disk", data.Source);
        var temp = data.Series.Single();
        Assert.InRange(temp.Points.Count, 50, 700);
        Assert.Contains(temp.Points, p => p[1] == null);               // the hour without values is a gap
        var seq = data.Steps.First(s => s.Spans.Count > 0);
        Assert.Equal(new[] { 10, 30 }, seq.Spans.Select(s => s.Step).Distinct());
        Assert.NotNull(data.SavedFrom);
    }

    [Fact]
    public void The_live_buffer_answers_when_it_covers_the_window_and_the_disk_when_it_does_not()
    {
        var now = L(10, 12, 0);
        var history = new MachineHistory(TimeSpan.FromMinutes(15));
        var log = NewLog();
        history.ValuesRecorded = log.Add;
        for (int s = 0; s < 600; s += 5) history.Add(Snap(now.AddSeconds(s - 600), 20 + s / 100.0), TestData.Status("step30-mix-heat"));
        var shortWindow = TrendEndpoints.Get(TestData.Model, history, log, null, now, 10, null, null, null, null, new[] { "Temp" });
        Assert.Equal("memory", shortWindow.Source);
        var longWindow = TrendEndpoints.Get(TestData.Model, history, log, null, now, null, 1, null, null, null, new[] { "Temp" });
        Assert.Equal("disk", longWindow.Source);
        Assert.NotNull(longWindow.ResolutionSec);
        // Nothing saved at all (no folder): the live buffer is all there is
        var none = TrendEndpoints.Get(TestData.Model, history, new ValueLog(null, Ids), null, now, null, 1, null, null, null, new[] { "Temp" });
        Assert.Equal("memory", none.Source);
    }

    [Fact]
    public void Storage_keeps_full_files_for_their_days_and_minute_files_for_theirs()
    {
        var log = NewLog();
        foreach (var day in new[] { 1, 5, 9, 10 })
        {
            log.Add(Snap(L(day, 8), 20));
            log.Add(Snap(L(day, 8, 1), 20));
        }
        log.Flush();
        var now = L(10, 12);
        var s = new StorageService(_dir, () => new RetentionLimits { ValueFullDays = 2, ValueDays = 7 }, values: log);

        var dry = s.Prune(now, dryRun: true);
        Assert.Contains(dry, r => r.Kind == "values-full" && r.Count == 2);       // 1st and 5th
        Assert.Contains(dry, r => r.Kind == "values" && r.Count == 1);            // the 1st
        Assert.Equal(4, ValueLog.DayFiles(Path.Combine(_dir, "values"), true).Count);

        s.Prune(now);
        Assert.Equal(new[] { 9, 10 }, ValueLog.DayFiles(Path.Combine(_dir, "values"), true).Select(d => d.Day.Day));
        Assert.Equal(new[] { 5, 9, 10 }, ValueLog.DayFiles(Path.Combine(_dir, "values"), false).Select(d => d.Day.Day));

        var row = s.Rows(now).Single(r => r.Kind == "values");
        Assert.Equal(3, row.Count);
        Assert.True(row.Bytes > 0);
        Assert.NotNull(row.Clear);

        var done = s.Clear("values", "kassem", now);
        Assert.Equal(5, done.Count);
        Assert.Empty(ValueLog.DayFiles(Path.Combine(_dir, "values"), false));
        // After a clear the next file starts with a whole set again
        log.Add(Snap(L(10, 13), 21));
        Assert.Contains("\"k\":1", Lines(Path.Combine(Machine, "full", "2026-11-10.jsonl"))[0]);
    }

    [Fact]
    public void The_value_limits_have_plain_ranges()
    {
        Assert.Null(RetentionLimits.Default.Problem());
        Assert.Equal(2, RetentionLimits.Default.ValueFullDays);
        Assert.Equal(90, RetentionLimits.Default.ValueDays);
        Assert.NotNull((RetentionLimits.Default with { ValueFullDays = 0 }).Problem());
        Assert.NotNull((RetentionLimits.Default with { ValueFullDays = 15 }).Problem());
        Assert.NotNull((RetentionLimits.Default with { ValueDays = 3 }).Problem());
        Assert.NotNull((RetentionLimits.Default with { ValueFullDays = 10, ValueDays = 7 }).Problem());
    }
}
