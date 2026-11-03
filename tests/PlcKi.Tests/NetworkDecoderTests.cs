using System.Xml.Linq;
using PlcKi.Core.Import;
using Xunit;

/// <summary>LAD and GRAPH networks decoded into SCL-like text, from the real V20 demo export and small hand-made networks.</summary>
public class NetworkDecoderTests
{
    private static readonly Lazy<ProgramExport> Export = new(() =>
        ProgramExport.Load(Path.Combine(AppContext.BaseDirectory, "data", "demo-export.zip")));
    private static readonly Lazy<ExportPlc> Plc = new(() => Assert.Single(Export.Value.Plcs));

    [Fact]
    public void Lad_valve_networks_become_statements()
    {
        var logic = Plc.Value.Block("FB_Valve")!.DecodedLogic!;
        Assert.Contains("// Network 1 (LAD): Open command", logic);
        Assert.Contains("#Out := #Open AND #Permit;", logic);
        Assert.Contains("#IsOpen := #ZSO AND NOT #ZSC;", logic);
        Assert.Contains("#tOpen(IN := #Out AND NOT #IsOpen, PT := #MonitorTime);   // TON", logic);
        Assert.Contains("#FaultNotOpen := #tOpen.Q;", logic);
    }

    [Fact]
    public void Lad_math_and_moves_keep_their_enable_condition()
    {
        var logic = Plc.Value.Block("FB_VFD")!.DecodedLogic!;
        Assert.Contains("IF #RunOut THEN #SpeedOut := #SpeedSP; END_IF;", logic);
        Assert.Contains("#SpeedMin := 0.9 * #SpeedSP;", logic);
        Assert.Contains("#AtSpeed := #RunOut AND #SpeedPV >= #SpeedMin;", logic);
    }

    [Fact]
    public void Graph_shows_steps_interlocks_and_real_transition_conditions()
    {
        var logic = Plc.Value.Block("FB_BatchSequence")!.DecodedLogic!;
        Assert.Contains("Step 1 Idle (initial step)", logic);
        Assert.Contains("action N #OpenXV101 (only while the interlock is fulfilled)", logic);
        Assert.Contains("interlock: #Healthy", logic);
        Assert.Contains("Transition T2 T2_LevelA_Reached: step 10 FillA -> step 20 FillB when NOT #Halted AND #Level >= \"DB_Recipe\".LevelA", logic);
        Assert.Contains("Transition T7 T7_BatchCounted: step 60 Done -> step 1 Idle when #Done.X", logic);
        Assert.Contains("#Healthy := NOT #AnyFault AND #EStopOK AND NOT #Paused AND #Auto;", logic);
        Assert.Contains("IF #Stop THEN #Paused := TRUE; END_IF;   // set", logic);
        Assert.Contains("IF #AnyFault THEN #Paused := TRUE; END_IF;   // set", logic);
        Assert.Contains("IF NOT #EStopOK THEN #Paused := TRUE; END_IF;   // set", logic);
        Assert.DoesNotContain(".TT", logic);
        Assert.DoesNotContain("NoHeating", logic);
    }

    [Fact]
    public void Export_has_the_current_demo_program()
    {
        // v7: step timeouts are edge-latched, each with its own previous-cycle bit, and the heating check is in FB_Station.
        var alarms = Plc.Value.Block("FB_AlarmHandler")!.SclSource!;
        foreach (var (step, alarm) in new[] { (10, "A16"), (20, "A17"), (30, "A18"), (50, "A19") })
            Assert.Contains($"{alarm}_Step{step}_Timeout := (#Step{step}_Timeout AND NOT #Step{step}_TimeoutOld)", alarms);
        var station = Plc.Value.Block("FB_Station")!.SclSource!;
        Assert.Contains("#NoHeating := #tHeat.Q AND #TT301.Value < #TempAtHeatOn + 2.0;", station);
        Assert.Contains("E301_NoHeating := #NoHeating", station);
    }

    [Fact]
    public void Digest_contains_the_decoded_logic_instead_of_the_placeholder()
    {
        var digest = ProgramDigest.Build(Export.Value, Plc.Value);
        Assert.Contains("Logic (decoded from GRAPH networks", digest);
        Assert.Contains("Logic (decoded from LAD networks", digest);
        Assert.DoesNotContain("logic not included yet", digest);
    }

    private static readonly XNamespace Ns = "http://www.siemens.com/automation/Openness/SW/NetworkSource/FlgNet/v5";

    private static XElement Net(string parts, string wires) =>
        XElement.Parse($"<FlgNet xmlns=\"{Ns}\"><Parts>{parts}</Parts><Wires>{wires}</Wires></FlgNet>");

    private static string Var(string uid, string name) =>
        $"<Access Scope=\"LocalVariable\" UId=\"{uid}\"><Symbol><Component Name=\"{name}\" /></Symbol></Access>";

    [Fact]
    public void Parallel_branches_become_or_and_negated_coils_not()
    {
        // (A OR B) AND NOT C -> Q ; and a negated coil: R := NOT ((A OR B) AND NOT C)
        var flg = Net(
            Var("1", "A") + Var("2", "B") + Var("3", "C") + Var("4", "Q") + Var("5", "R") +
            "<Part Name=\"Contact\" UId=\"10\" /><Part Name=\"Contact\" UId=\"11\" />" +
            "<Part Name=\"O\" UId=\"12\"><TemplateValue Name=\"Card\" Type=\"Cardinality\">2</TemplateValue></Part>" +
            "<Part Name=\"Contact\" UId=\"13\"><Negated Name=\"operand\" /></Part>" +
            "<Part Name=\"Coil\" UId=\"14\" /><Part Name=\"Coil\" UId=\"15\"><Negated Name=\"operand\" /></Part>",
            "<Wire UId=\"20\"><Powerrail /><NameCon UId=\"10\" Name=\"in\" /><NameCon UId=\"11\" Name=\"in\" /></Wire>" +
            "<Wire UId=\"21\"><IdentCon UId=\"1\" /><NameCon UId=\"10\" Name=\"operand\" /></Wire>" +
            "<Wire UId=\"22\"><IdentCon UId=\"2\" /><NameCon UId=\"11\" Name=\"operand\" /></Wire>" +
            "<Wire UId=\"23\"><NameCon UId=\"10\" Name=\"out\" /><NameCon UId=\"12\" Name=\"in1\" /></Wire>" +
            "<Wire UId=\"24\"><NameCon UId=\"11\" Name=\"out\" /><NameCon UId=\"12\" Name=\"in2\" /></Wire>" +
            "<Wire UId=\"25\"><NameCon UId=\"12\" Name=\"out\" /><NameCon UId=\"13\" Name=\"in\" /></Wire>" +
            "<Wire UId=\"26\"><IdentCon UId=\"3\" /><NameCon UId=\"13\" Name=\"operand\" /></Wire>" +
            "<Wire UId=\"27\"><NameCon UId=\"13\" Name=\"out\" /><NameCon UId=\"14\" Name=\"in\" /></Wire>" +
            "<Wire UId=\"28\"><IdentCon UId=\"4\" /><NameCon UId=\"14\" Name=\"operand\" /></Wire>" +
            "<Wire UId=\"29\"><NameCon UId=\"14\" Name=\"out\" /><NameCon UId=\"15\" Name=\"in\" /></Wire>" +
            "<Wire UId=\"30\"><IdentCon UId=\"5\" /><NameCon UId=\"15\" Name=\"operand\" /></Wire>");
        var lines = NetworkDecoder.Decode(flg);
        Assert.Equal(new[] { "#Q := (#A OR #B) AND NOT #C;", "#R := NOT ((#A OR #B) AND NOT #C);" }, lines);
    }

    [Fact]
    public void Global_symbols_array_indexes_and_constants_are_written_like_scl()
    {
        var access = XElement.Parse(
            $"<Access xmlns=\"{Ns}\" Scope=\"GlobalVariable\" UId=\"1\"><Symbol><Component Name=\"DB_IO\" />" +
            "<Component Name=\"Valve\" AccessModifier=\"Array\"><Access Scope=\"LiteralConstant\"><Constant><ConstantValue>3</ConstantValue></Constant></Access></Component>" +
            "<Component Name=\"Open\" /></Symbol></Access>");
        Assert.Equal("\"DB_IO\".Valve[3].Open", NetworkDecoder.AccessText(access));
    }
}
