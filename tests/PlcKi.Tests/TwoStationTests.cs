using System.Text.RegularExpressions;
using PlcKi.Core.Model;
using PlcKi.Core.Status;
using PlcKi.Server.Live;
using Xunit;

/// <summary>
/// The two-station test model (models/demo-two-stations.json) with its snapshot files, for the station paths that the
/// single-station demo cannot show (Stations page, TV mode per station, alarm sound per station, station as start page).
/// The pages group by the instance named in a location ("FB_Valve, instance "Mixer".XV101" → Mixer); the same rule is
/// used here, so a test fails when the model stops yielding two stations.
/// </summary>
public class TwoStationTests
{
    private static readonly MachineModel Model = MachineModel.Load(Path.Combine(AppContext.BaseDirectory, "models", "demo-two-stations.json"));

    private static MachineStatus Run(string snapshot)
    {
        using var src = new SnapshotFileSource(Path.Combine(AppContext.BaseDirectory, "models", "snapshots", snapshot + ".json"));
        src.Connect(Model.Tags);
        var snap = src.Read();
        return new StatusEngine(Model).Evaluate(snap, new PlcLinkInfo("snapshot", "test", null, snap.Time, Model.Tags.Count, 0));
    }

    /// <summary>The station named in a location: instance "Mixer".XV101 → Mixer (as keyOf in stations.js).</summary>
    private static string Key(string? location) => Regex.Match(location ?? "", "instance\\s+\"([^\"]+)\"") is { Success: true } m ? m.Groups[1].Value : "";

    private static string[] Stations(MachineStatus s) => s.Equipment.Select(e => Key(e.Location)).Where(k => k != "").Distinct().Order().ToArray();

    /// <summary>The station of an alarm: the device it is about, or the station instance its location names; "" = the machine.</summary>
    private static string StationOf(MachineStatus s, AlarmStatus a)
    {
        var key = Key(a.Location);
        return Stations(s).Contains(key) ? key : "";
    }

    [Fact]
    public void Model_yields_two_stations_with_their_own_devices_and_sequence()
    {
        var s = Run("two-stations-both-running");
        Assert.Equal(new[] { "Filler_Line", "Mixer" }, Stations(s));
        Assert.Equal(new[] { "E301", "M201", "XV101" }, s.Equipment.Where(e => Key(e.Location) == "Mixer").Select(e => e.Id).Order());
        Assert.Equal(new[] { "CP701", "CV501", "XV601" }, s.Equipment.Where(e => Key(e.Location) == "Filler_Line").Select(e => e.Id).Order());

        // One sequence per station, each "called in" its station block, each with its own step
        Assert.Equal(2, s.Sequences.Count);
        var mixer = s.Sequences.Single(q => q.Block!.Contains("called in FB_Mixer"));
        var filler = s.Sequences.Single(q => q.Block!.Contains("called in FB_Filler_Line"));
        Assert.Equal((20, "Mix and heat"), (mixer.Step, mixer.StepName));
        Assert.Equal((20, "Fill bottle"), (filler.Step, filler.StepName));
        Assert.False(mixer.Halted);
        Assert.False(filler.Halted);
    }

    [Fact]
    public void Both_running_has_no_alarms_and_healthy_devices()
    {
        var s = Run("two-stations-both-running");
        Assert.Empty(s.ActiveAlarms);
        Assert.Equal("running at 298 rpm (setpoint 300 rpm)", s.Equipment.Single(e => e.Id == "M201").State);
        Assert.Equal("open", s.Equipment.Single(e => e.Id == "XV601").State);
    }

    [Fact]
    public void Fault_on_one_station_leaves_the_other_station_untouched()
    {
        var s = Run("two-stations-filler-fault");
        var alarm = Assert.Single(s.ActiveAlarms);
        Assert.Equal(("F02", "FAULT"), (alarm.Id, alarm.Severity));
        Assert.Equal("Filler_Line", StationOf(s, alarm));
        Assert.Contains(s.Sequences, q => q.Block!.Contains("FB_Filler_Line") && q.Halted);
        Assert.Contains(s.Sequences, q => q.Block!.Contains("FB_Mixer") && !q.Halted);
        // Mixer devices carry no alarm
        Assert.DoesNotContain(s.Equipment.Where(e => Key(e.Location) == "Mixer"), e => e.Level == "fault");
    }

    [Fact]
    public void A_halted_sequence_explains_only_the_off_devices_of_its_own_station()
    {
        var s = Run("two-stations-filler-fault");
        // Mixer is running; its idle valve is not off because the Filler halted
        var xv101 = s.Equipment.Single(e => e.Id == "XV101");
        Assert.DoesNotContain("halted", xv101.Explanation);
        Assert.Contains("Not requested in the current step", xv101.Explanation);
        // The Filler's own idle devices are
        Assert.Contains("Filler sequence is halted", s.Equipment.Single(e => e.Id == "CV501").Explanation);
        Assert.DoesNotContain("Mixer sequence", s.Equipment.Single(e => e.Id == "CV501").Explanation);

        // Both halted: each station names only its own sequence
        var both = Run("two-stations-alarms-both");
        Assert.Contains("Mixer sequence is halted", both.Equipment.Single(e => e.Id == "XV101").Explanation);
        Assert.DoesNotContain("Filler sequence", both.Equipment.Single(e => e.Id == "XV101").Explanation);
    }

    [Fact]
    public void Alarms_on_both_stations_and_one_for_the_machine()
    {
        var s = Run("two-stations-alarms-both");
        var byStation = s.ActiveAlarms.GroupBy(a => StationOf(s, a)).ToDictionary(g => g.Key, g => g.Select(a => a.Id).Order().ToArray());
        Assert.Equal(new[] { "M02" }, byStation["Mixer"]);
        Assert.Equal(new[] { "F01", "F03" }, byStation["Filler_Line"]);
        Assert.Equal(new[] { "L01" }, byStation[""]);                    // air pressure: not tied to a station
        Assert.Equal("FAULT", s.ActiveAlarms.Single(a => a.Id == "F01").Severity);
        Assert.Equal("WARNING", s.ActiveAlarms.Single(a => a.Id == "F03").Severity);
        Assert.All(s.Sequences, q => Assert.True(q.Halted));
        Assert.Equal("drive fault", s.Equipment.Single(e => e.Id == "M201").State);
        Assert.Equal("motor protection tripped", s.Equipment.Single(e => e.Id == "CV501").State);
    }

    [Fact]
    public void Every_alarm_has_a_fix_and_a_station_or_is_a_machine_alarm()
    {
        Assert.Equal(10, Model.Alarms.Count);
        Assert.All(Model.Alarms, a => Assert.NotEmpty(a.Fix));
        var s = Run("two-stations-both-running");
        var machineOnly = Model.Alarms.Where(a => !Stations(s).Contains(Key(a.Location))).Select(a => a.Id).Order();
        Assert.Equal(new[] { "L01", "L02" }, machineOnly);
    }
}
