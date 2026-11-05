using PlcKi.Core.Model;
using PlcKi.Core.Status;
using Xunit;

/// <summary>Runs the status engine on the demo model with snapshots taken from real watch tables.</summary>
public class StatusTests
{
    private static readonly MachineModel Model = TestData.Model;

    private static MachineStatus Run(string snapshot) => TestData.Status(snapshot);

    [Fact]
    public void Demo_model_is_valid_and_covers_every_alarm()
    {
        Assert.Equal(25, Model.Alarms.Count);
        Assert.All(Model.Alarms, a => Assert.NotEmpty(a.Fix));
    }

    [Fact]
    public void Step10_with_A02_explains_halt_and_alarm()
    {
        var s = Run("step10-xv101-not-open");
        var seq = Assert.Single(s.Sequences);
        Assert.Equal(10, seq.Step);
        Assert.True(seq.Halted);
        Assert.Contains(seq.HaltReasons, r => r.Contains("FAULT alarm"));
        var alarm = Assert.Single(s.ActiveAlarms);
        Assert.Equal("A02", alarm.Id);
        Assert.Contains("air", string.Join(" ", alarm.Fix));
        Assert.Contains(s.Summary, l => l.Contains("halted") && l.Contains("step 10"));
        // The step-10 valve is off because of the halt, not because step 10 does not need it
        var xv101 = s.Equipment.Single(e => e.Id == "XV101");
        Assert.DoesNotContain("Not requested in the current step", xv101.Explanation);
        Assert.Contains("halted", xv101.Explanation);
    }

    [Fact]
    public void Step30_with_low_level_names_the_agitator_interlock()
    {
        var s = Run("step30-agitator-blocked");
        var m201 = s.Equipment.Single(e => e.Id == "M201");
        Assert.True(m201.Requested);
        Assert.False(m201.Commanded);
        var block = Assert.Single(m201.Blocking);
        Assert.Contains("20 %", block.Text);
        var heater = s.Equipment.Single(e => e.Id == "E301");
        Assert.Contains(heater.Blocking, b => b.Text.Contains("at speed"));
        Assert.Contains(s.Summary, l => l.Contains("Agitator M201 is requested by the program but blocked"));
    }

    [Fact]
    public void Step30_lists_what_is_still_missing()
    {
        var s = Run("step30-mix-heat");
        var next = Assert.Single(s.Sequences[0].Next);
        Assert.Equal(40, next.To);
        Assert.False(next.Ready);
        Assert.All(next.Conditions, c => Assert.False(c.Met));
        Assert.Contains(s.Equipment, e => e.Id == "E301" && e.Level == "ok");
    }

    [Fact]
    public void Step50_drain_from_watch_table()
    {
        var s = Run("step50-drain");
        var seq = s.Sequences[0];
        Assert.Equal(50, seq.Step);
        Assert.False(seq.Halted);
        Assert.Equal("27 s", seq.TimeInStep);
        Assert.Empty(s.ActiveAlarms);
        var cond = Assert.Single(seq.Next[0].Conditions);
        Assert.False(cond.Met);
        Assert.Contains(cond.Values, v => v.Id == "Level" && v.Text == "16.2 %");
        Assert.Equal("running", s.Equipment.Single(e => e.Id == "P401").State);
    }
}

/// <summary>Status details that generated models need: the sequence's own "not halted" term and values in alarm texts.</summary>
public class StatusTextTests
{
    private static MachineModel Changed(Func<string, string> change)
    {
        var src = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "models", "demo-mixing-station.json"));
        var path = Path.Combine(Path.GetTempPath(), $"plcki-model-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, change(src));
        try { return MachineModel.Load(path); }
        finally { File.Delete(path); }
    }

    private static MachineStatus Run(MachineModel model, string snapshot)
    {
        var snap = TestData.Snapshot(snapshot);
        return new StatusEngine(model).Evaluate(snap, new PlcLinkInfo("snapshot", "192.168.10.10", null, snap.Time, model.Tags.Count, 0));
    }

    [Fact]
    public void Not_halted_term_of_a_transition_is_not_listed_as_a_waiting_for_line()
    {
        var model = Changed(s => s.Replace("\"next\": [ { \"to\": 20, \"conditions\": [",
            "\"next\": [ { \"to\": 20, \"conditions\": [ { \"expr\": \"NOT Halted\", \"text\": \"Sequence not halted\" },"));
        var seq = Assert.Single(Run(model, "step10-xv101-not-open").Sequences);
        var next = Assert.Single(seq.Next);
        Assert.DoesNotContain(next.Conditions, c => c.Text.Contains("not halted"));
        Assert.NotEmpty(next.Conditions);
        Assert.NotEqual(true, next.Ready);   // halted: still not ready
    }

    [Fact]
    public void Placeholders_in_alarm_texts_get_live_values()
    {
        var model = Changed(s => s.Replace("\"Check the open switch ZSO\"", "\"Check the open switch ZSO (level now {Level})\""));
        var a02 = Assert.Single(Run(model, "step10-xv101-not-open").ActiveAlarms);
        Assert.DoesNotContain("{Level}", string.Join(" ", a02.Fix));
        Assert.Contains("%", a02.Fix.Single(f => f.Contains("level now")));
    }

}

/// <summary>What the pages lean on: blocked devices with their reason, and alarms in the order they came.</summary>
public class StatusAttentionTests
{
    private static readonly MachineModel Model = TestData.Model;
    private static MachineStatus Run(string snapshot) => TestData.Status(snapshot);

    [Fact]
    public void Step30_blocked_devices_say_what_they_need()
    {
        var s = Run("step30-agitator-blocked");
        var m201 = s.Equipment.Single(e => e.Id == "M201");
        Assert.NotNull(m201.BlockedBy);
        Assert.StartsWith("needs ", m201.BlockedBy);
        Assert.Contains("20 %", m201.BlockedBy);
        Assert.Contains("now", m201.BlockedBy);
        // Devices that are not requested are not "blocked"
        Assert.All(s.Equipment.Where(e => e.Requested != true), e => Assert.Null(e.BlockedBy));
        // The step says what it should do, not that it does it
        Assert.Contains("In this step it should run agitator M201", s.Sequences[0].Explanation);
    }

    [Fact]
    public void A_requested_device_with_every_interlock_met_is_not_blocked()
    {
        // Like a robot that keeps its last job number after the job: the request stays on, the output is off,
        // and no interlock is missing. That is not "blocked".
        var snap = TestData.Snapshot("step30-agitator-blocked");
        snap.Values["Level"] = 50.0;
        var m201 = TestData.Evaluate(snap, "snapshot").Equipment.Single(e => e.Id == "M201");
        Assert.True(m201.Requested);
        Assert.False(m201.Commanded);
        Assert.Empty(m201.Blocking);
        Assert.Null(m201.BlockedBy);
        Assert.Contains("every interlock is met", m201.Explanation);
    }

    [Theory]
    [InlineData("Runs agitator M201", "In this step it should run agitator M201.")]
    [InlineData("Mixes for 5 min.", "In this step it should mix for 5 min.")]
    [InlineData("Doses ingredient B", "In this step it should dose ingredient B.")]
    [InlineData("Carries the batch", "In this step it should carry the batch.")]
    [InlineData("Keeps all outputs off", "In this step it should keep all outputs off.")]
    [InlineData("Waiting for start", "What this step does: Waiting for start.")]
    [InlineData("Runs agitator M201 and heats with E301", "In this step it should run agitator M201 and heat with E301.")]
    [InlineData("Opens XV101 and valves stay closed", "In this step it should open XV101 and valves stay closed.")]
    public void Step_task_is_worded_as_intent(string does, string expected) =>
        Assert.Equal(expected, StatusEngine.ShouldDo(does));

    [Fact]
    public void Alarms_are_ordered_by_when_they_came_not_by_severity()
    {
        var engine = new StatusEngine(Model);
        MachineStatus Eval(Snapshot snap) => engine.Evaluate(snap, new PlcLinkInfo("connected", "x", null, snap.Time, 0, 0));
        var t0 = new DateTimeOffset(2026, 11, 3, 14, 0, 0, TimeSpan.Zero);

        // First read: A02 is already on, so when it came is not known
        var first = TestData.Snapshot("step10-xv101-not-open", t0);
        var s1 = Eval(first);
        var a02 = Assert.Single(s1.ActiveAlarms);
        Assert.True(a02.AtStart);

        // A02 goes, then the warning A08 comes, then the fault A06: A08 is the first-out
        var t1 = t0.AddSeconds(10);
        var noAlarm = TestData.Snapshot("step10-xv101-not-open", t1); noAlarm.Values["A02"] = false;
        Assert.Empty(Eval(noAlarm).ActiveAlarms);
        var warn = TestData.Snapshot("step10-xv101-not-open", t1.AddSeconds(2)); warn.Values["A02"] = false; warn.Values["A08"] = true;
        Eval(warn);
        var both = TestData.Snapshot("step10-xv101-not-open", t1.AddSeconds(5)); both.Values["A02"] = false; both.Values["A08"] = true; both.Values["A06"] = true;
        var s4 = Eval(both);
        Assert.Equal(new[] { "A08", "A06" }, s4.ActiveAlarms.Select(a => a.Id));
        Assert.Equal(t1.AddSeconds(2), s4.ActiveAlarms[0].Since);
        Assert.Equal(t1.AddSeconds(5), s4.ActiveAlarms[1].Since);
        Assert.All(s4.ActiveAlarms, a => Assert.False(a.AtStart));
    }

    [Fact]
    public void Feedback_comes_from_the_ok_state_minus_the_command()
    {
        var s = Run("step10-xv101-not-open");
        var xv101 = s.Equipment.Single(e => e.Id == "XV101");
        Assert.NotNull(xv101.Feedback);
        Assert.Equal(new[] { "XV101 open switch: reached", "XV101 closed switch: not reached" }, xv101.Feedback!.Select(f => f.Text));
        // The valve is closed: open switch not reached, closed switch reached
        Assert.Equal(new bool?[] { false, false }, xv101.Feedback.Select(f => f.Met));
        Assert.Equal("not reached", xv101.Feedback[0].Values.Single().Text);
        var m201 = s.Equipment.Single(e => e.Id == "M201");
        Assert.Equal("M201 at speed: yes", Assert.Single(m201.Feedback!).Text);
        // The heater's ok state is its command alone: nothing tells the program it heats
        Assert.Null(s.Equipment.Single(e => e.Id == "E301").Feedback);
    }

    [Fact]
    public void Feedback_from_the_model_wins()
    {
        var model = MachineModel.Load(Path.Combine(AppContext.BaseDirectory, "models", "demo-mixing-station.json"));
        model.Equipment.Single(e => e.Id == "E301").Feedback.Add(new CondDef { Expr = "Temp > 25", Text = "Temperature rising" });
        var snap = TestData.Snapshot("step30-mix-heat");
        var s = new StatusEngine(model).Evaluate(snap, new PlcLinkInfo("snapshot", "x", null, snap.Time, 0, 0));
        Assert.Equal("Temperature rising", Assert.Single(s.Equipment.Single(e => e.Id == "E301").Feedback!).Text);
    }

    [Fact]
    public void Blocked_chain_is_linked_root_first()
    {
        var s = Run("step30-agitator-blocked");
        var m201 = s.Equipment.Single(e => e.Id == "M201");
        var e301 = s.Equipment.Single(e => e.Id == "E301");
        // E301 waits for "M201 at speed", which belongs to M201; M201 waits for the level, which belongs to no device
        Assert.Equal("M201", e301.BlockedVia);
        Assert.Equal("M201", e301.Blocking.Single(b => b.Text.Contains("at speed")).HeldBy);
        Assert.Null(m201.BlockedVia);
        Assert.Null(m201.Blocking.Single().HeldBy);
        // "Temperature reached" can't come while E301 is blocked; "Mixed for at least" has no device behind it
        var conds = s.Sequences[0].Next.SelectMany(n => n.Conditions).ToList();
        Assert.Equal("E301", conds.Single(c => c.Text.StartsWith("Temperature reached")).HeldBy);
        Assert.All(conds.Where(c => !c.Text.StartsWith("Temperature reached")), c => Assert.Null(c.HeldBy));
    }

    [Fact]
    public void Running_devices_have_no_chain()
    {
        var s = Run("step30-mix-heat");
        Assert.All(s.Equipment, e => Assert.Null(e.BlockedVia));
        Assert.All(s.Sequences[0].Next.SelectMany(n => n.Conditions), c => Assert.Null(c.HeldBy));
    }

    [Fact]
    public void Alarm_ids_sort_naturally()
    {
        var ids = new[] { "A10", "A9", "A2", "B1", "A01" }.OrderBy(x => x, NaturalComparer.Instance).ToArray();
        Assert.Equal(new[] { "A01", "A2", "A9", "A10", "B1" }, ids);
    }
}
