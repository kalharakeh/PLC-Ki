using PlcKi.Core.Diagnostics;
using PlcKi.Core.Hardware;
using PlcKi.Core.Import;
using PlcKi.Core.Model;
using PlcKi.Server.Jobs;
using PlcKi.Server.Live;

namespace PlcKi.Server.Hardware;

/// <summary>
/// Drive and PROFINET diagnostics for the Hardware page, the Alarms page and Ask: the CPU's system diagnostics
/// tied to stations and modules, and each drive's state from its status word, fault code or drive block.
/// The drives are worked out again when the hardware or the program changes; the values come from the polling loop.
/// </summary>
public sealed class DiagnosticsService
{
    private readonly HardwareService _hw;
    private readonly ProgramService _program;
    private readonly DeviceWatch _watch;
    private readonly LiveState _live;
    private readonly object _lock = new();
    private (HardwareConfig? Hw, ExportPlc? Plc, List<DriveDef> Drives)? _cache;

    public DiagnosticsService(HardwareService hw, ProgramService program, DeviceWatch watch, LiveState live)
    {
        _hw = hw;
        _program = program;
        _watch = watch;
        _live = live;
    }

    /// <summary>The drives, and tells the polling loop what to read for them.</summary>
    public (HardwareConfig? Hw, List<DriveDef> Drives) Drives()
    {
        HardwareConfig? hw = null;
        ExportPlc? plc = null;
        try { hw = _hw.Load().Hw; plc = _program.Loaded.Plc; }
        catch (Exception) { /* no program yet: no drives */ }
        lock (_lock)
        {
            if (_cache is { } c && ReferenceEquals(c.Hw, hw) && ReferenceEquals(c.Plc, plc)) return (hw, c.Drives);
            var drives = DeviceDiagnostics.FindDrives(hw, plc);
            _cache = (hw, plc, drives);
            _watch.Symbols = drives.SelectMany(d => d.Signals)
                .GroupBy(s => s.Symbol).Select(g => new TagDef { Id = g.Key, Symbol = g.Key, Type = g.First().ModelType })
                .Take(200).ToList();
            return (hw, drives);
        }
    }

    public object View()
    {
        var (hw, drives) = Drives();
        var alarms = _watch.Alarms;
        var problems = alarms == null ? new List<DeviceProblem>() : DeviceDiagnostics.Problems(alarms, hw);
        var values = _watch.Values;
        var states = drives.Select(d => DeviceDiagnostics.State(d, values, problems.Where(p => p.Station != null && p.Station == d.Station))).ToList();

        // Worst severity per station and per module, for the badges on the Hardware page
        static int Rank(string s) => s switch { "fault" => 3, "warning" => 2, "maintenance" => 1, _ => 0 };
        var stations = new Dictionary<string, string>();
        void Mark(string? station, string severity)
        {
            if (station == null) return;
            if (!stations.TryGetValue(station, out var was) || Rank(severity) > Rank(was)) stations[station] = severity;
        }
        foreach (var p in problems) Mark(p.Station, p.Severity);
        foreach (var d in states)
            Mark(d.Station, d.State == "Fault" ? "fault" : d.Warning != null || d.Problems.Count > 0 ? "warning" : "ok");
        var modules = problems.Where(p => p.Station != null && p.Slot != null)
            .GroupBy(p => $"{p.Station}/{p.Slot}").ToDictionary(g => g.Key, g => g.OrderByDescending(p => Rank(p.Severity)).First().Severity);

        var status = _live.Status;
        return new
        {
            connection = status?.Connection.State,
            alarmsRead = alarms != null,
            alarmsError = _watch.AlarmsError,
            alarmsTime = _watch.AlarmsTime,
            valuesTime = _watch.ValuesTime,
            cpuAlarms = alarms?.Count ?? 0,
            // The CPU's own messages as it words them, newest first: the Active alarm page shows them when the CPU is in STOP
            messages = (alarms ?? Array.Empty<PlcKi.Core.Diagnostics.CpuAlarm>()).OrderByDescending(m => m.Since ?? DateTimeOffset.MinValue).Take(10)
                .Select(m => new { text = m.Text, info = m.Info, since = m.Since }).ToList(),
            problems,
            unplaced = problems.Count(p => p.Station == null),
            drives = states,
            stations,
            modules,
            faults = problems.Count(p => p.Severity == "fault") + states.Count(s => s.State == "Fault" && !problems.Any(p => p.Station == s.Station && p.Severity == "fault")),
        };
    }
}
