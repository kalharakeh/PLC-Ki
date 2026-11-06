using PlcKi.Core.Hardware;
using PlcKi.Core.Import;
using PlcKi.Core.Status;
using Xunit;

/// <summary>
/// The rule table for "Change a value" and "Hold on PLCSIM" (ChangeRules), one test per row:
/// confirmed PLCSIM, an approved real PLC, a real PLC not on the list, and a PLCSIM PLC-Ki cannot confirm.
/// </summary>
public class ChangeRulesTests
{
    private const string NotConfirmed = "it finds no running PLCSIM instance at 192.168.10.10";

    /// <summary>Like Helium Leak Test after the refresh: E-stop and door as F-DI inputs, and copies in M900 the safety program reads.</summary>
    private static ExportPlc Plant(bool safety = true)
    {
        var plc = new ExportPlc { Name = "PLC_1" };
        if (safety)
        {
            var fMain = new ExportBlock { Name = "F_Main", Kind = "FB", Language = "F_LAD" };
            foreach (var n in new[] { "FDI_ES1", "M_ES_OK", "DI_Ack", "FDQ_Valve", "DB_SafetyBits" }) fMain.GlobalNames.Add(n);
            plc.Blocks.Add(fMain);
            plc.Blocks.Add(new ExportBlock { Name = "F_Main_DB", Kind = "InstanceDB", Language = "DB", InstanceOf = "F_Main" });
            plc.Blocks.Add(new ExportBlock { Name = "F_GlobDB", Kind = "GlobalDB", Language = "F_DB" });
            plc.Blocks.Add(new ExportBlock { Name = "RTG1SysInfo", Kind = "GlobalDB", Language = "DB" });
        }
        plc.Blocks.Add(new ExportBlock { Name = "DB_SafetyBits", Kind = "GlobalDB", Language = "DB" });
        plc.Blocks.Add(new ExportBlock { Name = "DB_Station", Kind = "GlobalDB", Language = "DB" });
        plc.PlcTags.Add(new ExportPlcTag("DI_BluePB", "Bool", "%I0.3", null, "Inputs"));
        plc.PlcTags.Add(new ExportPlcTag("DI_Ack", "Bool", "%I1.0", null, "Inputs"));          // standard input the safety program reads
        plc.PlcTags.Add(new ExportPlcTag("FDI_ES1", "Bool", "%I8.3", null, "Safety"));          // on the F-DI
        plc.PlcTags.Add(new ExportPlcTag("FDI_Spare", "Bool", "%I9.0", null, "Safety"));        // on the F-DI, not used
        plc.PlcTags.Add(new ExportPlcTag("DO_Lamp", "Bool", "%Q1.0", null, "Outputs"));
        plc.PlcTags.Add(new ExportPlcTag("FDQ_Valve", "Bool", "%Q20.0", null, "Safety"));       // on the F-DQ
        plc.PlcTags.Add(new ExportPlcTag("M_ES_OK", "Bool", "%M900.0", null, "Safety"));        // E-stop copy the safety program reads
        plc.PlcTags.Add(new ExportPlcTag("M_Plain", "Bool", "%M10.0", null, "Sim"));
        plc.PlcTags.Add(new ExportPlcTag("Sim_Speed", "Int", "%MW12", null, "Sim"));
        return plc;
    }

    private static HardwareConfig Hardware()
    {
        var hw = new HardwareConfig();
        var st = new HwStation { Name = "ET200SP_1", Kind = "io" };
        var di = new HwModule { Name = "DI 16", Title = "DI 16x24VDC ST", Kind = "di" };
        di.Addresses.Add(new HwAddress("Input", 0, 2));
        var dq = new HwModule { Name = "DQ 16", Title = "DQ 16x24VDC ST", Kind = "dq" };
        dq.Addresses.Add(new HwAddress("Output", 0, 2));
        var fdi = new HwModule { Name = "F-DI", Title = "F-DI 8x24VDC HF", Kind = "f-di", Safety = true };
        fdi.Addresses.Add(new HwAddress("Input", 8, 6));
        var fdq = new HwModule { Name = "F-DQ", Title = "F-DQ 4x24VDC PM", Kind = "f-dq", Safety = true };
        fdq.Addresses.Add(new HwAddress("Output", 20, 5));
        foreach (var m in new[] { di, dq, fdi, fdq }) st.Modules.Add(m);
        hw.Stations.Add(st);
        return hw;
    }

    private static ChangeDecision Sim(string symbol, ChangeKind kind = ChangeKind.Change, ExportPlc? plc = null, bool hardware = true) =>
        ChangeRules.Decide(plc ?? Plant(), hardware ? Hardware() : null, symbol, kind, ChangeTarget.Plcsim);

    private static ChangeDecision Real(string symbol, ChangeTarget target = ChangeTarget.RealAllowed, string? notConfirmed = null,
        ChangeKind kind = ChangeKind.Change) =>
        ChangeRules.Decide(Plant(), Hardware(), symbol, kind, target, notConfirmed);

    private static void Allowed(ChangeDecision d, string rule, string? warning)
    {
        Assert.Null(d.Refused);
        Assert.Equal(rule, d.Rule);
        Assert.Equal(warning, d.Warning);
    }

    // ---- Confirmed PLCSIM ----

    [Theory]
    [InlineData(ChangeKind.Change)]
    [InlineData(ChangeKind.Hold)]
    public void Plcsim_standard_input_change_and_hold(ChangeKind kind) => Allowed(Sim("\"DI_BluePB\"", kind), "plcsim-input", null);

    [Theory]
    [InlineData("\"FDI_ES1\"", ChangeKind.Change)]      // on the F-DI and read by the safety program: the F-input warning wins
    [InlineData("\"FDI_ES1\"", ChangeKind.Hold)]
    [InlineData("\"FDI_Spare\"", ChangeKind.Change)]    // on the F-DI only
    [InlineData("\"FDI_Spare\"", ChangeKind.Hold)]
    public void Plcsim_f_di_input_is_allowed_with_the_f_input_warning(string symbol, ChangeKind kind) =>
        Allowed(Sim(symbol, kind), "plcsim-safety-input", ChangeRules.FInput);

    [Fact]
    public void Plcsim_standard_input_the_safety_program_reads_warns() =>
        Allowed(Sim("\"DI_Ack\""), "plcsim-safety-read", ChangeRules.SafetyReads);

    [Theory]
    [InlineData("\"DI_BluePB\"")]
    [InlineData("\"FDI_ES1\"")]
    public void Plcsim_input_without_hardware_export_warns_that_it_cannot_tell(string symbol) =>
        Allowed(Sim(symbol, hardware: false), "plcsim-input-unchecked", ChangeRules.Unchecked("input"));

    [Fact]
    public void Plcsim_input_without_hardware_and_without_safety_program_needs_no_warning() =>
        Allowed(Sim("\"DI_BluePB\"", plc: Plant(safety: false), hardware: false), "plcsim-input", null);

    [Fact]
    public void Plcsim_output_change_once_is_allowed() => Allowed(Sim("\"DO_Lamp\""), "plcsim-output", null);

    [Fact]
    public void Plcsim_f_dq_output_change_once_is_allowed_with_the_f_output_warning() =>
        Allowed(Sim("\"FDQ_Valve\""), "plcsim-safety-output", ChangeRules.FOutput);

    [Theory]
    [InlineData("\"DO_Lamp\"")]
    [InlineData("\"FDQ_Valve\"")]
    public void Plcsim_output_cannot_be_held_and_says_why(string symbol)
    {
        var why = Sim(symbol, ChangeKind.Hold).Refused;
        Assert.Contains("is an output", why);
        Assert.Contains("PLCSIM only keeps inputs", why);
    }

    [Theory]
    [InlineData("\"M_Plain\"", "plcsim-memory")]
    [InlineData("\"Sim_Speed\"", "plcsim-memory")]
    [InlineData("\"DB_Station\".Speed", "plcsim-data")]
    public void Plcsim_standard_memory_and_data_may_be_changed(string symbol, string rule) => Allowed(Sim(symbol), rule, null);

    [Theory]
    [InlineData("\"M_ES_OK\"")]                 // the M900 E-stop bit
    [InlineData("\"DB_SafetyBits\".Door")]      // a member of a standard DB the safety program uses
    public void Plcsim_value_the_safety_program_reads_may_be_changed_with_the_warning(string symbol) =>
        Allowed(Sim(symbol), "plcsim-safety-read", ChangeRules.SafetyReads);

    [Theory]
    [InlineData("\"M_Plain\"", "memory value")]
    [InlineData("\"DB_Station\".Speed", "data block value")]
    public void Plcsim_memory_and_data_cannot_be_held(string symbol, string why) => Assert.Contains(why, Sim(symbol, ChangeKind.Hold).Refused);

    // ---- Never: F-blocks and their data, on every target ----

    [Theory]
    [InlineData("\"F_GlobDB\".Ack")]
    [InlineData("\"F_Main_DB\".x")]
    [InlineData("\"RTG1SysInfo\".MODE")]
    [InlineData("\"F_Main\"")]
    public void F_blocks_and_their_data_are_refused_everywhere(string symbol)
    {
        Assert.NotNull(Sim(symbol).Refused);
        Assert.NotNull(Sim(symbol, ChangeKind.Hold).Refused);
        Assert.NotNull(Real(symbol).Refused);
        var unconfirmed = Real(symbol, notConfirmed: NotConfirmed).Refused;
        Assert.NotNull(unconfirmed);
        Assert.DoesNotContain("can't confirm", unconfirmed);   // PLCSIM would refuse it too, so that is not the reason
    }

    [Fact]
    public void Without_a_program_nothing_is_changed()
    {
        Assert.NotNull(ChangeRules.Decide(null, Hardware(), "\"M_Plain\"", ChangeKind.Change, ChangeTarget.Plcsim).Refused);
        Assert.NotNull(ChangeRules.Decide(null, Hardware(), "\"M_Plain\"", ChangeKind.Change, ChangeTarget.RealAllowed).Refused);
    }

    // ---- Real PLC on the allowed list ----

    [Theory]
    [InlineData("\"M_Plain\"", "real-memory")]
    [InlineData("\"Sim_Speed\"", "real-memory")]
    [InlineData("\"DB_Station\".Speed", "real-data")]
    public void Real_standard_memory_and_data_may_be_changed(string symbol, string rule) => Allowed(Real(symbol), rule, null);

    [Theory]
    [InlineData("\"M_ES_OK\"")]
    [InlineData("\"DB_SafetyBits\".Door")]
    [InlineData("\"DI_Ack\"")]
    [InlineData("\"FDI_ES1\"")]
    [InlineData("\"FDQ_Valve\"")]
    public void Real_value_the_safety_program_reads_is_refused(string symbol) =>
        Assert.Equal(ChangeRules.SafetyReadsReal, Real(symbol).Refused);

    [Theory]
    [InlineData("\"DI_BluePB\"", "is an input")]
    [InlineData("\"FDI_Spare\"", "is an input")]
    [InlineData("\"DO_Lamp\"", "is an output")]
    public void Real_inputs_and_outputs_are_refused(string symbol, string why) => Assert.Contains(why, Real(symbol).Refused);

    [Fact]
    public void Real_nothing_is_held() => Assert.Contains("PLCSIM only", Real("\"DI_BluePB\"", kind: ChangeKind.Hold).Refused);

    [Fact]
    public void Real_plc_not_on_the_list_changes_nothing() =>
        Assert.Contains("not on the list", Real("\"M_Plain\"", ChangeTarget.RealNotAllowed).Refused);

    // ---- PLCSIM PLC-Ki cannot confirm: the real-PLC rules ----

    [Fact]
    public void Unconfirmed_plcsim_refuses_an_f_di_input_and_says_why()
    {
        var why = Real("\"FDI_ES1\"", notConfirmed: NotConfirmed).Refused;
        Assert.StartsWith("PLC-Ki can't confirm this is PLCSIM (TIA helper: it finds no running PLCSIM instance at 192.168.10.10).", why);
        Assert.Contains(ChangeRules.SafetyReadsReal, why);
    }

    [Fact]
    public void Unconfirmed_plcsim_refuses_the_m900_bit() =>
        Assert.StartsWith("PLC-Ki can't confirm this is PLCSIM", Real("\"M_ES_OK\"", notConfirmed: NotConfirmed).Refused);

    [Fact]
    public void Unconfirmed_plcsim_not_on_the_list_refuses_and_says_why() =>
        Assert.StartsWith("PLC-Ki can't confirm this is PLCSIM", Real("\"M_Plain\"", ChangeTarget.RealNotAllowed, NotConfirmed).Refused);

    [Fact]
    public void Unconfirmed_plcsim_on_the_list_still_allows_plain_memory() =>
        Allowed(Real("\"M_Plain\"", notConfirmed: NotConfirmed), "real-memory", null);

    [Fact]
    public void Unconfirmed_plcsim_cannot_hold() =>
        Assert.StartsWith("PLC-Ki can't confirm this is PLCSIM", Real("\"DI_BluePB\"", ChangeTarget.RealNotAllowed, NotConfirmed, ChangeKind.Hold).Refused);

    // ---- When is PLCSIM confirmed? ----

    private const string Ip = "192.168.10.10";

    [Fact]
    public void Other_address_is_not_plcsim() =>
        Assert.Equal("its PLCSIM CPU is at 192.168.10.10, not 192.168.0.5",
            ChangeRules.PlcsimNotConfirmed("192.168.0.5", false, false, null, "its PLCSIM CPU is at 192.168.10.10, not 192.168.0.5", null, null, null));

    [Fact]
    public void Helper_down_is_not_plcsim() =>
        Assert.Contains("does not answer", ChangeRules.PlcsimNotConfirmed(Ip, false, false, null, "it does not answer: timeout", null, null, null));

    [Fact]
    public void Same_address_but_no_running_instance_is_not_confirmed() =>
        Assert.Contains("no running PLCSIM instance", ChangeRules.PlcsimNotConfirmed(Ip, true, false, null, null, null, "RUN", null));

    [Fact]
    public void Slow_plcsim_is_not_confirmed() =>
        Assert.Contains("could not read PLCSIM just now: timeout", ChangeRules.PlcsimNotConfirmed(Ip, true, false, null, null, "timeout", null, null));

    [Fact]
    public void A_plcsim_error_is_not_confirmed() =>
        Assert.Contains("PLCSIM problem", ChangeRules.PlcsimNotConfirmed(Ip, true, true, "Helium_sim", null, "license", null, null));

    [Fact]
    public void Run_stop_mismatch_means_maybe_not_the_same_cpu() =>
        Assert.Contains("may not be the same CPU", ChangeRules.PlcsimNotConfirmed(Ip, true, true, "Helium_sim", null, null, "RUN", "Stop"));

    [Theory]
    [InlineData("STOP", "Stop")]
    [InlineData(null, "Run")]
    [InlineData("Startup", "Run")]
    public void Confirmed_plcsim(string? plcState, string simState) =>
        Assert.Null(ChangeRules.PlcsimNotConfirmed(Ip, true, true, "Helium_sim", null, null, plcState, simState));
}
