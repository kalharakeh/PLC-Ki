using PlcKi.Core.Diagnostics;
using PlcKi.Core.Hardware;
using PlcKi.Core.Import;
using Xunit;

/// <summary>Drive and PROFINET diagnostics: telegrams, status words, drive blocks, and the CPU's system alarms per station.</summary>
public class DiagnosticsTests
{
    // A PLC, an ET 200SP station and a G120 with SIEMENS telegram 352 (the telegram is a GSD submodule next to the head)
    private const string Hw = """
    {
      "format": "plcki-hardware-1", "project": "Test",
      "devices": [
        { "name": "PLC_1", "items": [ { "name": "Rail_0", "typeIdentifier": "System:Rack.S71500", "items": [
            { "name": "PLC_1", "position": 1, "orderNumber": "6ES7 515-2FM02-0AB0", "typeIdentifier": "OrderNumber:6ES7 515-2FM02-0AB0/V2.8",
              "items": [ { "name": "PROFINET interface_1", "nodes": [ { "address": "192.168.5.110" } ], "controlsIoSystem": "PROFINET IO-System (100)" } ] } ] } ] },
        { "name": "ET200SP_Leak", "items": [ { "name": "Rack_0", "typeIdentifier": "System:Rack.ET200SP", "items": [
            { "name": "IM 155-6 PN ST", "position": 0, "orderNumber": "6ES7 155-6AU01-0BN0",
              "items": [ { "name": "PROFINET interface_1", "nodes": [ { "address": "192.168.5.120", "pnName": "et200sp-leak" } ], "ioSystem": "PROFINET IO-System (100)" } ] },
            { "name": "DI 8x24VDC ST_1", "position": 1, "orderNumber": "6ES7 131-6BF01-0BA0", "addresses": [ { "io": "Input", "start": 0, "length": 1 } ] } ] } ] },
        { "name": "G120_Conveyor", "typeName": "SINAMICS G120 CU250S-2 PN", "typeIdentifier": "GSD:GSDML-V2.35-SINAMICSG120CU250S-2PN.XML/DAP",
          "items": [
            { "name": "G120_Conveyor", "position": 0, "orderNumber": "6SL3246-0BA22-1FA0", "typeName": "SINAMICS G120 CU250S-2 PN",
              "items": [ { "name": "PROFINET interface_1", "nodes": [ { "address": "192.168.5.150", "pnName": "g120-conveyor" } ], "ioSystem": "PROFINET IO-System (100)" } ] },
            { "name": "Drive_object_1", "position": 1, "typeIdentifier": "GSD:GSDML-V2.35-SINAMICSG120CU250S-2PN.XML/DO",
              "items": [ { "name": "SIEMENS telegram 352, PZD-6/6_1", "typeName": "SIEMENS telegram 352, PZD-6/6", "position": 1,
                "addresses": [ { "io": "Input", "start": 256, "length": 12 }, { "io": "Output", "start": 256, "length": 12 } ] } ] }
          ] }
      ]
    }
    """;

    private static readonly ExportPlcTag[] Tags =
    {
        new("Conv_ZSW1", "Word", "%IW256", "Conveyor status word", "Drives"),
        new("Conv_Speed", "Int", "%IW258", "Conveyor speed", "Drives"),
        new("Conv_Fault", "Word", "%IW266", "Conveyor fault number", "Drives"),
        new("Conv_Warn", "Word", "%IW264", "Conveyor warning number", "Drives"),
    };

    private static HardwareConfig Load() => HardwareConfig.Load(Hw, Tags);

    private static ExportPlc Plc()
    {
        var plc = new ExportPlc { Name = "PLC_1" };
        plc.PlcTags.AddRange(Tags);
        return plc;
    }

    [Fact]
    public void Finds_the_telegram_of_a_drive()
    {
        var g120 = Load().Stations.Single(s => s.Name == "G120_Conveyor");
        var tg = g120.Modules.SelectMany(m => m.Telegrams).Single();
        Assert.Equal(352, tg.Number);
        Assert.Equal(256, tg.InStart);
        Assert.Equal(6, tg.InWords);
    }

    [Fact]
    public void Reads_the_drive_through_the_tags_on_its_telegram()
    {
        var drive = Assert.Single(DeviceDiagnostics.FindDrives(Load(), Plc()));
        Assert.Equal("G120_Conveyor", drive.Station);
        Assert.Equal(new[] { "ZSW1", "NIST_A_GLATT", "WARN_CODE", "FAULT_CODE" }, drive.Signals.Select(s => s.Role));
        Assert.Equal("\"Conv_ZSW1\"", drive.Signals[0].Symbol);

        // Running at half speed
        var running = DeviceDiagnostics.State(drive, Values(("\"Conv_ZSW1\"", 0b0000_0010_0011_0111), ("\"Conv_Speed\"", 0x2000), ("\"Conv_Fault\"", 0), ("\"Conv_Warn\"", 0)), Array.Empty<DeviceProblem>());
        Assert.Equal("Running", running.State);
        Assert.Equal(50, running.SpeedPercent);
        Assert.Null(running.Fault);

        // Fault F07900 (motor blocked): the knowledge base explains it
        var fault = DeviceDiagnostics.State(drive, Values(("\"Conv_ZSW1\"", 0b0000_0010_0011_1000), ("\"Conv_Speed\"", 0), ("\"Conv_Fault\"", 7900), ("\"Conv_Warn\"", 0)), Array.Empty<DeviceProblem>());
        Assert.Equal("Fault", fault.State);
        Assert.Equal("F07900", fault.Fault);
        Assert.Equal("drive-f07900-motor-blocked", fault.Knowledge?.Id);
    }

    [Fact]
    public void Says_why_switching_on_is_inhibited()
    {
        var (state, text, _) = Telegrams.DecodeZsw1(0b0000_0010_0010_0000 | (1 << 6));   // bit 6 on, bit 4 (OFF2) off
        Assert.Equal("Inhibited", state);
        Assert.Contains("OFF2", text);
        var (_, _, problems) = Telegrams.DecodeZsw1(0b0000_0000_0011_0111);                // control requested (bit 9) off
        Assert.Contains(problems, p => p.Contains("does NOT have control"));
    }

    [Fact]
    public void A_drive_without_tags_says_why_it_cannot_be_read()
    {
        var drive = Assert.Single(DeviceDiagnostics.FindDrives(Load(), new ExportPlc { Name = "PLC_1" }));
        Assert.Empty(drive.Signals);
        Assert.Contains(drive.Notes, n => n.Contains("No PLC tag"));
    }

    [Fact]
    public void Finds_drive_blocks_and_ties_them_to_the_drive_by_name()
    {
        var plc = new ExportPlc { Name = "PLC_1" };
        plc.Blocks.Add(new ExportBlock { Name = "Conveyor_SinaSpeed_DB", Kind = "InstanceDB", InstanceOf = "SINA_SPEED" });
        var station = new ExportBlock { Name = "Station_DB", Kind = "InstanceDB", InstanceOf = "FB_Station" };
        station.Interface.Add(new ExportMember { Name = "Lift", Section = "Static", Datatype = "\"SINA_POS\"" });
        plc.Blocks.Add(station);

        var drives = DeviceDiagnostics.FindDrives(Load(), plc);
        var conveyor = drives.Single(d => d.Station == "G120_Conveyor");
        Assert.Equal("\"Conveyor_SinaSpeed_DB\"", conveyor.Block);
        Assert.Contains(conveyor.Signals, s => s.Symbol == "\"Conveyor_SinaSpeed_DB\".Error");
        var lift = drives.Single(d => d.Block == "\"Station_DB\".Lift");
        Assert.Equal("SINA_POS", lift.BlockType);
        Assert.Contains(lift.Signals, s => s.Symbol == "\"Station_DB\".Lift.ActFault" && s.Role == "faultCode");

        var state = DeviceDiagnostics.State(lift, Values(("\"Station_DB\".Lift.AxisError", true), ("\"Station_DB\".Lift.ActFault", 7901)), Array.Empty<DeviceProblem>());
        Assert.Equal("Fault", state.State);
        Assert.Equal("F07901", state.Fault);
    }

    [Fact]
    public void Ties_the_CPU_system_diagnostics_to_stations_and_modules()
    {
        var alarms = new[]
        {
            new CpuAlarm(1, "IO device failure", null, new[] { "PROFINET IO-System (100)", "ET200SP_Leak" }, null, 1, 0, null, null),
            new CpuAlarm(2, "Wire break", null, new[] { "ET200SP_Leak", "DI 8x24VDC ST_1", "Channel 3" }, null, 1, 0, null, null),
            new CpuAlarm(3, "Fault F30001: Power unit overcurrent", null, new[] { "g120-conveyor" }, null, 1, 0, null, null),
            new CpuAlarm(4, "Tank level too high", null, Array.Empty<string>(), "Alarms", 256, 0, null, null),   // a program alarm: not ours
        };
        var problems = DeviceDiagnostics.Problems(alarms, Load());
        Assert.Equal(3, problems.Count);

        var failure = problems.Single(p => p.Id == 1);
        Assert.Equal(("ET200SP_Leak", "fault", "profinet"), (failure.Station, failure.Severity, failure.Kind));
        Assert.Equal("pn-device-failure", failure.Knowledge?.Id);

        var wire = problems.Single(p => p.Id == 2);
        Assert.Equal(("ET200SP_Leak", "DI 8x24VDC ST_1", 1), (wire.Station, wire.Module, wire.Slot));
        Assert.Equal("channel", wire.Kind);

        var drive = problems.Single(p => p.Id == 3);
        Assert.Equal("G120_Conveyor", drive.Station);   // found by its PROFINET name
        Assert.Equal(new[] { "F30001" }, drive.Codes);
        Assert.Equal("drive", drive.Kind);

        // The drive picks the reported fault up even without a tag on its telegram
        var d = Assert.Single(DeviceDiagnostics.FindDrives(Load(), new ExportPlc { Name = "PLC_1" }));
        var st = DeviceDiagnostics.State(d, Values(), problems.Where(p => p.Station == d.Station));
        Assert.Equal("Fault", st.State);
        Assert.Equal("F30001", st.Fault);
    }

    private static Dictionary<string, (object? Value, string? Error)> Values(params (string Symbol, object Value)[] v) =>
        v.ToDictionary(x => x.Symbol, x => ((object?)x.Value, (string?)null));
}
