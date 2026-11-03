using PlcKi.Core.Import;
using Xunit;

/// <summary>
/// Shapes seen in the first real plant export (TIA V20, 3 Nov 2026) that the demo did not have:
/// SCL networks inside LAD blocks, SCL calls of FCs without a Name attribute, and member names with spaces.
/// </summary>
public class RealExportShapeTests
{
    private const string St = "http://www.siemens.com/automation/Openness/SW/NetworkSource/StructuredText/v4";
    private const string Flg = "http://www.siemens.com/automation/Openness/SW/NetworkSource/FlgNet/v5";

    private static ExportPlc Load()
    {
        var dir = Path.Combine(Path.GetTempPath(), "plcki-shape-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(Path.Combine(dir, "PLC_1", "Blocks"));
        File.WriteAllText(Path.Combine(dir, "manifest.json"),
            """{ "project": "Shape", "plcs": [ { "name": "PLC_1", "folder": "PLC_1" } ], "problems": [] }""");
        File.WriteAllText(Path.Combine(dir, "PLC_1", "Blocks", "fbMixed.xml"), $"""
<?xml version="1.0" encoding="utf-8"?>
<Document>
  <SW.Blocks.FB ID="0">
    <AttributeList><Name>fbMixed</Name><Number>3</Number><ProgrammingLanguage>LAD</ProgrammingLanguage></AttributeList>
    <ObjectList>
      <SW.Blocks.CompileUnit ID="1">
        <AttributeList>
          <NetworkSource><StructuredText xmlns="{St}">
  <LineComment><Text> copy the alarm</Text></LineComment>
  <NewLine Num="1" />
  <Access Scope="LocalVariable"><Symbol><Component Name="Result" /><Token Text="." /><Component Name="ID" /></Symbol></Access>
  <Blank Num="1" /><Token Text=":=" /><Blank Num="1" />
  <Access Scope="GlobalVariable"><Symbol><Component Name="Client_Data" /><Token Text="." /><Component Name="Read list_1" /><Token Text="." /><Component Name="Alarm" /></Symbol></Access>
  <Token Text=";" /><NewLine Num="1" />
  <Access Scope="LocalVariable"><Symbol><Component Name="Text" /></Symbol></Access>
  <Blank Num="1" /><Token Text=":=" /><Blank Num="1" />
  <Access Scope="Call"><CallInfo BlockType="FC">
    <Instance Scope="GlobalVariable"><Component Name="VAL_To_String" /></Instance>
    <Token Text="(" />
    <Parameter Name="IN"><Blank Num="1" /><Token Text=":=" /><Blank Num="1" /><Access Scope="LiteralConstant"><Constant><ConstantValue>42</ConstantValue></Constant></Access></Parameter>
    <Token Text=")" />
  </CallInfo></Access>
  <Token Text=";" />
</StructuredText></NetworkSource>
          <ProgrammingLanguage>SCL</ProgrammingLanguage>
        </AttributeList>
        <ObjectList><MultilingualText CompositionName="Title"><ObjectList><MultilingualTextItem><AttributeList><Culture>en-US</Culture><Text>Alarm data</Text></AttributeList></MultilingualTextItem></ObjectList></MultilingualText></ObjectList>
      </SW.Blocks.CompileUnit>
    </ObjectList>
  </SW.Blocks.FB>
</Document>
""".TrimStart());
        return Assert.Single(ProgramExport.Load(dir).Plcs);
    }

    [Fact]
    public void Scl_network_in_a_lad_block_is_decoded()
    {
        var logic = Load().Block("fbMixed")!.DecodedLogic!;
        Assert.Contains("(SCL)", logic);
        Assert.Contains("// copy the alarm", logic);
        Assert.Contains("#Result.ID := \"Client_Data\".\"Read list_1\".Alarm;", logic);
        Assert.Contains("#Text := \"VAL_To_String\"(IN := 42);", logic);
    }

    [Fact]
    public void Scl_fc_call_without_name_is_a_call_of_that_fc()
    {
        var call = Assert.Single(Load().Block("fbMixed")!.Calls);
        Assert.Equal("VAL_To_String", call.Block);
        Assert.Null(call.Instance);
    }

    [Fact]
    public void Graph_runtime_structures_give_only_step_active_and_time()
    {
        var export = ProgramExport.Load(Path.Combine(AppContext.BaseDirectory, "data", "demo-export.zip"));
        var tags = ProgramExport.Tags(Assert.Single(export.Plcs)).Select(t => t.Symbol).ToList();
        var seq = tags.Where(t => t.StartsWith("\"DB_BatchSequence\".")).ToList();
        Assert.Contains(seq, t => t.EndsWith(".X"));
        Assert.Contains(seq, t => t.EndsWith(".T"));
        Assert.DoesNotContain(seq, t => t.EndsWith(".TT") || t.EndsWith(".CRIT") || t.EndsWith(".TV"));
        Assert.Contains("\"DB_BatchSequence\".Halted", seq);   // the sequence's own statics stay
    }

    [Fact]
    public void Named_constant_in_a_comparison_is_shown_by_name()
    {
        var x = System.Xml.Linq.XElement.Parse("""<Access xmlns="urn:x" Scope="LocalConstant"><Constant Name="cRobPos_Home" /></Access>""");
        Assert.Equal("#cRobPos_Home", NetworkDecoder.AccessText(x));
    }

    [Fact]
    public void Scl_source_is_found_for_a_block_name_with_a_slash()
    {
        var dir = Path.Combine(Path.GetTempPath(), "plcki-slash-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(Path.Combine(dir, "PLC_1", "Blocks"));
        Directory.CreateDirectory(Path.Combine(dir, "PLC_1", "SclSource"));
        File.WriteAllText(Path.Combine(dir, "manifest.json"), """{ "project": "S", "plcs": [ { "name": "PLC_1", "folder": "PLC_1" } ] }""");
        File.WriteAllText(Path.Combine(dir, "PLC_1", "Blocks", "0.2. Inputs_Outputs Images.xml"), """
<Document><SW.Blocks.FC ID="0"><AttributeList><Name>0.2. Inputs/Outputs Images</Name><ProgrammingLanguage>SCL</ProgrammingLanguage></AttributeList></SW.Blocks.FC></Document>
""".Trim());
        File.WriteAllText(Path.Combine(dir, "PLC_1", "SclSource", "0.2. Inputs_Outputs Images.scl"), "\"mDI_CartInPos1\" := \"DI_CartInPos1\";");
        var plc = Assert.Single(ProgramExport.Load(dir).Plcs);
        Assert.Contains("mDI_CartInPos1", plc.Block("0.2. Inputs/Outputs Images")!.SclSource);
    }

    [Fact]
    public void Block_with_a_slash_can_be_read_for_editing()
    {
        var dir = Path.Combine(Path.GetTempPath(), "plcki-edit-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(Path.Combine(dir, "PLC_1", "SclSource"));
        File.WriteAllText(Path.Combine(dir, "manifest.json"), """{ "project": "S", "plcs": [ { "name": "PLC_1", "folder": "PLC_1" } ] }""");
        File.WriteAllText(Path.Combine(dir, "PLC_1", "SclSource", "0.2. Inputs_Outputs Images.scl"), "FUNCTION \"0.2. Inputs/Outputs Images\" : Void");
        var found = Assert.Single(PlcKi.Core.Generate.ExportSources.Read(dir, null, new[] { "0.2. Inputs/Outputs Images" }));
        Assert.Equal(".scl", found.Extension);
    }
}
