using PlcKi.Core.Import;
using PlcKi.Core.Status;
using Xunit;

public class WriteRulesTests
{
    private static ExportPlc Plant()
    {
        var plc = new ExportPlc { Name = "PLC_1" };
        var fMain = new ExportBlock { Name = "F_Main", Kind = "FB", Language = "F_LAD" };
        fMain.GlobalNames.Add("FDI_Door");
        fMain.GlobalNames.Add("DB_SafetyBits");
        plc.Blocks.Add(fMain);
        plc.Blocks.Add(new ExportBlock { Name = "F_Main_DB", Kind = "InstanceDB", Language = "DB", InstanceOf = "F_Main" });
        plc.Blocks.Add(new ExportBlock { Name = "F_GlobDB", Kind = "GlobalDB", Language = "F_DB" });
        plc.Blocks.Add(new ExportBlock { Name = "DB_SafetyBits", Kind = "GlobalDB", Language = "DB" });
        plc.Blocks.Add(new ExportBlock { Name = "DB_Station", Kind = "GlobalDB", Language = "DB" });
        plc.Blocks.Add(new ExportBlock { Name = "RTG1SysInfo", Kind = "GlobalDB", Language = "DB" });
        plc.PlcTags.Add(new ExportPlcTag("FDI_Door", "Bool", "%M900.1", null, "Sim"));
        plc.PlcTags.Add(new ExportPlcTag("DI_BluePB", "Bool", "%I0.3", null, "Inputs"));
        plc.PlcTags.Add(new ExportPlcTag("DO_Lamp", "Bool", "%Q1.0", null, "Outputs"));
        plc.PlcTags.Add(new ExportPlcTag("Sim_Speed", "Int", "%MW10", null, "Sim"));
        return plc;
    }

    [Theory]
    [InlineData("\"F_GlobDB\".Ack", "safety program")]
    [InlineData("\"F_Main_DB\".x", "safety block F_Main")]
    [InlineData("\"FDI_Door\"", "used by the safety program")]
    [InlineData("\"DB_SafetyBits\".Bit1", "used by the safety program")]
    [InlineData("\"RTG1SysInfo\".MODE", "safety system")]
    public void Safety_values_are_never_changed(string symbol, string why)
    {
        Assert.Contains(why, WriteRules.SafetyReason(Plant(), symbol));
    }

    [Theory]
    [InlineData("\"DB_Station\".Speed")]
    [InlineData("\"Sim_Speed\"")]
    [InlineData("DI_BluePB")]
    public void Normal_values_may_be_changed(string symbol) => Assert.Null(WriteRules.SafetyReason(Plant(), symbol));

    [Fact]
    public void Without_a_program_nothing_may_be_changed() => Assert.NotNull(WriteRules.SafetyReason(null, "\"DB_Station\".Speed"));

    [Fact]
    public void Real_inputs_and_outputs_are_refused_memory_is_not()
    {
        var plc = Plant();
        Assert.Equal("%I0.3", WriteRules.Address(plc, "\"DI_BluePB\""));
        Assert.Contains("input", WriteRules.RealIoReason(WriteRules.Address(plc, "\"DI_BluePB\"")));
        Assert.Contains("output", WriteRules.RealIoReason(WriteRules.Address(plc, "DO_Lamp")));
        Assert.Null(WriteRules.RealIoReason(WriteRules.Address(plc, "\"Sim_Speed\"")));
        Assert.Null(WriteRules.Address(plc, "\"DB_Station\".Speed"));
    }

    [Fact]
    public void Typed_values_become_the_plc_type()
    {
        Assert.Equal(true, WriteRules.Parse("TRUE", typeof(bool)));
        Assert.Equal(false, WriteRules.Parse("0", typeof(bool)));
        Assert.Equal((short)-5, WriteRules.Parse("-5", typeof(short)));
        Assert.Equal((ushort)255, WriteRules.Parse("16#FF", typeof(ushort)));
        Assert.Equal(12.5f, WriteRules.Parse("12,5", typeof(float)));
        Assert.Equal(90_000, WriteRules.Parse("T#1M30S", typeof(int), isTime: true));
        Assert.Equal(250, WriteRules.Parse("250ms", typeof(int), isTime: true));
        Assert.Equal(5000, WriteRules.Parse("5s", typeof(int), isTime: true));
        Assert.Equal("abc", WriteRules.Parse("'abc'", typeof(string)));
    }

    [Theory]
    [InlineData("40000", typeof(short), "does not fit")]
    [InlineData("maybe", typeof(bool), "TRUE or FALSE")]
    [InlineData("1.5", typeof(int), "not a whole number")]
    [InlineData("", typeof(float), "Type a value")]
    public void Wrong_values_say_why(string text, Type type, string why)
    {
        var ex = Assert.Throws<FormatException>(() => WriteRules.Parse(text, type));
        Assert.Contains(why, ex.Message);
    }
}
