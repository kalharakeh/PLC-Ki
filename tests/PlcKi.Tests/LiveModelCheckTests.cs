using System.Text.Json;
using System.Text.Json.Nodes;
using PlcKi.Core.Import;
using PlcKi.Core.Model;
using PlcKi.Core.Status;
using PlcKi.Server.Generate;
using PlcKi.Server.Import;
using Xunit;

/// <summary>The live model check: what the machine did that the model does not explain.</summary>
public class LiveModelCheckTests
{
    private static Snapshot Snap(string name, DateTimeOffset t, Action<Dictionary<string, object?>>? change = null)
    {
        var s = TestData.Snapshot(name, t);
        change?.Invoke(s.Values);
        return s;
    }

    private static MachineStatus Eval(StatusEngine engine, Snapshot s) =>
        engine.Evaluate(s, new PlcLinkInfo("connected", "192.168.10.10", null, s.Time, 10, 0));

    [Fact]
    public void An_output_on_while_an_interlock_is_missing_is_reported_as_wrong()
    {
        var engine = new StatusEngine(TestData.Model);
        var check = new LiveModelCheck(TestData.Model);
        var t = DateTimeOffset.Now;
        // Step 30 with a low level: the model says M201 needs level above 20 %, but the output is on anyway
        for (int i = 0; i < 3; i++)
            check.Add(Eval(engine, Snap("step30-agitator-blocked", t.AddSeconds(i), v => v["M201_RunCmd"] = true)));
        var f = Assert.Single(check.Report().Findings, x => x.Target == "equipment:M201");
        Assert.Equal("wrong", f.Severity);
        Assert.Contains("output on while", f.Title);
        Assert.Equal(3, f.Count);
    }

    [Fact]
    public void A_single_read_is_not_enough_for_an_interlock_finding()
    {
        var engine = new StatusEngine(TestData.Model);
        var check = new LiveModelCheck(TestData.Model);
        check.Add(Eval(engine, Snap("step30-agitator-blocked", DateTimeOffset.Now, v => v["M201_RunCmd"] = true)));
        Assert.DoesNotContain(check.Report().Findings, x => x.Target == "equipment:M201");
    }

    [Fact]
    public void Unknown_steps_and_step_changes_are_reported()
    {
        var engine = new StatusEngine(TestData.Model);
        var check = new LiveModelCheck(TestData.Model);
        var t = DateTimeOffset.Now;
        check.Add(Eval(engine, Snap("step30-mix-heat", t)));
        check.Add(Eval(engine, Snap("step30-mix-heat", t.AddSeconds(1), v => v["Step"] = 10.0)));    // 30 -> 10: no such transition
        check.Add(Eval(engine, Snap("step30-mix-heat", t.AddSeconds(2), v => v["Step"] = 35.0)));    // no step 35
        var r = check.Report();
        Assert.Contains(r.Findings, x => x.Kind == "transition" && x.Title.Contains("step 30 went to step 10"));
        Assert.Contains(r.Findings, x => x.Kind == "step" && x.Title.Contains("step 35 is not in the model"));
        Assert.Equal(2, r.StepChanges);
        Assert.Contains(r.Seen, x => x.Contains("2 of 7 steps seen"));
    }

    [Fact]
    public void A_device_blocked_while_the_machine_keeps_stepping_is_reported()
    {
        var engine = new StatusEngine(TestData.Model);
        var check = new LiveModelCheck(TestData.Model) { BlockedLimit = TimeSpan.FromSeconds(10) };
        var t = DateTimeOffset.Now;
        // M201 stays blocked (level 15 %) while a second sequence step value flips: use the step number 30/40 alternately
        for (int i = 0; i < 8; i++)
            check.Add(Eval(engine, Snap("step30-agitator-blocked", t.AddSeconds(i * 3), v => { v["Step"] = i % 2 == 0 ? 30.0 : 40.0; v["Seq_RunM201"] = true; })));
        Assert.Contains(check.Report().Findings, x => x.Target == "equipment:M201" && x.Title.Contains("blocked while the machine keeps running"));
    }

    [Fact]
    public void Reset_starts_over()
    {
        var engine = new StatusEngine(TestData.Model);
        var check = new LiveModelCheck(TestData.Model);
        check.Add(Eval(engine, Snap("step30-mix-heat", DateTimeOffset.Now, v => v["Step"] = 35.0)));
        Assert.NotEmpty(check.Report().Findings);
        check.Reset();
        Assert.Empty(check.Report().Findings);
        Assert.Equal(0, check.Report().Reads);
    }
}

/// <summary>Settings and alarm checks in the model, and the status that shows them.</summary>
public class SettingsAndChecksTests
{
    private static MachineModel ModelWith(Action<MachineModel> change)
    {
        var json = JsonSerializer.Serialize(TestData.Model, MachineModel.JsonOptions);
        var m = JsonSerializer.Deserialize<MachineModel>(json, MachineModel.JsonOptions)!;
        change(m);
        m.Validate();
        return m;
    }

    [Fact]
    public void A_setting_that_stops_the_machine_shows_with_its_problem()
    {
        var m = ModelWith(x => x.Settings.Add(new SettingDef { Tag = "MixTime", Text = "Mixing time", Valid = "MixTime > T#0S", Problem = "the mixing step ends at once" }));
        var snap = TestData.Snapshot("step30-mix-heat");
        snap.Values["MixTime"] = 0.0;
        var s = new StatusEngine(m).Evaluate(snap, new PlcLinkInfo("snapshot", "", null, snap.Time, 1, 0));
        var st = Assert.Single(s.Settings!);
        Assert.False(st.Valid);
        Assert.Contains(s.Summary, l => l.Contains("the mixing step ends at once"));
    }

    [Fact]
    public void Alarm_checks_show_which_comparison_failed()
    {
        var m = ModelWith(x => x.Alarms.First(a => a.Id == "A02").Checks.Add(new CondDef { Expr = "XV101_ZSO", Text = "Open limit switch of XV101 reached" }));
        var s = TestData.Evaluate(TestData.Snapshot("step10-xv101-not-open"));
        var engine = new StatusEngine(m);
        var snap = TestData.Snapshot("step10-xv101-not-open");
        var a = Assert.Single(engine.Evaluate(snap, new PlcLinkInfo("snapshot", "", null, snap.Time, 1, 0)).ActiveAlarms);
        var c = Assert.Single(a.Checks!);
        Assert.False(c.Met);
    }

    [Fact]
    public void A_bad_expression_in_a_setting_or_check_fails_validation()
    {
        Assert.Throws<InvalidDataException>(() => ModelWith(x => x.Settings.Add(new SettingDef { Tag = "MixTime", Valid = "NoSuchTag > 0" })));
        Assert.Throws<InvalidDataException>(() => ModelWith(x => x.Alarms[0].Checks.Add(new CondDef { Expr = "Nope", Text = "x" })));
    }
}

/// <summary>Correcting one model entry without a new import.</summary>
public class ModelFixTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("plcki-fix").FullName;
    private readonly string _file;

    public ModelFixTests()
    {
        _file = Path.Combine(_dir, "model.json");
        File.Copy(Path.Combine(AppContext.BaseDirectory, "models", "demo-mixing-station.json"), _file);
    }

    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private ModelFixService Service()
    {
        var opt = new PlcKi.Server.Live.PlcKiOptions();
        var program = new PlcKi.Server.Jobs.ProgramService(opt, TestData.Model, p => p);
        var live = new PlcKi.Server.Live.LiveState(new MachineHistory(TimeSpan.FromMinutes(5)), new LiveModelCheck(TestData.Model));
        return new ModelFixService(new ModelFile(_file), program, live, new PlcKi.Server.Live.AskOptions());
    }

    [Fact]
    public void An_entry_is_saved_with_a_copy_of_the_old_model()
    {
        var fix = Service();
        var entry = JsonNode.Parse(JsonSerializer.Serialize(TestData.Model.Equipment.First(e => e.Id == "M201"), MachineModel.JsonOptions))!.AsObject();
        entry["name"] = "Agitator M201 (corrected)";
        fix.Save("equipment:M201", entry.ToJsonString());
        var saved = MachineModel.Load(_file);
        Assert.Equal("Agitator M201 (corrected)", saved.Equipment.First(e => e.Id == "M201").Name);
        Assert.Single(Directory.GetFiles(_dir, "model.before-*.json"));
    }

    [Fact]
    public void A_broken_entry_is_not_saved()
    {
        var fix = Service();
        var ex = Assert.Throws<ModelFixException>(() => fix.Save("equipment:M201", """{ "id": "M201", "name": "x", "request": "NoSuchTag" }"""));
        Assert.Contains("NoSuchTag", ex.Message);
        Assert.Empty(Directory.GetFiles(_dir, "model.before-*.json"));
    }

    [Fact]
    public void A_new_setting_can_be_added_and_read_back()
    {
        var fix = Service();
        fix.Save("setting:MixTime", """{ "tag": "MixTime", "text": "Mixing time", "valid": "MixTime > T#0S", "problem": "mixing ends at once" }""");
        Assert.Single(MachineModel.Load(_file).Settings);
        var got = JsonSerializer.Serialize(fix.Get("setting:MixTime"));
        Assert.Contains("\"exists\":true", got);
    }

    [Theory]
    [InlineData("equipment:XV1", "equipment")]
    [InlineData("alarm:A1.7", "alarms")]
    [InlineData("sequence:Robot", "sequences")]
    [InlineData("setting:Tol", "settings")]
    public void Targets_name_the_model_array(string target, string array) =>
        Assert.Equal(array, ModelFixService.ParseTarget(target).Array);
}

/// <summary>The "Machine simulator" request on the Block generator page.</summary>
public class SimulatorRequestTests
{
    [Fact]
    public void The_request_names_the_devices_and_the_lessons()
    {
        var r = SimulatorRequest.Build(TestData.Model, null);
        Assert.Equal("machine-sim", r.Name);
        Assert.Contains("Agitator", r.Request, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("job number keeps its last value", r.Request);
        Assert.Contains("often 0 in a fresh PLCSIM", r.Request);
        Assert.Null(r.Edit);
    }
}

public class AlarmChecksAtAlarmTests
{
    [Fact]
    public void Checks_use_the_values_from_just_before_the_alarm_came()
    {
        var json = JsonSerializer.Serialize(TestData.Model, MachineModel.JsonOptions);
        var m = JsonSerializer.Deserialize<MachineModel>(json, MachineModel.JsonOptions)!;
        m.Alarms.First(a => a.Id == "A02").Checks.Add(new CondDef { Expr = "Level < 50", Text = "Level below 50 %" });
        var engine = new StatusEngine(m);
        var t = DateTimeOffset.Now;
        MachineStatus Run(double level, bool alarm, int s)
        {
            var snap = TestData.Snapshot("step10-xv101-not-open", t.AddSeconds(s));
            snap.Values["A02"] = alarm;
            snap.Values["Level"] = level;
            return engine.Evaluate(snap, new PlcLinkInfo("connected", "", null, snap.Time, 1, 0));
        }
        Run(10, false, 0);                 // before: level 10 %, check met
        Run(10, true, 1);                  // the alarm comes
        var a = Assert.Single(Run(80, true, 2).ActiveAlarms);   // the level rose since; the check still shows 10 %
        Assert.True(a.ChecksAtAlarm);
        Assert.True(Assert.Single(a.Checks!).Met);
    }
}
