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
