using PlcKi.Core.Hardware;
using PlcKi.Core.Import;
using PlcKi.Core.Status;
using Xunit;

/// <summary>"Hold on PLCSIM": PLCSIM only (confirmed by the TIA helper), inputs only, never safety I/O.</summary>
public class HoldRulesTests
{
    private const string Sim = "192.168.10.10";

    private static ExportPlc Plant(bool safety = true)
    {
        var plc = new ExportPlc { Name = "PLC_1" };
        if (safety)
        {
            var fMain = new ExportBlock { Name = "F_Main", Kind = "FB", Language = "F_LAD" };
            fMain.GlobalNames.Add("FDI_Door");
            plc.Blocks.Add(fMain);
        }
        plc.Blocks.Add(new ExportBlock { Name = "DB_Station", Kind = "GlobalDB", Language = "DB" });
        plc.PlcTags.Add(new ExportPlcTag("DI_BluePB", "Bool", "%I0.3", null, "Inputs"));
        plc.PlcTags.Add(new ExportPlcTag("AI_Pressure", "Int", "%IW64", null, "Inputs"));
        plc.PlcTags.Add(new ExportPlcTag("AI_Flow", "Real", "%ID100", null, "Inputs"));
        plc.PlcTags.Add(new ExportPlcTag("FDI_Door", "Bool", "%I20.0", null, "Safety"));
        plc.PlcTags.Add(new ExportPlcTag("DI_OnFModule", "Bool", "%I21.4", null, "Inputs"));
        plc.PlcTags.Add(new ExportPlcTag("DO_Lamp", "Bool", "%Q1.0", null, "Outputs"));
        plc.PlcTags.Add(new ExportPlcTag("Sim_Speed", "Int", "%MW10", null, "Sim"));
        plc.PlcTags.Add(new ExportPlcTag("DI_Odd", "Int", "%I2.0", null, "Inputs"));
        plc.PlcTags.Add(new ExportPlcTag("DI_Time", "Time", "%ID8", null, "Inputs"));
        return plc;
    }

    private static HardwareConfig Hardware()
    {
        var hw = new HardwareConfig();
        var st = new HwStation { Name = "ET200SP_1", Kind = "io" };
        var di = new HwModule { Name = "DI 16", Title = "DI 16x24VDC ST", Kind = "di" };
        di.Addresses.Add(new HwAddress("Input", 0, 2));
        var fdi = new HwModule { Name = "F-DI", Title = "F-DI 8x24VDC HF", Kind = "f-di", Safety = true };
        fdi.Addresses.Add(new HwAddress("Input", 20, 6));
        st.Modules.Add(di);
        st.Modules.Add(fdi);
        hw.Stations.Add(st);
        return hw;
    }

    private static string? Hold(string symbol, ExportPlc? plc = null, HardwareConfig? hw = null, string? sim = null) =>
        HoldRules.Refusal(plc ?? Plant(), hw ?? Hardware(), symbol, sim);

    [Theory]
    [InlineData("\"DI_BluePB\"")]
    [InlineData("DI_BluePB")]
    [InlineData("\"AI_Pressure\"")]
    [InlineData("\"AI_Flow\"")]
    public void Plain_inputs_on_plcsim_may_be_held(string symbol) => Assert.Null(Hold(symbol));

    [Fact]
    public void Not_plcsim_is_refused_whatever_the_value()
    {
        var why = HoldRules.PlcsimRefusal(Sim, applies: false, found: false, null, "The PLC at 192.168.10.10 is not the PLCSIM CPU.", null, null);
        Assert.Contains("PLCSIM only", why);
        Assert.Equal(why, Hold("\"DI_BluePB\"", sim: why));
    }

    [Fact]
    public void Same_address_but_no_running_instance_is_refused() =>
        Assert.Contains("no running PLCSIM instance", HoldRules.PlcsimRefusal(Sim, true, false, null, null, "RUN", null));

    [Fact]
    public void Run_stop_mismatch_means_maybe_not_the_same_cpu() =>
        Assert.Contains("may not be the same CPU", HoldRules.PlcsimRefusal(Sim, true, true, "Helium_sim", null, "RUN", "Stop"));

    [Theory]
    [InlineData("STOP", "Stop")]
    [InlineData(null, "Run")]
    [InlineData("Startup", "Run")]
    public void Confirmed_plcsim_is_accepted(string? plcState, string simState) =>
        Assert.Null(HoldRules.PlcsimRefusal(Sim, true, true, "Helium_sim", null, plcState, simState));

    [Theory]
    [InlineData("\"DO_Lamp\"", "output")]
    [InlineData("\"Sim_Speed\"", "memory value")]
    [InlineData("\"DB_Station\".Speed", "data block value")]
    public void Outputs_memory_and_data_are_not_held(string symbol, string why) => Assert.Contains(why, Hold(symbol));

    [Fact]
    public void An_unknown_tag_is_named_as_unknown() => Assert.Contains("not in the loaded program", Hold("\"NoSuchTag\""));

    [Fact]
    public void A_tag_the_safety_program_uses_is_refused() => Assert.Contains("safety program", Hold("\"FDI_Door\""));

    [Fact]
    public void A_byte_on_an_f_module_is_refused() => Assert.Contains("F-DI 8x24VDC HF in ET200SP_1", Hold("\"DI_OnFModule\""));

    [Fact]
    public void A_word_that_overlaps_an_f_module_is_refused()
    {
        var plc = Plant();
        plc.PlcTags.Add(new ExportPlcTag("AI_Edge", "Int", "%IW19", null, "Inputs"));   // bytes 19 and 20: 20 is on the F-DI
        Assert.Contains("safety module", Hold("\"AI_Edge\"", plc));
    }

    [Fact]
    public void Without_hardware_only_a_program_without_safety_may_hold()
    {
        Assert.Contains("No hardware export", HoldRules.Refusal(Plant(), null, "\"DI_BluePB\"", null));
        Assert.Null(HoldRules.Refusal(Plant(safety: false), null, "\"DI_BluePB\"", null));
    }

    [Fact]
    public void Without_a_program_nothing_is_held() => Assert.NotNull(HoldRules.Refusal(null, Hardware(), "\"DI_BluePB\"", null));

    [Theory]
    [InlineData("\"DI_Odd\"", "does not fit")]
    [InlineData("\"DI_Time\"", "only Bool")]
    public void Address_and_type_must_agree(string symbol, string why) => Assert.Contains(why, Hold(symbol));

    [Theory]
    [InlineData("%I0.3", 'I', 0, 3, 0, false)]
    [InlineData("%IW64:P", 'I', 64, null, 2, true)]
    [InlineData("E 2.1", 'I', 2, 1, 0, false)]
    [InlineData("%ID100", 'I', 100, null, 4, false)]
    [InlineData("%QB4", 'Q', 4, null, 1, false)]
    [InlineData("AW8", 'Q', 8, null, 2, false)]
    public void Addresses_are_read(string text, char area, int b, int? bit, int size, bool p)
    {
        Assert.Equal(new IoAddress(area, b, bit, size, p), HoldRules.ParseAddress(text));
    }

    [Theory]
    [InlineData("%I4")]
    [InlineData("%IW4.1")]
    [InlineData("%I0.8")]
    [InlineData("\"DB\".x")]
    public void Not_addresses(string text) => Assert.Null(HoldRules.ParseAddress(text));

    [Theory]
    [InlineData("Bool", "TRUE", "01")]
    [InlineData("Int", "-2", "FFFE")]
    [InlineData("Word", "16#1234", "1234")]
    [InlineData("DInt", "70000", "00011170")]
    [InlineData("Real", "1.5", "3FC00000")]
    public void Values_are_stored_big_endian_and_read_back(string type, string text, string hex)
    {
        var bytes = HoldRules.Encode(WriteRules.Parse(text, HoldRules.ClrType(type)), type);
        Assert.Equal(hex, Convert.ToHexString(bytes));
        var shown = HoldRules.Decode(bytes, type);
        Assert.Equal(WriteRules.Show(WriteRules.Parse(text, HoldRules.ClrType(type))), shown);
    }
}
