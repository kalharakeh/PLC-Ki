using PlcKi.Core.Import;
using PlcKi.Core.Model;
using Xunit;

/// <summary>Reads the real TIA V20 export of the demo project and checks it against the hand-written model.</summary>
public class ImportTests
{
    private static readonly Lazy<ProgramExport> Export = new(() =>
        ProgramExport.Load(Path.Combine(AppContext.BaseDirectory, "data", "demo-export.zip")));

    private static ExportPlc Plc => Assert.Single(Export.Value.Plcs);

    [Fact]
    public void Reads_blocks_instances_and_comments()
    {
        Assert.Equal("MixingDemo", Export.Value.Project);
        var plc = Plc;
        Assert.Contains("192.168.10.10", plc.IpAddresses);
        Assert.Equal(16, plc.Blocks.Count);
        var valve = plc.Block("FB_Valve")!;
        Assert.Equal("FB", valve.Kind);
        Assert.Equal("On/off valve with limit switch monitoring", valve.Title);
        Assert.Contains(valve.Interface, m => m.Name == "Permit" && m.Comment!.Contains("Interlock"));
        Assert.NotNull(valve.SclSource);
        var station = plc.Block("Station")!;
        Assert.Equal("InstanceDB", station.Kind);
        Assert.Equal("FB_Station", station.InstanceOf);
        var main = plc.Block("Main")!;
        Assert.Contains(main.Calls, c => c.Block == "FB_Station" && c.Instance == "\"Station\"");
    }

    [Fact]
    public void Every_tag_of_the_hand_written_model_exists_in_the_export_with_the_same_type()
    {
        var tags = ProgramExport.Tags(Plc).ToDictionary(t => ProgramExport.NormalizeSymbol(t.Symbol), StringComparer.OrdinalIgnoreCase);
        var missing = new List<string>();
        foreach (var t in TestData.Model.Tags)
        {
            if (!tags.TryGetValue(ProgramExport.NormalizeSymbol(t.Symbol), out var real)) { missing.Add(t.Symbol); continue; }
            Assert.Equal(t.Type, real.ModelType);
        }
        Assert.Empty(missing);
    }

    [Fact]
    public void Instance_tags_get_comments_from_the_FB_definition()
    {
        var tags = ProgramExport.Tags(Plc);
        var permit = tags.Single(t => t.Symbol == "\"Station\".XV101.Permit");
        Assert.Contains("Interlock", permit.Comment);
    }

    [Fact]
    public void The_hand_written_model_passes_the_check()
    {
        var model = MachineModel.Load(Path.Combine(AppContext.BaseDirectory, "models", "demo-mixing-station.json"));
        Assert.Empty(ModelCheck.Check(model, Plc));
    }

    [Fact]
    public void A_wrong_symbol_is_reported_with_a_hint()
    {
        var model = new MachineModel
        {
            Name = "x",
            Tags = { new TagDef { Id = "Lvl", Symbol = "\"Station\".LT101.Valu", Type = "Real" },
                     new TagDef { Id = "Out", Symbol = "\"Statoin\".XV101.Out", Type = "Real" } },
        };
        var problems = ModelCheck.Check(model, Plc);
        Assert.Contains(problems, p => p.Contains("'Lvl'") && p.Contains("does not exist"));
        Assert.Contains(problems, p => p.Contains("'Out'") && p.Contains("did you mean \"Station\".XV101.Out"));
    }

    [Fact]
    public void Check_takes_the_type_from_the_program()
    {
        var model = new MachineModel { Name = "x", Tags = { new TagDef { Id = "Lvl", Symbol = "\"Station\".LT101.Value", Type = "Bool" } } };
        Assert.Empty(ModelCheck.Check(model, Plc));
        Assert.Equal("Real", model.Tags[0].Type);
    }

    [Fact]
    public void Digest_has_structure_sources_and_symbols()
    {
        var digest = ProgramDigest.Build(Export.Value, Plc);
        Assert.Contains("OB Main", digest);
        Assert.Contains("calls FB_Station with instance \"Station\"", digest);
        Assert.Contains("calls FB_Valve with instance #XV101", digest);
        Assert.Contains("#XV101(Open := #Seq.OpenXV101", digest);
        Assert.Contains("\"DB_Alarms\".A02_XV101_NotOpen : Bool // FAULT: Inlet valve XV101", digest);
        Assert.InRange(digest.Length, 10_000, 200_000);
    }

    [Fact]
    public void Compare_with_itself_is_complete()
    {
        var r = ModelCompare.Compare(TestData.Model, TestData.Model);
        Assert.Equal(1.0, r.TagCoverage);
        Assert.Equal(1.0, r.AlarmCoverage);
        Assert.Equal(r.ReferenceSteps, r.MatchedSteps);
        Assert.Equal(r.ReferenceDevices, r.MatchedDevices);
    }
}
