using PlcKi.Core.Memory;
using PlcKi.Core.Model;
using PlcKi.Core.Status;

namespace PlcKi.Server.Live;

/// <summary>Latest status and the recent history, shared between the polling loop, the web API and the assistant.</summary>
public sealed class LiveState
{
    private volatile MachineStatus? _status;
    private volatile Snapshot? _snapshot;

    public LiveState(MachineHistory history, LiveModelCheck check, AlarmMemory? memory = null)
    {
        History = history;
        Check = check;
        Memory = memory ?? new AlarmMemory(null);
    }

    /// <summary>Every alarm occurrence with its context and the technicians' notes, kept on disk (machine memory).</summary>
    public AlarmMemory Memory { get; }

    /// <summary>What the live machine did that the model does not explain (Setup > Program).</summary>
    public LiveModelCheck Check { get; }

    public MachineStatus? Status => _status;
    /// <summary>The CPU's mode as last read (null when the source cannot tell or never could).</summary>
    public CpuReading? Cpu { get; set; }
    /// <summary>The CPU's mode with since when, for /api/status (null when not known or not connected).</summary>
    public CpuModeTracker CpuMode { get; } = new();
    public Snapshot? Snapshot => _snapshot;
    public MachineHistory History { get; }

    /// <summary>Others that record each status (the production log for the reports). Set up at start, before polling.</summary>
    public List<Action<MachineStatus>> Listeners { get; } = new();

    public void Publish(Snapshot snap, MachineStatus status)
    {
        _snapshot = snap;
        _status = status;
        History.Add(snap, status);
        Check.Add(status);
        Memory.Observe(status);
        foreach (var l in Listeners) l(status);
    }
}

/// <summary>
/// Extra symbols the Tags page is showing, read by the polling loop next to the model tags. A symbol stays on the
/// list while a page keeps asking for it (it drops off 15 s after the last ask), and at most 300 are read.
/// </summary>
public sealed class WatchList
{
    public const int Max = 300;
    private readonly object _lock = new();
    private readonly Dictionary<string, (TagDef Def, DateTime Asked)> _wanted = new();
    private Dictionary<string, (object? Value, string? Error)> _values = new();
    public DateTimeOffset? Time { get; private set; }

    public void Ask(IEnumerable<TagDef> defs)
    {
        lock (_lock)
            foreach (var d in defs.Take(Max))
                _wanted[d.Symbol] = (d, DateTime.UtcNow);
    }

    public List<TagDef> Current()
    {
        lock (_lock)
        {
            foreach (var k in _wanted.Where(w => DateTime.UtcNow - w.Value.Asked > TimeSpan.FromSeconds(15)).Select(w => w.Key).ToList())
                _wanted.Remove(k);
            return _wanted.Values.OrderByDescending(w => w.Asked).Take(Max).Select(w => w.Def).ToList();
        }
    }

    public void Publish(Dictionary<string, (object? Value, string? Error)> values, DateTimeOffset time)
    {
        lock (_lock) { _values = values; Time = time; }
    }

    public Dictionary<string, (object? Value, string? Error)> Values(IEnumerable<string> symbols)
    {
        lock (_lock)
            return symbols.Distinct().ToDictionary(s => s, s => _values.TryGetValue(s, out var v) ? v : ((object?)null, (string?)null));
    }
}

/// <summary>
/// Device diagnostics the polling loop reads next to the model tags: the drive values (symbols set by the
/// diagnostics service from the hardware and program) every 2 s, and the CPU's active alarms every 5 s.
/// </summary>
public sealed class DeviceWatch
{
    private volatile IReadOnlyList<TagDef> _symbols = Array.Empty<TagDef>();
    private volatile Dictionary<string, (object? Value, string? Error)> _values = new();

    /// <summary>What to read for the drives; set by the diagnostics service.</summary>
    public IReadOnlyList<TagDef> Symbols { get => _symbols; set => _symbols = value; }
    public Dictionary<string, (object? Value, string? Error)> Values => _values;
    public DateTimeOffset? ValuesTime { get; private set; }

    /// <summary>The CPU's active alarms as last read; null when the source cannot read them.</summary>
    public IReadOnlyList<PlcKi.Core.Diagnostics.CpuAlarm>? Alarms { get; private set; }
    public DateTimeOffset? AlarmsTime { get; private set; }
    public string? AlarmsError { get; private set; }

    public void PublishValues(Dictionary<string, (object? Value, string? Error)> values, DateTimeOffset time) { _values = values; ValuesTime = time; }
    public void PublishAlarms(IReadOnlyList<PlcKi.Core.Diagnostics.CpuAlarm>? alarms, string? error, DateTimeOffset time)
    {
        if (alarms != null || error == null) Alarms = alarms;
        AlarmsError = error;
        AlarmsTime = time;
    }

    /// <summary>Not connected: what was read before is no longer true.</summary>
    public void Clear() { _values = new(); Alarms = null; }
}

/// <summary>
/// Owns the PLC connection. Connects, reads every model tag once per poll interval,
/// and reconnects with a growing delay (2 s up to 30 s) when the link drops.
/// The driver is synchronous, so the loop runs on its own thread.
/// </summary>
public sealed class PollingService : BackgroundService
{
    private readonly IPlcSource _source;
    private readonly StatusEngine _engine;
    private readonly LiveState _state;
    private readonly PlcKiOptions _opt;
    private readonly ILogger<PollingService> _log;
    private readonly WatchList _watch;
    private readonly DeviceWatch _devices;
    private readonly FastRecorder _fast;
    private readonly AlarmCaptureBook _captures;
    private readonly LinkGrace _grace;

    public PollingService(IPlcSource source, StatusEngine engine, LiveState state, PlcKiOptions opt, ILogger<PollingService> log, WatchList watch, DeviceWatch devices,
        FastRecorder fast, AlarmCaptureBook captures)
    {
        _grace = new LinkGrace(LinkGrace.WindowFor(opt.Plc.PollMs));
        _devices = devices;
        _watch = watch;
        _fast = fast;
        _captures = captures;
        _source = source;
        _engine = engine;
        _state = state;
        _opt = opt;
        _log = log;
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        Task.Factory.StartNew(() => Loop(stoppingToken), stoppingToken, TaskCreationOptions.LongRunning, TaskScheduler.Default);

    private void Loop(CancellationToken ct)
    {
        var tags = _engine.Model.Tags;
        var missing = new Dictionary<string, string>();
        Snapshot last = new();
        DateTimeOffset? lastGood = null;
        int backoffMs = 2000;
        string? error = null;
        var lastCpu = DateTime.MinValue;
        var lastDrives = DateTime.MinValue;
        var lastAlarms = DateTime.MinValue;
        var alarmFailures = 0;
        if (_source is not S7CommPlusSource)
            _fast.SetMode("reads", _opt.Plc.PollMs, _source.Kind == "snapshot" ? "values come from a test file."
                : "it needs a direct connection from this PC to the PLC (not through PLC-Ki Link yet).");

        while (!ct.IsCancellationRequested)
        {
            // A fresh install has no PLC address yet: say so, and check again for a while (no connection attempts)
            if (_source.Kind != "snapshot" && _opt.Plc.Via != "link" && string.IsNullOrWhiteSpace(_source.Address))
            {
                Publish(last, "notset", null, lastGood, missing);
                if (ct.WaitHandle.WaitOne(5000)) break;
                continue;
            }
            if (!_source.IsConnected)
            {
                Publish(last, "connecting", error, lastGood, missing);
                try
                {
                    missing = _source.Connect(tags);
                    error = null;
                    backoffMs = 2000;
                    foreach (var (id, why) in missing) _log.LogWarning("Tag {Id}: {Why}", id, why);
                }
                catch (Exception ex)
                {
                    // A PLC without the right program will not change in 2 s: wait the longest, and say it once
                    if (ex is PlcProgramException) backoffMs = 30_000;
                    _grace.Lost(DateTimeOffset.Now, ex.Message);
                    if (ex.Message != error)
                        _log.LogWarning("{Error} Retrying every {Delay} s.", ex.Message, backoffMs / 1000);
                    else
                        _log.LogDebug("Still: {Error}", ex.Message);
                    error = ex.Message;
                    Publish(last, "disconnected", error, lastGood, missing);
                    if (ct.WaitHandle.WaitOne(backoffMs)) break;
                    backoffMs = Math.Min(backoffMs * 2, 30_000);
                    continue;
                }
            }

            var started = DateTime.UtcNow;
            try
            {
                var snap = _source.Read();
                foreach (var (id, why) in missing)
                {
                    snap.Errors.TryAdd(id, why);
                    snap.Values.TryAdd(id, null);
                }
                last = snap;
                lastGood = snap.Time;
                var readMs = (DateTime.UtcNow - started).TotalMilliseconds;
                if (_grace.DownSince is { } down)
                    _log.LogInformation("PLC connection back after {Seconds:0.0} s (drop {Drops} since start).", (snap.Time - down).TotalSeconds, _grace.Drops);
                var bridged = _grace.Good(snap.Time);
                _fast.AddSnapshot(snap, _source.FastTagIds);
                var extra = _watch.Current();
                if (extra.Count > 0)
                {
                    try { _watch.Publish(_source.ReadSymbols(extra), snap.Time); }
                    catch (Exception ex) { _log.LogDebug(ex, "Reading the watched tags failed."); }
                }
                if (DateTime.UtcNow - lastCpu > TimeSpan.FromSeconds(2))
                {
                    lastCpu = DateTime.UtcNow;
                    try
                    {
                        var r = _state.Cpu = _source.ReadCpu();
                        _state.CpuMode.Update(r?.State, r?.Switch, r?.Error, r?.Time ?? DateTimeOffset.Now);
                    }
                    // A mode read that cost the connection is a drop like a failed read: handled below
                    catch (Exception ex) when (_source.IsConnected)
                    {
                        _log.LogDebug(ex, "Reading the CPU mode failed.");
                        _state.CpuMode.Update("Unknown", null, ex.Message, DateTimeOffset.Now);
                    }
                }
                ReadDevices(ref lastDrives, ref lastAlarms, ref alarmFailures);
                if (!_source.IsConnected) throw new IOException("The PLC connection closed during this read.");
                Publish(snap, _source.Kind == "snapshot" ? "snapshot" : "connected", null, lastGood, missing);
                if (bridged != null) _state.History.Note(bridged);
                Capture();
                var totalMs = (DateTime.UtcNow - started).TotalMilliseconds;
                if (totalMs > Math.Max(2 * _opt.Plc.PollMs, 2000))
                    _log.LogInformation("Slow poll: {Total:0} ms (model tags {Read:0} ms, pages and diagnostics {Rest:0} ms).", totalMs, readMs, totalMs - readMs);
            }
            catch (Exception ex)
            {
                error = ex.Message;
                _grace.Lost(DateTimeOffset.Now, ex.Message);
                _log.LogWarning("Read from {Address} failed: {Error}", _source.Address, ex.Message);
                _source.Disconnect();
                _fast.Break(DateTimeOffset.Now);
                _devices.Clear();
                // What the CPU said before the link dropped is no longer true now
                _state.Cpu = null;
                _state.CpuMode.Clear();
                Publish(last, "disconnected", error, lastGood, missing);
                Capture();
                continue;
            }

            var wait = _opt.Plc.PollMs - (int)(DateTime.UtcNow - started).TotalMilliseconds;
            if (wait > 0 && ct.WaitHandle.WaitOne(wait)) break;
        }
        _source.Disconnect();
    }

    /// <summary>Drive values every 2 s, the CPU's alarm list every 5 s (every 60 s after it failed three times).</summary>
    private void ReadDevices(ref DateTime lastDrives, ref DateTime lastAlarms, ref int alarmFailures)
    {
        var symbols = _devices.Symbols;
        if (symbols.Count > 0 && DateTime.UtcNow - lastDrives > TimeSpan.FromSeconds(2))
        {
            lastDrives = DateTime.UtcNow;
            try { _devices.PublishValues(_source.ReadSymbols(symbols), DateTimeOffset.Now); }
            catch (Exception ex) { _log.LogDebug(ex, "Reading the drive values failed."); }
        }
        var every = alarmFailures >= 3 ? TimeSpan.FromSeconds(60) : TimeSpan.FromSeconds(5);
        if (DateTime.UtcNow - lastAlarms > every)
        {
            lastAlarms = DateTime.UtcNow;
            try
            {
                _devices.PublishAlarms(_source.ReadCpuAlarms(), null, DateTimeOffset.Now);
                alarmFailures = 0;
            }
            catch (Exception ex)
            {
                if (alarmFailures++ == 0) _log.LogInformation("The CPU's alarm list cannot be read: {Error}", ex.Message);
                _devices.PublishAlarms(null, "The CPU's alarm list cannot be read: " + ex.Message, DateTimeOffset.Now);
            }
        }
    }

    /// <summary>Cuts the alarm captures that are due; an alarm too short for the reads goes into the event list too.</summary>
    private void Capture()
    {
        try
        {
            foreach (var c in _captures.Check(DateTimeOffset.Now, _opt.Plc.PollMs))
            {
                _log.LogInformation("Captured the seconds around alarm {Alarm} ({Tracks} tags changed).", c.AlarmId, c.Tracks.Count);
                if (c.Blip)
                    _state.History.Note(new MachineEvent(c.Time, "alarm",
                        $"{c.AlarmId} blipped: on for only {c.OnMs:0} ms, too short for the normal reads: {c.AlarmText}.") { Alarm = c.AlarmId });
            }
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Alarm capture failed.");
        }
    }

    private void Publish(Snapshot snap, string state, string? error, DateTimeOffset? lastGood, Dictionary<string, string> missing)
    {
        // A drop that is still within the grace shows as connected (values at most a few seconds old); History gets it anyway
        var shown = _grace.Shown(state, DateTimeOffset.Now);
        if (shown != state) error = null;
        state = shown;
        var info = new PlcLinkInfo(state, _source.Address, error, lastGood, _engine.Model.Tags.Count,
            Math.Max(snap.Errors.Count, missing.Count));
        try
        {
            var cpu = state is "connected" or "snapshot" ? _state.CpuMode.Current : null;
            _state.Publish(snap, _engine.Evaluate(snap, info, cpu));
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Status evaluation failed.");
        }
    }
}
