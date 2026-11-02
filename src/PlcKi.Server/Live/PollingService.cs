using PlcKi.Core.Model;
using PlcKi.Core.Status;

namespace PlcKi.Server.Live;

/// <summary>Latest status and the recent history, shared between the polling loop, the web API and the assistant.</summary>
public sealed class LiveState
{
    private volatile MachineStatus? _status;
    private volatile Snapshot? _snapshot;

    public LiveState(MachineHistory history) => History = history;

    public MachineStatus? Status => _status;
    public Snapshot? Snapshot => _snapshot;
    public MachineHistory History { get; }

    public void Publish(Snapshot snap, MachineStatus status)
    {
        _snapshot = snap;
        _status = status;
        History.Add(snap, status);
    }
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

    public PollingService(IPlcSource source, StatusEngine engine, LiveState state, PlcKiOptions opt, ILogger<PollingService> log)
    {
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

        while (!ct.IsCancellationRequested)
        {
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
                    error = ex.Message;
                    _log.LogWarning("Cannot connect to {Address}: {Error} Retrying in {Delay} s.", _source.Address, ex.Message, backoffMs / 1000);
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
                Publish(snap, _source.Kind == "snapshot" ? "snapshot" : "connected", null, lastGood, missing);
            }
            catch (Exception ex)
            {
                error = ex.Message;
                _log.LogWarning("Read from {Address} failed: {Error}", _source.Address, ex.Message);
                _source.Disconnect();
                Publish(last, "disconnected", error, lastGood, missing);
                continue;
            }

            var wait = _opt.Plc.PollMs - (int)(DateTime.UtcNow - started).TotalMilliseconds;
            if (wait > 0 && ct.WaitHandle.WaitOne(wait)) break;
        }
        _source.Disconnect();
    }

    private void Publish(Snapshot snap, string state, string? error, DateTimeOffset? lastGood, Dictionary<string, string> missing)
    {
        var info = new PlcLinkInfo(state, _source.Address, error, lastGood, _engine.Model.Tags.Count,
            Math.Max(snap.Errors.Count, missing.Count));
        try
        {
            _state.Publish(snap, _engine.Evaluate(snap, info));
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Status evaluation failed.");
        }
    }
}
