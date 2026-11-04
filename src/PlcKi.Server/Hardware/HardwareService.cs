using System.IO.Compression;
using System.Text.Json;
using PlcKi.Core.Hardware;
using PlcKi.Server.Jobs;
using PlcKi.Server.Live;

namespace PlcKi.Server.Hardware;

/// <summary>
/// The hardware configuration for the Hardware and PLC pages. It comes from hardware.json, next to the program
/// export ("&lt;export&gt;.hardware.json", written by "Read the hardware from TIA") or inside it (a full export
/// with a TIA helper that knows hardware). Without either, only the CPU from the export's manifest is known.
/// Read again when the file changes, so a new hardware read shows without a restart.
/// </summary>
public sealed class HardwareService
{
    private readonly PlcKiOptions _opt;
    private readonly ProgramService _program;
    private readonly Func<string, string> _resolve;
    private readonly object _lock = new();
    private (string Key, HardwareConfig Hw, string Source)? _cache;

    public HardwareService(PlcKiOptions opt, ProgramService program, Func<string, string> resolve)
    {
        _opt = opt;
        _program = program;
        _resolve = resolve;
    }

    /// <summary>The program export file or folder PLC-Ki uses, or null.</summary>
    public string? ExportPath => string.IsNullOrWhiteSpace(_opt.Export) ? null : _resolve(_opt.Export.Trim());

    /// <summary>Where "Read the hardware from TIA" writes: next to the export zip.</summary>
    public string? SidecarPath => ExportPath is { } p
        ? (Directory.Exists(p) ? Path.Combine(p, "hardware.json") : Path.ChangeExtension(p, null) + ".hardware.json")
        : null;

    public (HardwareConfig? Hw, string Source, string? Error) Load()
    {
        var export = ExportPath;
        if (export == null) return (null, "none", "No program is loaded yet. Import the program first (Setup › Program).");
        try
        {
            var sidecar = SidecarPath!;
            var sideTime = File.Exists(sidecar) ? File.GetLastWriteTimeUtc(sidecar) : DateTime.MinValue;
            var exportTime = File.Exists(export) ? File.GetLastWriteTimeUtc(export) : Directory.Exists(export) ? Directory.GetLastWriteTimeUtc(export) : DateTime.MinValue;
            var key = $"{export}|{exportTime.Ticks}|{sideTime.Ticks}";
            lock (_lock)
                if (_cache is { } c && c.Key == key) return (c.Hw, c.Source, null);

            var (_, plc, _) = _program.Loaded;
            var tags = plc?.PlcTags;
            HardwareConfig? hw = null;
            var source = "program";
            // The newer of the two wins: a quick hardware read after the export, or a full export after an old read
            var inside = InsideExport(export);
            if (sideTime > DateTime.MinValue && (inside == null || sideTime >= exportTime))
            { hw = HardwareConfig.Load(File.ReadAllText(sidecar), tags); source = "hardware"; }
            else if (inside != null)
            { hw = HardwareConfig.Load(inside, tags); source = "hardware"; }
            else if (_program.Loaded.Export is { } ex)
                hw = HardwareConfig.FromProgram(ex);
            if (hw == null) return (null, "none", _program.Loaded.Error ?? "The program export has no PLC.");
            lock (_lock) _cache = (key, hw, source);
            return (hw, source, null);
        }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidDataException or UnauthorizedAccessException)
        {
            return (null, "none", "Cannot read the hardware configuration: " + ex.Message);
        }
    }

    /// <summary>hardware.json inside the export folder or zip, or null.</summary>
    private static string? InsideExport(string export)
    {
        if (Directory.Exists(export))
        {
            var f = Directory.GetFiles(export, "hardware.json", SearchOption.AllDirectories).OrderBy(p => p.Length).FirstOrDefault();
            return f == null ? null : File.ReadAllText(f);
        }
        if (!File.Exists(export) || !export.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) return null;
        using var zip = ZipFile.OpenRead(export);
        var e = zip.Entries.Where(x => x.Name.Equals("hardware.json", StringComparison.OrdinalIgnoreCase)).OrderBy(x => x.FullName.Length).FirstOrDefault();
        if (e == null) return null;
        using var r = new StreamReader(e.Open());
        return r.ReadToEnd();
    }

    /// <summary>The station of the PLC PLC-Ki reads: the one with the PLC's address, else the first PLC.</summary>
    public static HwStation? PlcStation(HardwareConfig hw, string? address) =>
        hw.Stations.FirstOrDefault(s => s.Kind == "plc" && !string.IsNullOrEmpty(address) && s.Ips.Contains(address))
        ?? hw.Stations.FirstOrDefault(s => s.Kind == "plc");

    public object View()
    {
        var (hw, source, error) = Load();
        if (hw == null) return new { available = false, error };
        var plc = PlcStation(hw, _opt.Plc.Address);
        return new
        {
            available = true,
            source,          // hardware = full configuration; program = only the CPU from the program export
            project = hw.Project,
            exportedAt = hw.ExportedAt,
            plcStation = plc?.Name,
            stations = hw.Stations.Select(s => new
            {
                s.Name, s.Kind, s.Family, s.TypeName, s.OrderNumber, s.Comment, s.Ips, s.PnName, s.IoSystem, s.ControlsIoSystem,
                modules = s.Modules.Select(m => new
                {
                    m.Slot, m.Name, m.Title, m.OrderNumber, m.Firmware, m.Family, m.Kind, m.WidthMm, m.Safety, m.Display, m.Comment, m.Ips, m.Ports,
                    addresses = m.Addresses.Select(a => new { a.Io, a.Start, a.Bytes, a.Text }),
                    channels = m.Channels.Select(c => new
                    {
                        n = c.Number, io = c.Io, type = c.Type, address = c.Address, tag = c.Tag,
                        symbol = c.Tag == null ? null : $"\"{c.Tag}\"", datatype = c.Datatype, modelType = c.Datatype == null ? null : PlcKi.Core.Import.ProgramExport.ModelType(c.Datatype), comment = c.Comment, table = c.Table,
                    }),
                }),
            }),
        };
    }
}
