using PlcKi.Core.Reports;
using PlcKi.Core.Status;
using Xunit;

/// <summary>A CPU that is not in RUN: every text says the program is not running, and a failed mode read never says STOP.</summary>
public class CpuStopTests
{
    static readonly DateTimeOffset T0 = new(2026, 11, 2, 9, 12, 0, TimeSpan.Zero);

    static MachineStatus Eval(string snapshot, CpuStatus? cpu, string state = "connected", DateTimeOffset? t = null)
    {
        var snap = TestData.Snapshot(snapshot, t ?? T0.AddMinutes(5));
        return TestData.Engine.Evaluate(snap, new PlcLinkInfo(state, "192.168.10.10", null, snap.Time, TestData.Model.Tags.Count, 0), cpu);
    }

    static CpuStatus Stop(DateTimeOffset? since = null) => new("Stop", "RUN", since ?? T0);
    static CpuStatus Run() => new("Run", "RUN", T0);

    /// <summary>Every text a page shows about the sequences and devices.</summary>
    static string AllText(MachineStatus s) => string.Join("\n", s.Summary.Concat(s.Sequences.Select(q => q.Explanation))
        .Concat(s.Equipment.Select(e => e.Explanation + " " + e.BlockedBy)));

    [Fact]
    public void Run_changes_nothing()
    {
        var none = Eval("step30-agitator-blocked", null);
        var run = Eval("step30-agitator-blocked", Run());
        Assert.Equal(none.Summary, run.Summary);
        Assert.Equal(none.Sequences.Select(q => q.Explanation), run.Sequences.Select(q => q.Explanation));
        Assert.Equal(none.Equipment.Select(e => e.BlockedBy), run.Equipment.Select(e => e.BlockedBy));
        Assert.NotEmpty(run.Sequences[0].Next);
        Assert.False(run.Cpu!.Stopped);
        Assert.True(run.Cpu.Running);
    }

    [Fact]
    public void Stop_says_the_program_is_not_running_instead_of_waiting_or_blocked()
    {
        var s = Eval("step30-agitator-blocked", Stop());
        Assert.StartsWith("The PLC is in STOP since ", s.Summary[0]);
        Assert.EndsWith(": the program is not running.", s.Summary[0]);
        Assert.Contains(T0.ToLocalTime().ToString("HH:mm"), s.Summary[0]);
        Assert.Equal("The values below are its last state.", s.Summary[1]);
        Assert.True(s.Cpu!.Stopped);

        var text = AllText(s);
        Assert.DoesNotContain("waiting for", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("blocked", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Active now", text);

        var seq = Assert.Single(s.Sequences);
        Assert.Empty(seq.Next);
        Assert.Equal(30, seq.Step);
        Assert.Contains("not running, because the CPU is in STOP", seq.Explanation);
        Assert.Contains("last step was 30", seq.Explanation);
        Assert.All(s.Equipment, e =>
        {
            Assert.Null(e.BlockedBy);
            Assert.Empty(e.Blocking);
            Assert.Contains("not running, because the CPU is in STOP. Last state:", e.Explanation);
        });
    }

    [Fact]
    public void Stop_keeps_the_active_alarms_listed()
    {
        var s = Eval("step10-xv101-not-open", Stop());
        Assert.Contains(s.ActiveAlarms, a => a.Id == "A02");
        Assert.Contains(s.Summary, l => l.Contains("A02"));
    }

    [Fact]
    public void Other_modes_that_are_not_run_count_too()
    {
        Assert.StartsWith("The PLC is in STARTUP", Eval("step30-mix-heat", new CpuStatus("Startup", null, T0)).Summary[0]);
        Assert.StartsWith("The PLC is in HOLD", Eval("step30-mix-heat", new CpuStatus("Halt", null, T0)).Summary[0]);
        Assert.StartsWith("The PLC is in DEFECT", Eval("step30-mix-heat", new CpuStatus("Defective", null, T0)).Summary[0]);
        // Test files say "STOP" or "stop" as well as the driver's "Stop"
        Assert.True(new CpuStatus("STOP", null, T0).Stopped);
    }

    [Theory]
    [InlineData("Unknown", null)]
    [InlineData("NotSupported", null)]
    [InlineData("Unknown", "Timeout while reading the CPU state.")]
    [InlineData("Stop", "Timeout while reading the CPU state.")]
    public void A_failed_or_unknown_read_is_never_stop(string state, string? error)
    {
        var tracker = new CpuModeTracker();
        var cpu = tracker.Update(state, null, error, T0)!;
        Assert.False(cpu.Stopped);
        Assert.False(cpu.Running);
        Assert.NotNull(cpu.Error);

        var none = Eval("step30-agitator-blocked", null);
        var s = Eval("step30-agitator-blocked", cpu);
        Assert.Equal(none.Summary, s.Summary);
        Assert.Contains(s.Equipment, e => e.BlockedBy != null);
        Assert.NotEqual("PLC in STOP.", ProductionLog.Classify(s, TestData.Model).Why);
    }

    [Fact]
    public void Not_connected_drops_the_mode()
    {
        var snap = new Snapshot { Time = T0 };
        var s = TestData.Engine.Evaluate(snap, new PlcLinkInfo("disconnected", "192.168.10.10", "gone", T0, 0, 0), Stop());
        Assert.Null(s.Cpu);
        Assert.DoesNotContain(s.Summary, l => l.Contains("STOP"));
    }

    [Fact]
    public void Tracker_keeps_since_through_a_failed_read_and_marks_a_stop_seen_at_start()
    {
        var tr = new CpuModeTracker();
        Assert.Null(tr.Update(null, null, null, T0));     // a test file without a mode
        var first = tr.Update("Stop", "RUN", null, T0)!;
        Assert.True(first.Stopped);
        Assert.True(first.AtStart);
        Assert.Equal(T0, first.Since);
        Assert.Contains("since at least", StatusEngine.SinceText(first, T0));

        Assert.False(tr.Update("Unknown", null, "read failed", T0.AddSeconds(2))!.Stopped);
        var again = tr.Update("Stop", "RUN", null, T0.AddSeconds(4))!;
        Assert.Equal(T0, again.Since);                     // not reset by the failed read

        var run = tr.Update("Run", "RUN", null, T0.AddMinutes(3))!;
        Assert.Equal(T0.AddMinutes(3), run.Since);
        Assert.False(run.AtStart);
        var stop = tr.Update("Stop", "STOP", null, T0.AddMinutes(9))!;
        Assert.Equal(T0.AddMinutes(9), stop.Since);
        Assert.False(stop.AtStart);
        Assert.DoesNotContain("at least", StatusEngine.SinceText(stop, T0.AddMinutes(10)));
    }

    [Fact]
    public void History_logs_going_to_stop_and_back_once_and_no_blocked_noise()
    {
        var h = new MachineHistory(TimeSpan.FromMinutes(30));
        var t = T0;
        MachineStatus At(CpuStatus? cpu) { t = t.AddSeconds(1); var snap = TestData.Snapshot("step30-agitator-blocked", t); var s = TestData.Engine.Evaluate(snap, new PlcLinkInfo("connected", "x", null, t, 0, 0), cpu); h.Add(snap, s); return s; }

        At(Run());
        At(Run());
        At(Stop(t));
        At(Stop(t));
        At(new CpuStatus("Unknown", null, null, "read failed"));   // a flaky read in between: no event
        At(Stop(t));
        At(Run());

        var events = h.Events(TimeSpan.FromHours(1), t.AddSeconds(1));
        var cpu = events.Where(e => e.Kind == "cpu").Select(e => e.Text).ToList();
        Assert.Equal(2, cpu.Count);
        Assert.StartsWith("The PLC went to STOP", cpu[0]);
        Assert.StartsWith("The PLC is back in RUN", cpu[1]);
        Assert.DoesNotContain(events, e => e.Text.Contains("no longer blocked"));
    }

    [Fact]
    public void History_says_when_watching_started_in_stop()
    {
        var h = new MachineHistory();
        var snap = TestData.Snapshot("step30-mix-heat", T0);
        h.Add(snap, TestData.Engine.Evaluate(snap, new PlcLinkInfo("connected", "x", null, T0, 0, 0), Stop()));
        Assert.Contains(h.Events(TimeSpan.FromHours(1), T0), e => e.Kind == "cpu" && e.Text.Contains("was in STOP when watching started"));
    }

    [Fact]
    public void Time_in_stop_is_downtime_with_its_own_reason()
    {
        // Even with a fault alarm on, STOP is why it is not running
        var (state, why, alarm) = ProductionLog.Classify(Eval("step10-xv101-not-open", Stop()), TestData.Model);
        Assert.Equal("down", state);
        Assert.Equal("PLC in STOP.", why);
        Assert.Null(alarm);

        var day = new DateOnly(2026, 11, 2);
        var cfg = new ReportSettings();
        cfg.Shifts.Clear();
        cfg.Shifts.Add(new ShiftDef { Name = "Early", Start = "06:00", End = "14:00" });
        var (from, to) = ReportBuilder.Window(day, cfg.Shifts[0]);
        var log = new ProductionLog(TestData.Model, null, () => cfg);
        for (int i = 0; i <= 60; i++)
        {
            var t = from.AddMinutes(i);
            log.Add(Eval("step30-mix-heat", i < 30 ? Run() : Stop(from.AddMinutes(30)), t: t));
        }
        var r = new ReportBuilder(TestData.Model, log, null, () => cfg).Shift(day, 0, from.AddMinutes(60));
        var loss = Assert.Single(r.Losses, l => l.Why == "PLC in STOP.");
        Assert.Equal("down", loss.State);
        Assert.Equal(30, loss.Minutes, 0);
        Assert.Equal(30, r.Totals.Running, 0);
        Assert.Equal(1, r.Maint.Stops);
    }
}
