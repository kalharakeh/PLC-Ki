using PlcKi.Core.Hardware;
using PlcKi.Core.Import;
using Xunit;

/// <summary>The hardware configuration: modules from order numbers, channel addresses, PLC tags by address.</summary>
public class HardwareTests
{
    private static readonly string Sample = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "data", "hardware-sample.json"));

    private static readonly ExportPlcTag[] Tags =
    {
        new("FDI_ES1", "Bool", "%I10.0", "E-stop 1", "Safety"),
        new("DI_DoorClosed", "Bool", "%I 0.1", "Door closed", "IO"),
        new("DQ_Lamp", "Bool", "%Q3.7", "Lamp", "IO"),
        new("AI_Pressure", "Int", "%IW258", "Chamber pressure", "IO"),
        new("Leak_Valve", "Bool", "%Q20.2", null, "IO"),
    };

    [Fact]
    public void Reads_the_stations_and_racks()
    {
        var hw = HardwareConfig.Load(Sample, Tags);
        Assert.Equal(new[] { "plc", "io", "other", "other", "other" }, hw.Stations.Select(s => s.Kind));
        var plc = hw.Stations[0];
        Assert.Equal("PLC_1", plc.Name);
        Assert.Equal("s7-1500", plc.Family);
        Assert.Equal(9, plc.Modules.Count);
        Assert.Equal(Enumerable.Range(0, 9).Select(i => (int?)i), plc.Modules.Select(m => m.Slot));
        var cpu = plc.Modules.Single(m => m.Kind == "cpu");
        Assert.Equal("CPU 1515F-2 PN", cpu.Title);
        Assert.Equal("6ES7 515-2FM02-0AB0", cpu.OrderNumber);
        Assert.Equal("V2.8", cpu.Firmware);
        Assert.True(cpu.Safety && cpu.Display);
        Assert.Equal(70, cpu.WidthMm);
        Assert.Contains("192.168.5.110", plc.Ips);
        Assert.Equal("PROFINET IO-System (100)", plc.ControlsIoSystem);

        var sp = hw.Stations[1];
        Assert.Equal("et200sp", sp.Family);
        Assert.Equal("PROFINET IO-System (100)", sp.IoSystem);
        Assert.Equal("et200sp-leak", sp.PnName);
        Assert.Equal("im", sp.Modules[0].Kind);
    }

    [Fact]
    public void Channels_get_addresses_and_tags()
    {
        var hw = HardwareConfig.Load(Sample, Tags);
        var plc = hw.Stations[0];
        var di = plc.Modules.Single(m => m.Slot == 2);
        Assert.Equal(32, di.Channels.Count);
        Assert.Equal("I 0...3", di.Addresses.Single().Text);          // length 32 was bits
        Assert.Equal("%I0.0", di.Channels[0].Address);
        Assert.Equal("%I3.7", di.Channels[31].Address);
        Assert.Equal("DI_DoorClosed", di.Channels[1].Tag);             // "%I 0.1" in the tag table

        var dq = plc.Modules.Single(m => m.Slot == 4);
        Assert.Equal("DQ_Lamp", dq.Channels.Single(c => c.Address == "%Q3.7").Tag);

        var fdi = plc.Modules.Single(m => m.Kind == "f-di");
        Assert.Equal("FDI_ES1", fdi.Channels[0].Tag);
        Assert.Equal("E-stop 1", fdi.Channels[0].Comment);

        var ai = plc.Modules.Single(m => m.Kind == "ai");
        Assert.Equal("I 256...271", ai.Addresses.Single().Text);       // length 16 was bytes
        Assert.Equal(new[] { "%IW256", "%IW258" }, ai.Channels.Take(2).Select(c => c.Address));
        Assert.Equal("AI_Pressure", ai.Channels[1].Tag);

        var spDq = hw.Stations[1].Modules.Single(m => m.Kind == "dq");
        Assert.Equal("Leak_Valve", spDq.Channels[2].Tag);
    }

    [Theory]
    [InlineData("OrderNumber:6ES7 515-2FM02-0AB0/V2.8", "6ES7 515-2FM02-0AB0", "cpu", "s7-1500")]
    [InlineData("6ES7521-1BL00-0AB0", "6ES7 521-1BL00-0AB0", "di", "s7-1500")]
    [InlineData("6ES7 521-1BL00-0AA0", "6ES7 521-1BL00-0AA0", "di", "s7-1500")]     // other version letter: near match
    [InlineData("6ES7 522-1BH99-0AB0", "6ES7 522-1BH99-0AB0", "dq", "s7-1500")]     // unknown: from the pattern
    [InlineData("6ES7 214-1AG40-0XB0", "6ES7 214-1AG40-0XB0", "cpu", "s7-1200")]
    [InlineData("6ES7 134-6HD01-0BA1", "6ES7 134-6HD01-0BA1", "ai", "et200sp")]
    [InlineData("6ES7 136-6BA00-0CA0", "6ES7 136-6BA00-0CA0", "f-di", "et200sp")]
    public void Order_numbers_decode(string raw, string normal, string kind, string family)
    {
        Assert.Equal(normal, ModuleCatalog.Normalize(raw));
        var spec = ModuleCatalog.Lookup(raw);
        Assert.Equal(kind, spec.Kind);
        Assert.Equal(family, spec.Family);
    }

    [Fact]
    public void Unknown_modules_take_kind_and_channels_from_the_type_name()
    {
        var spec = ModuleCatalog.Lookup("6ES7 521-7XX00-0AB0", "DI 16x24VDC HS");
        Assert.Equal("di", spec.Kind);
        Assert.Equal(16, spec.Inputs);
        Assert.Equal(("aio", 4, 2), ModuleCatalog.FromName("AI 4xU/I/RTD/TC/AQ 2xU/I ST"));
    }

    [Theory]
    [InlineData(32, 4, 4)]     // bits
    [InlineData(4, 4, 4)]      // bytes
    [InlineData(128, 16, 16)]  // bits, analog 8 channels
    [InlineData(16, 16, 16)]   // bytes
    public void Address_length_in_bits_or_bytes(int length, int needed, int bytes) =>
        Assert.Equal(bytes, HardwareConfig.Bytes(length, needed));

    [Fact]
    public void Program_export_without_hardware_gives_the_cpu()
    {
        var export = ProgramExport.Load(Path.Combine(AppContext.BaseDirectory, "models", "demo-export.zip"));
        var hw = HardwareConfig.FromProgram(export);
        var st = Assert.Single(hw.Stations);
        Assert.Equal("cpu", Assert.Single(st.Modules).Kind);
    }
}
