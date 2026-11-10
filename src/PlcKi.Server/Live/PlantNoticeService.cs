using PlcKi.Core.Import;
using PlcKi.Core.Model;
using PlcKi.Core.Status;
using PlcKi.Server.Jobs;

namespace PlcKi.Server.Live;

/// <summary>
/// The notices on every page (review 43): test switches that are on, and blocks in the PLC that differ from the
/// imported program. The polling loop reads the switches (every 2 s) and the PLC's block list (after a connect, then
/// every 10 min), read-only; /api/status hands the notices to the pages. Forces set in TIA are not here: the S7 driver
/// cannot read the CPU's force list.
/// </summary>
public sealed class PlantNoticeService
{
    private readonly ProgramService _program;
    private readonly ILogger<PlantNoticeService> _log;
    private readonly object _lock = new();
    private (ExportPlc? Plc, TestSwitchSet Set, List<TagDef> Defs)? _cache;
    private volatile Dictionary<string, (object? Value, string? Error)> _values = new();
    private volatile PlantNotice? _drift;

    public PlantNoticeService(ProgramService program, ILogger<PlantNoticeService> log)
    {
        _program = program;
        _log = log;
    }

    private ExportPlc? Plc()
    {
        try { return _program.Loaded.Plc; }
        catch (Exception) { return null; }   // no program yet: nothing to check against
    }

    /// <summary>The test switches of the loaded program, worked out again when the program changes.</summary>
    public (TestSwitchSet Set, List<TagDef> Defs) Switches()
    {
        var plc = Plc();
        lock (_lock)
        {
            if (_cache is { } c && ReferenceEquals(c.Plc, plc)) return (c.Set, c.Defs);
            var set = TestSwitches.Find(plc);
            var defs = set.Switches.Select(s => new TagDef { Id = s.Symbol, Symbol = s.Symbol, Type = "Bool" }).ToList();
            _cache = (plc, set, defs);
            _values = new();
            if (set.Switches.Count > 0)
                _log.LogInformation("Watching {Count} test switches in {Blocks}{Skipped}.", set.Switches.Count, string.Join(", ", set.Blocks),
                    set.Skipped.Count > 0 ? $" (not read, over the limit of {TestSwitches.MaxBlocks} blocks / {TestSwitches.MaxSwitches} switches: {string.Join(", ", set.Skipped)})" : "");
            return (set, defs);
        }
    }

    public void PublishSwitches(Dictionary<string, (object? Value, string? Error)> values) => _values = values;

    /// <summary>The PLC's block list as just read; null when the source cannot list blocks (then no notice).</summary>
    public void PublishBlocks(IReadOnlyList<PlcBlock>? onPlc)
    {
        ProgramExport? export = null;
        try { export = _program.Loaded.Export; } catch (Exception) { /* no program yet */ }
        var plc = Plc();
        if (onPlc == null || plc == null || plc.Blocks.Count == 0) { _drift = null; return; }
        var (added, missing) = ProgramDrift.Compare(onPlc, plc, ProgramDrift.NotExported(export));
        var was = _drift;
        _drift = ProgramDrift.Notice(added, missing);
        if (_drift != null && was?.Text != _drift.Text)
            _log.LogInformation("The PLC's blocks differ from the imported program: {Items}.", string.Join("; ", _drift.Items));
    }

    /// <summary>Not connected: what was read is no longer true.</summary>
    public void Clear() { _values = new(); _drift = null; }

    /// <summary>The notices now, or null when there are none.</summary>
    public List<PlantNotice>? Current()
    {
        var list = new List<PlantNotice>();
        if (TestSwitches.Notice(Switches().Set, _values) is { } t) list.Add(t);
        if (_drift is { } d) list.Add(d);
        return list.Count > 0 ? list : null;
    }
}
