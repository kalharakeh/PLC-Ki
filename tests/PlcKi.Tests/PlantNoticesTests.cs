using PlcKi.Core.Import;
using PlcKi.Core.Status;
using Xunit;

/// <summary>The notices on every page (review 43): test switches on, and the PLC's blocks against the import.</summary>
public class PlantNoticesTests
{
    private static ExportPlc Demo() =>
        ProgramExport.Load(Path.Combine(AppContext.BaseDirectory, "models", "demo-export.zip")).Plcs[0];

    private static ExportBlock Db(string name, string kind = "GlobalDB", string? title = null, params string[] bools)
    {
        var b = new ExportBlock { Name = name, Kind = kind, Title = title };
        foreach (var m in bools) b.Interface.Add(new ExportMember { Name = m, Datatype = "Bool", Section = "Static" });
        return b;
    }

    [Fact]
    public void Finds_the_demo_fault_injection_switches_with_their_comments()
    {
        var set = TestSwitches.Find(Demo());
        Assert.Equal(new[] { "DB_FaultInjection" }, set.Blocks);       // not "Sim" (an instance DB), not DB_Alarms
        Assert.Equal(11, set.Switches.Count);
        var s = set.Switches.Single(x => x.Name == "XV101_StuckClosed");
        Assert.Equal("\"DB_FaultInjection\".XV101_StuckClosed", s.Symbol);
        Assert.Contains("XV101", s.Comment);
        Assert.Empty(set.Skipped);
    }

    [Theory]
    [InlineData("DB_FaultInjection", null, true)]
    [InlineData("DB_Test", null, true)]
    [InlineData("TestBench", null, true)]
    [InlineData("DB_Sim", null, false)]
    [InlineData("DB_IO", "Field signals (simulated)", false)]
    [InlineData("DB_Latest", null, false)]
    [InlineData("DB_Contest", null, false)]
    [InlineData("DB_Switches", "Switches for testing only", true)]
    [InlineData("DB_Recipe", "Recipe values", false)]
    public void Knows_a_test_block_by_its_name_or_title(string name, string? title, bool expected) =>
        Assert.Equal(expected, TestSwitches.IsTestBlock(Db(name, title: title)));

    [Theory]
    [InlineData("SimActive", true)]
    [InlineData("Sim", true)]
    [InlineData("Simulation_On", true)]
    [InlineData("EnableSim", true)]
    [InlineData("UseSimulation", true)]
    [InlineData("SimLevel", false)]
    [InlineData("Similar", false)]
    [InlineData("EStop_OK", false)]
    public void Knows_a_simulation_switch_by_its_name(string member, bool expected) =>
        Assert.Equal(expected, TestSwitches.IsSimSwitch(member));

    [Fact]
    public void In_other_blocks_only_simulation_switches_count()
    {
        var plc = new ExportPlc { Name = "PLC_1" };
        plc.Blocks.Add(Db("DB_IO", title: "Field signals (simulated)", bools: new[] { "EStop_OK", "XV101_ZSO" }));
        plc.Blocks.Add(Db("DB_Settings", bools: new[] { "SimActive", "AutoMode" }));
        var set = TestSwitches.Find(plc);
        var s = Assert.Single(set.Switches);
        Assert.Equal("\"DB_Settings\".SimActive", s.Symbol);
    }

    [Fact]
    public void Instance_data_blocks_never_count() =>
        Assert.False(TestSwitches.IsTestBlock(Db("DB_Test", kind: "InstanceDB")));

    [Fact]
    public void Reads_at_most_four_blocks_and_says_which_were_left_out()
    {
        var plc = new ExportPlc { Name = "PLC_1" };
        for (var i = 1; i <= 6; i++) plc.Blocks.Add(Db($"DB_Test{i}", bools: new[] { "A", "B" }));
        var set = TestSwitches.Find(plc);
        Assert.Equal(4, set.Blocks.Count);
        Assert.Equal(8, set.Switches.Count);
        Assert.Equal(new[] { "DB_Test5", "DB_Test6" }, set.Skipped);
    }

    [Fact]
    public void Reads_at_most_64_switches()
    {
        var plc = new ExportPlc { Name = "PLC_1" };
        plc.Blocks.Add(Db("DB_Test", bools: Enumerable.Range(1, 80).Select(i => "S" + i).ToArray()));
        var set = TestSwitches.Find(plc);
        Assert.Equal(TestSwitches.MaxSwitches, set.Switches.Count);
        Assert.Equal(new[] { "DB_Test (in part)" }, set.Skipped);
    }

    [Fact]
    public void Notice_names_each_switch_that_is_on_with_its_comment()
    {
        var set = TestSwitches.Find(Demo());
        var values = set.Switches.ToDictionary(s => s.Symbol, s => ((object?)false, (string?)null));
        Assert.Null(TestSwitches.Notice(set, values));

        values["\"DB_FaultInjection\".XV101_StuckClosed"] = (true, null);
        var n = TestSwitches.Notice(set, values)!;
        Assert.Equal("test", n.Kind);
        Assert.StartsWith("A test switch is on in the PLC.", n.Text);
        Assert.StartsWith("XV101_StuckClosed: Valve XV101", Assert.Single(n.Items));
        Assert.Equal("#/program/DB_FaultInjection", n.Link);

        values["\"DB_FaultInjection\".P401_Trip"] = (true, null);
        Assert.StartsWith("2 test switches are on", TestSwitches.Notice(set, values)!.Text);
    }

    [Fact]
    public void A_switch_that_could_not_be_read_is_not_on()
    {
        var set = TestSwitches.Find(Demo());
        var values = set.Switches.ToDictionary(s => s.Symbol, s => ((object?)null, (string?)"Not in the test file."));
        Assert.Null(TestSwitches.Notice(set, values));
    }

    private static List<PlcBlock> AsOnPlc(ExportPlc plc) => plc.Blocks
        .Where(b => b.Kind is "OB" or "FB" or "FC" or "GlobalDB" or "InstanceDB")
        .Select(b => new PlcBlock(b.Name, b.Kind is "GlobalDB" or "InstanceDB" ? "DB" : b.Kind, b.Kind.EndsWith("DB") ? "DB" : "SCL")).ToList();

    [Fact]
    public void Same_blocks_as_the_import_gives_no_notice()
    {
        var plc = Demo();
        var (added, missing) = ProgramDrift.Compare(AsOnPlc(plc), plc);
        Assert.Empty(added);
        Assert.Empty(missing);
        Assert.Null(ProgramDrift.Notice(added, missing));
    }

    [Fact]
    public void Says_which_blocks_were_added_and_which_are_missing()
    {
        var plc = Demo();
        var onPlc = AsOnPlc(plc);
        var gone = onPlc.First(b => b.Kind == "FB");
        onPlc.Remove(gone);
        onPlc.Add(new PlcBlock("FB_New", "FB", "SCL"));
        onPlc.Add(new PlcBlock("FC_Extra", "FC", "LAD_IEC"));
        var (added, missing) = ProgramDrift.Compare(onPlc, plc);
        Assert.Equal(new[] { "FB_New", "FC_Extra" }, added);
        Assert.Equal(new[] { gone.Name }, missing);
        var n = ProgramDrift.Notice(added, missing)!;
        Assert.Equal("program", n.Kind);
        Assert.StartsWith("2 blocks in the PLC aren't in the imported program, and 1 block of the imported program isn't in the PLC.", n.Text);
        Assert.DoesNotContain("changed", n.Text);    // it never claims a block's content changed
        Assert.Contains("In the PLC only: FB_New", n.Items);
    }

    [Fact]
    public void Safety_system_and_technology_blocks_in_the_PLC_do_not_count_as_added()
    {
        var plc = Demo();
        var onPlc = AsOnPlc(plc);
        onPlc.Add(new PlcBlock("RTG1SysInfo", "DB", "F_DB"));
        onPlc.Add(new PlcBlock("F_CTRL_1", "FC", "F_LAD"));
        onPlc.Add(new PlcBlock("Axis_1", "DB", "TechnoDB"));
        onPlc.Add(new PlcBlock("Udt_X", "UDT", "UDT"));
        var (added, missing) = ProgramDrift.Compare(onPlc, plc);
        Assert.Empty(added);
        Assert.Empty(missing);
    }
}
