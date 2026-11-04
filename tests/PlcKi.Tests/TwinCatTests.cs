using System.IO.Compression;
using PlcKi.Core.Import;
using PlcKi.Core.Model;
using Xunit;

/// <summary>Reads the small TwinCAT 3 sample project (tests/data/twincat-demo) the same way as a Siemens export.</summary>
public class TwinCatTests
{
    private static readonly string Folder = Path.Combine(AppContext.BaseDirectory, "data", "twincat-demo");
    private static readonly Lazy<ProgramExport> Export = new(() => ProgramExport.Load(Folder));
    private static ExportPlc Plc => Assert.Single(Export.Value.Plcs);

    [Fact]
    public void Picks_the_twincat_reader_and_reads_the_project()
    {
        Assert.Equal("FillStation", Export.Value.Project);
        var plc = Plc;
        Assert.Equal("FillPlc", plc.Name);
        Assert.Equal(PlcBrands.Beckhoff, plc.Brand);
        Assert.Equal("5.80.201.232.1.1", plc.AmsNetId);
        Assert.Empty(plc.Problems);
        // F_Old.TcPOU lies in the folder but is not part of the .plcproj
        Assert.Null(plc.Block("F_Old"));
        Assert.Equal(new[] { "FB_Device", "FB_Pump", "FB_Valve", "F_Scale", "GVL_Alarms", "GVL_IO", "MAIN", "PlcTask" },
            plc.Blocks.Select(b => b.Name).OrderBy(n => n, StringComparer.OrdinalIgnoreCase));
        Assert.Equal(new[] { "E_FillState", "ST_Recipe" }, plc.Types.Select(b => b.Name).OrderBy(n => n));
    }

    [Fact]
    public void Reads_kinds_interfaces_comments_and_source()
    {
        var plc = Plc;
        Assert.Equal("PRG", plc.Block("MAIN")!.Kind);
        Assert.Equal("FC", plc.Block("F_Scale")!.Kind);
        Assert.Equal("Task", plc.Block("PlcTask")!.Kind);
        Assert.Equal("cycle 1 ms, priority 20", plc.Block("PlcTask")!.Comment);
        Assert.Equal("ENUM", plc.Block("E_FillState")!.Kind);
        Assert.Contains(plc.Block("E_FillState")!.Interface, m => m.Name == "Filling" && m.StartValue == "10");

        var valve = plc.Block("FB_Valve")!;
        Assert.Equal("FB", valve.Kind);
        Assert.Equal("ST", valve.Language);
        Assert.Equal("On/off valve with open feedback and travel monitoring", valve.Comment);
        var permit = Assert.Single(valve.Interface, m => m.Name == "bPermit");
        Assert.Equal(("Input", "BOOL", "interlocks OK"), (permit.Section, permit.Datatype, permit.Comment));
        Assert.Equal("T#10S", valve.Interface.Single(m => m.Name == "tTravel").StartValue);
        Assert.Contains("bOpenCmd := bOpenReq AND bPermit;", valve.SclSource);
        Assert.Contains("// METHOD Reset", valve.SclSource);

        Assert.Equal("FB_Device", plc.Block("FB_Pump")!.InstanceOf);
        Assert.Equal("Temp", plc.Block("F_Scale")!.Interface.Single(m => m.Name == "fTmp").Section);

        var io = plc.Block("GVL_IO")!;
        Assert.Equal("Field inputs and outputs, linked to the EtherCAT terminals", io.Comment);
        var start = io.Interface.Single(m => m.Name == "bStartButton");
        Assert.Equal(("%I*", "Start push button S1"), (start.Address, start.Comment));
        Assert.Equal("Constant", io.Interface.Single(m => m.Name == "cMaxLevel").Section);

        var recipe = plc.Block("ST_Recipe")!;
        Assert.Equal("recipe number from MES", recipe.Interface.Single(m => m.Name == "nRecipeNo").Comment);
        Assert.Equal("STRING(40)", recipe.Interface.Single(m => m.Name == "sName").Datatype);
        Assert.Equal("\"Stop\" pressed: wait for Start", plc.Block("MAIN")!.Interface.Single(m => m.Name == "bPaused").Comment);
    }

    [Fact]
    public void Finds_calls_and_globals()
    {
        var plc = Plc;
        Assert.Contains(new ExportCall("PRG", "MAIN", null), plc.Block("PlcTask")!.Calls);
        var main = plc.Block("MAIN")!;
        Assert.Contains(new ExportCall("FB", "FB_Valve", "fbInlet"), main.Calls);
        Assert.Contains(new ExportCall("FB", "FB_Pump", "fbDrain"), main.Calls);
        Assert.Contains(new ExportCall("FB", "TON", "tonStep"), main.Calls);
        Assert.Contains(new ExportCall("FC", "F_Scale", null), main.Calls);
        Assert.DoesNotContain(main.Calls, c => c.Block is "INT_TO_REAL");
        Assert.Equal(new[] { "GVL_Alarms", "GVL_IO" }, main.GlobalNames.OrderBy(n => n, StringComparer.Ordinal));
        Assert.Equal("FB_Valve", main.Interface.Single(m => m.Name == "fbInlet").UserType);
    }

    [Fact]
    public void Lists_ads_symbols_through_instances_structs_timers_and_arrays()
    {
        var tags = ProgramExport.Tags(Plc).ToDictionary(t => t.Symbol);
        Assert.Equal("Bool", tags["GVL_IO.bStartButton"].ModelType);
        Assert.Contains("%I*", tags["GVL_IO.bStartButton"].Source);
        Assert.Equal("Int", tags["MAIN.eState"].ModelType);                  // enum
        Assert.Equal("Real", tags["MAIN.fLevel"].ModelType);
        Assert.Equal("Real", tags["MAIN.stRecipe.fLevelSetpoint"].ModelType);
        Assert.Equal("Time", tags["MAIN.stRecipe.tSettle"].ModelType);
        Assert.Null(tags["MAIN.stRecipe.sName"].ModelType);
        Assert.Equal("Bool", tags["MAIN.fbInlet.bFaultNotOpen"].ModelType);
        Assert.Equal("Time", tags["MAIN.fbInlet.tonTravel.ET"].ModelType);   // standard TON inside an FB
        Assert.Equal("Bool", tags["MAIN.tonStep.Q"].ModelType);
        Assert.Equal("Bool", tags["MAIN.fbDrain.bFault"].ModelType);         // inherited from FB_Device
        Assert.Equal("Bool", tags["GVL_Alarms.aAlarm[4]"].ModelType);
        Assert.False(tags.ContainsKey("GVL_IO.cMaxLevel"));                  // constants are not readable
        Assert.DoesNotContain(tags.Keys, k => k.StartsWith("F_Scale"));      // function locals are not readable
    }

    [Fact]
    public void Digest_explains_the_brand_and_lists_the_program()
    {
        var digest = ProgramDigest.Build(Export.Value, Plc);
        Assert.Contains("Brand: Beckhoff TwinCAT 3", digest);
        Assert.Contains("TwinCAT ADS paths", digest);
        Assert.Contains("Task PlcTask", digest);
        Assert.Contains("calls FB_Valve with instance fbInlet", digest);
        Assert.Contains("### PRG MAIN, ST", digest);
        Assert.Contains("bStartButton AT %I* : BOOL // Start push button S1", digest);
        Assert.Contains("CASE eState OF", digest);
        Assert.Contains("MAIN.fbInlet.bOpenCmd : BOOL", digest);
    }

    [Fact]
    public void Model_check_accepts_ads_symbols()
    {
        var model = new MachineModel
        {
            Name = "Fill",
            Tags =
            {
                new TagDef { Id = "Level", Symbol = "MAIN.fLevel", Type = "Real" },
                new TagDef { Id = "Wrong", Symbol = "MAIN.fLevelX", Type = "Real" },
            },
        };
        var problems = ModelCheck.Check(model, Plc);
        var symbolProblem = Assert.Single(problems, p => p.Contains("does not exist"));
        Assert.Contains("MAIN.fLevelX", symbolProblem);
    }

    [Fact]
    public void Loads_a_zipped_project_and_a_tnzip()
    {
        foreach (var ext in new[] { ".zip", ".tnzip" })
        {
            var file = Path.Combine(Path.GetTempPath(), "plcki-twincat-test-" + Guid.NewGuid().ToString("N")[..8] + ext);
            ZipFile.CreateFromDirectory(Folder, file);
            try { Assert.Equal(PlcBrands.Beckhoff, Assert.Single(ProgramExport.Load(file).Plcs).Brand); }
            finally { File.Delete(file); }
        }
    }

    [Fact]
    public void Siemens_exports_still_use_the_tia_reader()
    {
        var plc = Assert.Single(ProgramExport.Load(Path.Combine(AppContext.BaseDirectory, "data", "demo-export.zip")).Plcs);
        Assert.Equal(PlcBrands.Siemens, plc.Brand);
        Assert.DoesNotContain("Notes for this brand", ProgramDigest.Build(new ProgramExport(), plc));
    }
}
