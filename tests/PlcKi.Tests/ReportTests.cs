using PlcKi.Core.Reports;
using PlcKi.Core.Status;
using Xunit;

/// <summary>Shift, OEE and fault reports: how the machine state is classified, counted, kept and turned into numbers.</summary>
public class ReportTests
{
    static MachineStatus At(string snapshot, DateTimeOffset t, Action<Snapshot>? change = null)
    {
        var s = TestData.Snapshot(snapshot, t);
        change?.Invoke(s);
        return TestData.Evaluate(s);
    }

    [Fact]
    public void Classifies_running_down_idle_and_no_link()
    {
        var t = DateTimeOffset.Now;
        var halted = At("step10-xv101-not-open", t);
        var (state, why, alarm) = ProductionLog.Classify(halted, TestData.Model);
        Assert.Equal("down", state);
        Assert.Equal("A02", alarm);
        Assert.StartsWith("A02", why);

        Assert.Equal("running", ProductionLog.Classify(At("step30-mix-heat", t), TestData.Model).State);

        var home = TestData.Model.Sequences[0].Steps[0].No;
        var idle = At("step30-mix-heat", t, s => s.Values[TestData.Model.Sequences[0].StepTag] = (double)home);
        Assert.Equal("idle", ProductionLog.Classify(idle, TestData.Model).State);

        var empty = new Snapshot { Time = t };
        Assert.Equal("nolink", ProductionLog.Classify(TestData.Evaluate(empty, "disconnected"), TestData.Model).State);
    }

    [Fact]
    public void Counts_parts_from_a_counter_and_survives_a_counter_reset()
    {
        var cfg = new ReportSettings { GoodCountTag = "PartsOk" };
        var log = new ProductionLog(TestData.Model, null, () => cfg);
        var t0 = new DateTimeOffset(2026, 11, 4, 7, 0, 0, TimeSpan.Zero);
        double[] counter = { 100, 101, 103, 0, 2, 2 };     // +1 +2, reset to 0 (+0), +2
        for (int i = 0; i < counter.Length; i++)
            log.Add(At("step30-mix-heat", t0.AddSeconds(i * 30), s => s.Values["PartsOk"] = counter[i]));
        log.Add(At("step30-mix-heat", t0.AddMinutes(5)));   // flush after a minute
        var total = log.Records(t0, t0.AddHours(1)).Where(r => r.K == "count").Sum(r => r.Good ?? 0);
        Assert.Equal(5, total);
    }

    [Fact]
    public void A_sequence_back_in_its_first_step_is_one_cycle()
    {
        var cfg = new ReportSettings();
        var log = new ProductionLog(TestData.Model, null, () => cfg);
        var seq = TestData.Model.Sequences[0];
        var home = seq.Steps[0].No;
        var other = seq.Steps[1].No;
        var t0 = new DateTimeOffset(2026, 11, 4, 7, 0, 0, TimeSpan.Zero);
        int[] steps = { home, other, other, home, other, home };
        for (int i = 0; i < steps.Length; i++)
            log.Add(At("step30-mix-heat", t0.AddSeconds(i * 40), s => s.Values[seq.StepTag] = (double)steps[i]));
        var cycles = log.Records(t0, t0.AddHours(1)).Where(r => r.K == "cycle").ToList();
        Assert.Equal(2, cycles.Count);
        Assert.Equal(80, cycles[0].Sec);
        Assert.Equal(40, cycles[1].Sec);
    }

    [Fact]
    public void Shift_report_computes_oee_from_time_and_parts()
    {
        var day = new DateOnly(2026, 11, 2);
        var cfg = new ReportSettings { Shifts = { }, IdealCycleSeconds = 180 };
        cfg.Shifts.Clear();
        cfg.Shifts.Add(new ShiftDef { Name = "Early", Start = "06:00", End = "14:00" });
        var log = new ProductionLog(TestData.Model, null, () => cfg);
        var (from, to) = ReportBuilder.Window(day, cfg.Shifts[0]);

        // 6 h running, 1 h down (A02), 1 h idle; a line every minute so there is no gap
        var recs = new List<ProdRecord> { new(from, "start"), new(from, "state", "running") };
        recs.Add(new ProdRecord(from.AddHours(6), "state", "down", "A02 Valve did not open", "A02"));
        recs.Add(new ProdRecord(from.AddHours(7), "state", "idle", "waiting"));
        for (var t = from.AddMinutes(1); t < to; t = t.AddMinutes(1)) recs.Add(new ProdRecord(t, "alive"));
        for (int i = 0; i < 100; i++) recs.Add(new ProdRecord(from.AddSeconds(200 * (i + 1)), "cycle", Seq: "S1", Sec: 200));
        log.AddRaw(recs.OrderBy(r => r.T));

        var b = new ReportBuilder(TestData.Model, log, null, () => cfg);
        var r = b.Shift(day, 0, to.AddHours(1));
        Assert.False(r.InProgress);
        Assert.Equal(360, r.Totals.Running, 1);
        Assert.Equal(60, r.Totals.Down, 1);
        Assert.Equal(60, r.Totals.Idle, 1);
        Assert.Equal(0.75, r.Oee.Availability!.Value, 3);
        Assert.Equal(180.0 * 100 / (360 * 60), r.Oee.Performance!.Value, 3);
        Assert.Null(r.Oee.Quality);
        Assert.Equal(0.75 * 180.0 * 100 / (360 * 60), r.Oee.Value!.Value, 3);
        Assert.Equal(100, r.Parts.Total);
        Assert.Equal("cycles", r.Parts.From);
        Assert.Equal(1, r.Maint.Stops);
        Assert.Equal(60, r.Maint.MttrMinutes);
        Assert.Equal("A02", r.Losses.First(l => l.State == "down").Alarm);
    }

    [Fact]
    public void Planned_stops_come_out_of_idle_time()
    {
        var day = new DateOnly(2026, 11, 2);
        var cfg = new ReportSettings();
        cfg.Shifts.Clear();
        cfg.Shifts.Add(new ShiftDef { Name = "Early", Start = "06:00", End = "14:00", PlannedStopMinutes = 30 });
        var log = new ProductionLog(TestData.Model, null, () => cfg);
        var (from, to) = ReportBuilder.Window(day, cfg.Shifts[0]);
        var recs = new List<ProdRecord> { new(from, "state", "running"), new(from.AddHours(7), "state", "idle", "break") };
        for (var t = from.AddMinutes(1); t < to; t = t.AddMinutes(1)) recs.Add(new ProdRecord(t, "alive"));
        log.AddRaw(recs.OrderBy(r => r.T));
        var r = new ReportBuilder(TestData.Model, log, null, () => cfg).Shift(day, 0, to.AddHours(1));
        Assert.Equal(30, r.Oee.PlannedStopMinutes, 1);
        Assert.Equal(420.0 / 450, r.Oee.Availability!.Value, 3);
    }

    [Fact]
    public void A_gap_in_the_log_is_time_not_watched()
    {
        var from = new DateTimeOffset(2026, 11, 2, 6, 0, 0, TimeSpan.Zero);
        var recs = new List<ProdRecord> { new(from, "start"), new(from, "state", "running"), new(from.AddMinutes(1), "alive") };
        // PLC-Ki stopped for an hour, then started again
        recs.Add(new ProdRecord(from.AddMinutes(61), "start"));
        recs.Add(new ProdRecord(from.AddMinutes(61), "state", "running"));
        recs.Add(new ProdRecord(from.AddMinutes(62), "alive"));
        var spans = ReportBuilder.Timeline(recs, from, from.AddMinutes(62), from.AddMinutes(62));
        var totals = ReportBuilder.Totals(spans);
        Assert.Equal(59, totals.Off, 1);
        Assert.Equal(3, totals.Running, 1);
    }

    [Fact]
    public void Night_shift_runs_past_midnight()
    {
        var cfg = new ReportSettings();     // Early 6-14, Late 14-22, Night 22-6
        var t = new DateTimeOffset(new DateTime(2026, 11, 5, 3, 0, 0), TimeZoneInfo.Local.GetUtcOffset(new DateTime(2026, 11, 5, 3, 0, 0)));
        var at = ReportBuilder.ShiftAt(cfg, t);
        Assert.NotNull(at);
        Assert.Equal(new DateOnly(2026, 11, 4), at!.Value.Day);
        Assert.Equal("Night", cfg.Shifts[at.Value.Index].Name);
        var (a, b) = ReportBuilder.Window(at.Value.Day, cfg.Shifts[at.Value.Index]);
        Assert.Equal(TimeSpan.FromHours(8), b - a);
    }

    [Fact]
    public void Log_is_kept_on_disk_one_file_per_day()
    {
        var dir = Path.Combine(Path.GetTempPath(), "plcki-reports-" + Guid.NewGuid().ToString("N"));
        try
        {
            var cfg = new ReportSettings();
            var t0 = DateTimeOffset.Now.AddMinutes(-10);
            var log = new ProductionLog(TestData.Model, dir, () => cfg);
            log.Add(At("step30-mix-heat", t0));
            log.Add(At("step10-xv101-not-open", t0.AddSeconds(5)));
            Assert.Single(Directory.GetFiles(dir, "*.jsonl"));

            // A new PLC-Ki (after a restart) reads what the old one wrote
            var again = new ProductionLog(TestData.Model, dir, () => cfg);
            var recs = again.Records(t0.AddMinutes(-1), t0.AddMinutes(1));
            Assert.Contains(recs, r => r.K == "state" && r.S == "running");
            Assert.Contains(recs, r => r.K == "state" && r.S == "down" && r.Alarm == "A02");
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void Fault_summary_groups_alarm_occurrences()
    {
        var memory = new PlcKi.Core.Memory.AlarmMemory(null);
        var alarms = new MemoryAlarmSpans(memory);
        var t0 = DateTimeOffset.Now.AddMinutes(-30);
        memory.Observe(At("step30-mix-heat", t0));
        memory.Observe(At("step10-xv101-not-open", t0.AddMinutes(1)));    // A02 comes
        memory.Observe(At("step30-mix-heat", t0.AddMinutes(4)));          // and goes after 3 min
        memory.Observe(At("step10-xv101-not-open", t0.AddMinutes(10)));   // comes again
        var spans = alarms.Spans(t0, DateTimeOffset.Now);
        Assert.Equal(2, spans.Count(s => s.AlarmId == "A02"));
        Assert.Null(spans.Last(s => s.AlarmId == "A02").Went);

        var cfg = new ReportSettings();
        var log = new ProductionLog(TestData.Model, null, () => cfg);
        var report = new ReportBuilder(TestData.Model, log, alarms, () => cfg).Faults(2, DateTimeOffset.Now);
        var a02 = report.Faults.Single(f => f.Id == "A02");
        Assert.Equal(2, a02.Count);
        Assert.True(a02.StillOn);
        Assert.NotEmpty(a02.Fix);
    }

    [Fact]
    public void Summary_windows_are_rolling_and_reach_a_year()
    {
        var cfg = new ReportSettings();
        var log = new ProductionLog(TestData.Model, null, () => cfg);
        var b = new ReportBuilder(TestData.Model, log, null, () => cfg);
        var now = DateTimeOffset.Now;
        // "24 h" is the last 24 hours, not since midnight; "1 year" is not cut to 92 days
        Assert.Equal(now.AddDays(-1), b.Faults(1, now).From);
        var year = b.Faults(365, now);
        Assert.Equal(now.AddDays(-365), year.From);
        Assert.Contains(year.Notes, n => n.Contains("last 31 days"));
        Assert.DoesNotContain(b.Faults(7, now).Notes, n => n.Contains("last 31 days"));
    }

    [Fact]
    public void Settings_reject_bad_times()
    {
        var s = new ReportSettings();
        Assert.Empty(s.Problems());
        s.Shifts[0].Start = "25:00";
        Assert.NotEmpty(s.Problems());
    }
}
