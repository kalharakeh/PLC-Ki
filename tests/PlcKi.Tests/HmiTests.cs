using System.Text.Json;
using PlcKi.Core.Import;
using PlcKi.Core.Model;
using Xunit;

/// <summary>
/// The HMI part of an export (WinCC Comfort/Advanced SimaticML, shapes as TIA V18-V20 write them): operator settings
/// with the text next to them, buttons that set PLC bits, text lists, and how PLC-Ki uses them.
/// </summary>
public class HmiTests
{
    private const string If = "http://www.siemens.com/automation/Openness/SW/Interface/v5";

    private static string Mt(string composition, string text) => $"""
      <MultilingualText ID="90" CompositionName="{composition}"><ObjectList>
        <MultilingualTextItem ID="91" CompositionName="Items"><AttributeList><Culture>de-DE</Culture><Text /></AttributeList></MultilingualTextItem>
        <MultilingualTextItem ID="92" CompositionName="Items"><AttributeList><Culture>en-US</Culture><Text><body><p>{text}</p></body></Text></AttributeList></MultilingualTextItem>
      </ObjectList></MultilingualText>
""";

    private static string Tag(string name, string controller) => $"""
      <Hmi.Tag.Tag ID="1" CompositionName="Tags">
        <AttributeList><AcquisitionTriggerMode>Visible</AcquisitionTriggerMode><Name>{name}</Name><StartValue /></AttributeList>
        <LinkList>
          <AcquisitionCycle TargetID="@OpenLink"><Name>1 s</Name></AcquisitionCycle>
          <Connection TargetID="@OpenLink"><Name>HMI_Connection_1</Name></Connection>
          <ControllerTag TargetID="@OpenLink"><Name>{controller}</Name></ControllerTag>
          <DataType TargetID="@OpenLink"><Name>Real</Name></DataType>
        </LinkList>
        <ObjectList>{Mt("Comment", "")}</ObjectList>
      </Hmi.Tag.Tag>
""";

    private static string Text(int left, int top, int width, string text) => $"""
          <Hmi.Screen.TextField ID="7" CompositionName="ScreenItems">
            <AttributeList><Height>28</Height><Left>{left}</Left><ObjectName>Text_{left}_{top}</ObjectName><Top>{top}</Top><Width>{width}</Width></AttributeList>
            <ObjectList>{Mt("Text", text)}</ObjectList>
          </Hmi.Screen.TextField>
""";

    private static string Io(int left, int top, string mode, string tag, string unit = "") => $"""
          <Hmi.Screen.IOField ID="A9" CompositionName="ScreenItems">
            <AttributeList><FormatPattern>s999.9</FormatPattern><Height>28</Height><Left>{left}</Left><Mode>{mode}</Mode><ObjectName>IO_{top}</ObjectName><Top>{top}</Top><Unit>{unit}</Unit><Width>80</Width></AttributeList>
            <LinkList><Authorization TargetID="@OpenLink"><Name>Manage Parameter</Name></Authorization></LinkList>
            <ObjectList>
              {Mt("HelpText", "")}
              <Hmi.Screen.Property ID="B3" CompositionName="Properties">
                <AttributeList><Name>ProcessValue</Name></AttributeList>
                <ObjectList><Hmi.Dynamic.TagConnectionDynamic ID="B4" CompositionName="Dynamic">
                  <AttributeList><Indirect>false</Indirect></AttributeList>
                  <LinkList><Tag TargetID="@OpenLink"><Name>{tag}</Name></Tag></LinkList>
                </Hmi.Dynamic.TagConnectionDynamic></ObjectList>
              </Hmi.Screen.Property>
            </ObjectList>
          </Hmi.Screen.IOField>
""";

    private const string Button = """
          <Hmi.Screen.Button ID="7" CompositionName="ScreenItems">
            <AttributeList><Height>60</Height><Left>1000</Left><ObjectName>BtnReset</ObjectName><Top>700</Top><Width>150</Width></AttributeList>
            <ObjectList>
              <Hmi.Event.Event ID="A" CompositionName="Events">
                <AttributeList><Name>Press</Name></AttributeList>
                <ObjectList><Hmi.Event.FunctionListEventHandler ID="B" CompositionName="EventHandler"><ObjectList>
                  <Hmi.Event.FunctionListEntry ID="C" CompositionName="FunctionListEntries">
                    <AttributeList><Name>SetBitWhileKeyPressed</Name><Type>SystemFunction</Type></AttributeList>
                    <ObjectList>
                      <Hmi.Event.FunctionListEntryParameter ID="D" CompositionName="Parameters">
                        <AttributeList><Name>Tag (Output)</Name></AttributeList>
                        <LinkList><Value TargetID="@OpenLink"><Name>Cmd_Reset</Name></Value></LinkList>
                      </Hmi.Event.FunctionListEntryParameter>
                    </ObjectList>
                  </Hmi.Event.FunctionListEntry>
                </ObjectList></Hmi.Event.FunctionListEventHandler></ObjectList>
              </Hmi.Event.Event>
              <MultilingualText ID="1B" CompositionName="TextOff"><ObjectList>
                <MultilingualTextItem ID="1C" CompositionName="Items"><AttributeList><Culture>en-US</Culture><Text><body><p>Reset</p></body></Text></AttributeList></MultilingualTextItem>
              </ObjectList></MultilingualText>
            </ObjectList>
          </Hmi.Screen.Button>
""";

    /// <summary>An export with PLC_1 (DB "HMI" with two values, DB "System" with a reset bit) and panel HMI_1.</summary>
    internal static string Export()
    {
        var dir = Path.Combine(Path.GetTempPath(), "plcki-hmi-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(Path.Combine(dir, "PLC_1", "Blocks"));
        File.WriteAllText(Path.Combine(dir, "manifest.json"), """
            { "project": "Helium", "plcs": [ { "name": "PLC_1", "folder": "PLC_1" } ], "problems": [],
              "hmis": [ { "name": "HMI_1", "kind": "classic", "folder": "HMI/HMI_1" } ] }
            """);
        string Db(string name, string members) => $"""
<?xml version="1.0" encoding="utf-8"?>
<Document><SW.Blocks.GlobalDB ID="0"><AttributeList>
  <Interface><Sections xmlns="{If}"><Section Name="Static">{members}</Section></Sections></Interface>
  <Name>{name}</Name><Number>5</Number><ProgrammingLanguage>DB</ProgrammingLanguage>
</AttributeList></SW.Blocks.GlobalDB></Document>
""".TrimStart();
        File.WriteAllText(Path.Combine(dir, "PLC_1", "Blocks", "HMI.xml"), Db("HMI",
            """<Member Name="CalibrationLeakRateCheckTolerance" Datatype="Real" Remanence="Retain" /><Member Name="DelayBetweenCycles" Datatype="Time" /><Member Name="ActualLeakRate" Datatype="Real" />"""));
        File.WriteAllText(Path.Combine(dir, "PLC_1", "Blocks", "System.xml"), Db("System", """<Member Name="Cmd_Reset_Bit" Datatype="Bool" />"""));

        var hmi = Path.Combine(dir, "HMI", "HMI_1");
        Directory.CreateDirectory(Path.Combine(hmi, "Tags"));
        Directory.CreateDirectory(Path.Combine(hmi, "Screens", "Parameters"));
        Directory.CreateDirectory(Path.Combine(hmi, "TextLists"));
        File.WriteAllText(Path.Combine(hmi, "hmi.json"), """{ "name": "HMI_1", "device": "HMI_1", "typeIdentifier": "OrderNumber:6AV2 124-0MC01-0AX0", "kind": "classic" }""");
        File.WriteAllText(Path.Combine(hmi, "Tags", "Default tag table.xml"), $"""
<?xml version="1.0" encoding="utf-8"?>
<Document><Engineering version="V20" />
  <Hmi.Tag.TagTable ID="0"><AttributeList><Name>Default tag table</Name></AttributeList><ObjectList>
{Tag("CalibrationLeakRateCheckTolerance", "HMI.CalibrationLeakRateCheckTolerance")}
{Tag("DelayBetweenCycles", "HMI.DelayBetweenCycles")}
{Tag("LeakRate", "HMI.ActualLeakRate")}
{Tag("Cmd_Reset", "System.Cmd_Reset_Bit")}
  </ObjectList></Hmi.Tag.TagTable>
</Document>
""".TrimStart());
        File.WriteAllText(Path.Combine(hmi, "Screens", "Parameters", "Leak test.xml"), $"""
<?xml version="1.0" encoding="utf-8"?>
<Document><Engineering version="V20" />
  <Hmi.Screen.Screen ID="0">
    <AttributeList><Height>800</Height><Name>Leak test</Name><Number>12</Number><Width>1280</Width></AttributeList>
    <ObjectList>
      <Hmi.Screen.ScreenLayer ID="6" CompositionName="Layers"><AttributeList><Index>0</Index></AttributeList><ObjectList>
{Text(20, 20, 600, "Leak test parameters")}
{Text(40, 120, 300, "Calibration tolerance")}
{Io(360, 120, "InputOutput", "CalibrationLeakRateCheckTolerance", "%")}
{Text(40, 170, 300, "Delay between cycles")}
{Io(360, 170, "Input", "DelayBetweenCycles", "ms")}
{Text(40, 220, 300, "Actual leak rate")}
{Io(360, 220, "Output", "LeakRate")}
{Button}
      </ObjectList></Hmi.Screen.ScreenLayer>
    </ObjectList>
  </Hmi.Screen.Screen>
</Document>
""".TrimStart());
        File.WriteAllText(Path.Combine(hmi, "TextLists", "PackML states.xml"), $"""
<?xml version="1.0" encoding="utf-8"?>
<Document><Engineering version="V20" />
  <Hmi.TextGraphicList.TextList ID="0"><AttributeList><ListRange>Decimal</ListRange><Name>PackML states</Name></AttributeList><ObjectList>
    <Hmi.TextGraphicList.TextListEntry ID="3" CompositionName="Entries"><AttributeList><DefaultEntry>false</DefaultEntry><EntryType>SingleValue</EntryType><From>0</From><To>0</To></AttributeList>
      <ObjectList>{Mt("Text", "Stopped")}</ObjectList></Hmi.TextGraphicList.TextListEntry>
    <Hmi.TextGraphicList.TextListEntry ID="6" CompositionName="Entries"><AttributeList><DefaultEntry>false</DefaultEntry><EntryType>Range</EntryType><From>1</From><To>3</To></AttributeList>
      <ObjectList>{Mt("Text", "Starting")}</ObjectList></Hmi.TextGraphicList.TextListEntry>
  </ObjectList></Hmi.TextGraphicList.TextList>
</Document>
""".TrimStart());
        return dir;
    }

    [Fact]
    public void Panel_settings_have_the_text_next_to_them_and_the_plc_value_behind_them()
    {
        var export = ProgramExport.Load(Export());
        var hmi = Assert.Single(export.Hmis);
        Assert.Equal("6AV2 124-0MC01-0AX0", hmi.Type);
        Assert.Equal(4, hmi.Tags.Count);
        var screen = Assert.Single(hmi.Screens);
        Assert.Equal(12, screen.Number);
        Assert.Equal("Parameters", screen.Folder);
        Assert.Contains("Leak test parameters", screen.Texts);

        var settings = hmi.OperatorSettings().ToList();
        Assert.Equal(2, settings.Count);   // the output field (actual leak rate) is not a setting
        var tol = settings[0];
        Assert.Equal("Calibration tolerance", tol.Label);
        Assert.Equal("\"HMI\".CalibrationLeakRateCheckTolerance", tol.PlcSymbol);
        Assert.Equal("%", tol.Unit);
        Assert.Equal("Manage Parameter", tol.Authorization);
        Assert.Equal("Delay between cycles", settings[1].Label);
        Assert.Equal("Actual leak rate", screen.Fields.Single(f => f.Tag == "LeakRate").Label);

        var reset = Assert.Single(hmi.OperatorCommands());
        Assert.Equal("Reset", reset.Label);
        Assert.Equal("SetBitWhileKeyPressed on Press", reset.Action);
        Assert.Equal("\"System\".Cmd_Reset_Bit", reset.PlcSymbol);

        var list = Assert.Single(hmi.TextLists);
        Assert.Equal(new[] { ("0", "Stopped"), ("1..3", "Starting") }, list.Entries);
        Assert.Empty(hmi.Problems);
    }

    [Fact]
    public void The_import_text_lists_the_panel_settings_with_exact_plc_symbols()
    {
        var export = ProgramExport.Load(Export());
        var digest = ProgramDigest.Build(export, export.Plcs[0]);
        Assert.Contains("## HMI panel HMI_1 (6AV2 124-0MC01-0AX0), WinCC Comfort/Advanced", digest);
        Assert.Contains("- screen \"Leak test\" (no. 12) > \"Calibration tolerance\" -> \"HMI\".CalibrationLeakRateCheckTolerance [%], user group Manage Parameter", digest);
        Assert.Contains("> \"Reset\" -> SetBitWhileKeyPressed on Press \"System\".Cmd_Reset_Bit", digest);
        Assert.Contains("- PackML states: 0=Stopped; 1..3=Starting", digest);
        Assert.Contains("alarm texts come from the PLC program", digest);
        Assert.DoesNotContain("LeakRate\" ->", digest);
    }

    [Fact]
    public void Settings_tab_finds_each_model_setting_on_the_panel()
    {
        var model = new MachineModel
        {
            Tags = { new TagDef { Id = "CalTolerance", Symbol = "\"HMI\".CalibrationLeakRateCheckTolerance", Type = "Real" } },
            Settings = { new SettingDef { Tag = "CalTolerance", Valid = "CalTolerance > 0" } },
        };
        var program = new PlcKi.Server.Jobs.ProgramService(new PlcKi.Server.Live.PlcKiOptions { Export = Export() }, model, p => p);
        var json = JsonSerializer.SerializeToElement(program.Hmi());
        Assert.True(json.GetProperty("available").GetBoolean());
        Assert.Equal("Screen Leak test > Calibration tolerance", json.GetProperty("bySetting").GetProperty("CalTolerance").GetString());
        var rows = json.GetProperty("settings").EnumerateArray().ToList();
        Assert.Equal(2, rows.Count);
        Assert.Equal("CalTolerance", rows[0].GetProperty("modelTag").GetString());
        Assert.Equal("Real", rows[0].GetProperty("type").GetString());
        Assert.Equal("Time", rows[1].GetProperty("type").GetString());
        Assert.Equal(JsonValueKind.Null, rows[1].GetProperty("modelTag").ValueKind);
        Assert.Single(json.GetProperty("commands").EnumerateArray());
    }

    [Fact]
    public void An_export_without_hmi_says_how_to_get_it()
    {
        var data = Path.Combine(AppContext.BaseDirectory, "data");
        var program = new PlcKi.Server.Jobs.ProgramService(new PlcKi.Server.Live.PlcKiOptions { Export = Path.Combine(data, "demo-export.zip") }, TestData.Model, p => p);
        var json = JsonSerializer.SerializeToElement(program.Hmi());
        Assert.False(json.GetProperty("available").GetBoolean());
        Assert.Contains("Export the program again", json.GetProperty("error").GetString());
    }
}
