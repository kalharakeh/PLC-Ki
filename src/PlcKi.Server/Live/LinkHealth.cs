using PlcKi.Core.Status;
using S7CommPlusDriver;

namespace PlcKi.Server.Live;

/// <summary>
/// What PLC-Ki learned about extra symbols (Tags, Program and Hardware pages): the PLC's tag for a symbol, or that the PLC
/// does not have it. It belongs to the program, not to one connection, so a reconnect keeps it: a name the PLC does not
/// know is not looked up again on every reconnect. Cleared when the program changes (Refresh from TIA).
/// Used from the polling thread only.
/// </summary>
public sealed class SymbolLookupCache<TTag> where TTag : class
{
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);

    /// <summary>A name the PLC said it does not have is asked again after this long (a download may have added it).</summary>
    public TimeSpan RetryNotFoundAfter { get; init; } = TimeSpan.FromMinutes(10);

    public sealed record Entry(TTag? Tag, string? Error, DateTimeOffset Time);

    public int Count => _entries.Count;

    /// <summary>Found or "not on the PLC" and not due again yet. False = look it up.</summary>
    public bool TryGet(string symbol, DateTimeOffset now, out Entry entry)
    {
        if (_entries.TryGetValue(symbol, out entry!))
        {
            if (entry.Tag != null || now - entry.Time < RetryNotFoundAfter) return true;
            _entries.Remove(symbol);
        }
        entry = null!;
        return false;
    }

    public void Found(string symbol, TTag tag, DateTimeOffset now) => _entries[symbol] = new Entry(tag, null, now);
    public void NotFound(string symbol, string why, DateTimeOffset now) => _entries[symbol] = new Entry(null, why, now);

    /// <summary>A found tag that no longer reads (the program on the PLC changed): look it up again.</summary>
    public void Forget(string symbol) => _entries.Remove(symbol);

    /// <summary>The program changed: everything learned about it is old.</summary>
    public void Clear() => _entries.Clear();
}

/// <summary>
/// How much lookup work one poll may do, so the model tags keep their pace: at most a number of lookups, and none started
/// after a time. A lookup that already started is not cut short (the driver cannot cancel one safely).
/// </summary>
public sealed class LookupBudget
{
    private readonly int _max;
    private readonly TimeSpan _time;
    private readonly Func<TimeSpan> _elapsed;
    private int _used;

    public LookupBudget(int max, TimeSpan time, Func<TimeSpan>? elapsed = null)
    {
        _max = max;
        _time = time;
        if (elapsed == null)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            elapsed = () => sw.Elapsed;
        }
        _elapsed = elapsed;
    }

    public int Used => _used;

    /// <summary>True when one more lookup fits in this poll (and counts it).</summary>
    public bool TryTake()
    {
        if (_used >= _max || _elapsed() >= _time) return false;
        _used++;
        return true;
    }
}

/// <summary>
/// Keeps a short drop of the PLC link from flashing "Not connected": while the last good read is recent, the status still
/// says connected (the values are at most a few seconds old). Every drop is still recorded in History: a drop that came
/// back within the grace gets its own event here; a longer one shows as "Lost the PLC connection" (MachineHistory.Diff),
/// so it is never written twice.
/// Used from the polling thread only.
/// </summary>
public sealed class LinkGrace
{
    private DateTimeOffset? _lastGood;
    private DateTimeOffset? _droppedAt;
    private string? _reason;
    private bool _shownLost;

    public LinkGrace(TimeSpan window) { Window = window; }

    public TimeSpan Window { get; }

    /// <summary>How many times the link dropped since PLC-Ki started.</summary>
    public int Drops { get; private set; }

    /// <summary>When the current drop started; null while the link is up.</summary>
    public DateTimeOffset? DownSince => _droppedAt;

    /// <summary>
    /// How long a drop may last before the page says "Not connected". pollMs is the time between two reads (PlcKi:Plc:PollMs).
    /// </summary>
    public static TimeSpan WindowFor(int pollMs)
    {
        // Three polls, at least 3 s (a reconnect with login, lookups and subscription takes 1-4 s on PLCSIM) and at most 10 s
        return TimeSpan.FromMilliseconds(Math.Clamp(3 * pollMs, 3000, 10000));
    }

    /// <summary>The link dropped (once per drop; later failed reconnects keep the first reason).</summary>
    public void Lost(DateTimeOffset now, string reason)
    {
        if (_droppedAt != null) return;
        _droppedAt = now;
        _reason = reason;
        _shownLost = false;
        Drops++;
    }

    /// <summary>A good read. Returns the History event for a drop the grace hid, else null.</summary>
    public MachineEvent? Good(DateTimeOffset now)
    {
        MachineEvent? ev = null;
        if (_droppedAt is { } at && !_shownLost)
            ev = new MachineEvent(now, "link", "The PLC connection dropped for " +
                (now - at).TotalSeconds.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + " s and came back by itself" +
                (string.IsNullOrWhiteSpace(_reason) ? "." : $" (reason: {_reason})."));
        _droppedAt = null;
        _reason = null;
        _lastGood = now;
        return ev;
    }

    /// <summary>The state the page sees: "connected" during a drop that is still within the grace, else the real one.</summary>
    public string Shown(string actual, DateTimeOffset now)
    {
        if (actual is not ("connecting" or "disconnected") || _droppedAt == null) return actual;
        if (!_shownLost && _lastGood is { } good && now - good <= Window) return "connected";
        _shownLost = true;
        return actual;
    }
}

/// <summary>
/// Counts the S7 connections PLC-Ki holds, from the driver's own state changes, and warns when there are more than expected
/// or when the driver opened a new connection without closing the old one (it does that when a client in the Faulted
/// state is used again; S7CommPlusSource never does, see Guard). Thread-safe: the driver raises the events on its threads.
/// </summary>
public static class S7Connections
{
    /// <summary>The polling connection, the extra-values connection, and one change being written.</summary>
    public const int Expected = 3;

    private static int _open;
    private static int _leaked;

    public static int Open => Volatile.Read(ref _open);
    public static int Leaked => Volatile.Read(ref _leaked);

    /// <summary>Follows one client: +1 when a session connects, -1 when it is closed, and a leak when a faulted one is replaced.</summary>
    public static void Track(S7CommPlusClient client, string role, ILogger log)
    {
        var tracker = new SessionTracker();
        client.ConnectionStateChanged += (_, e) =>
        {
            var change = tracker.On(e.OldState, e.NewState);
            if (change.Leaked)
            {
                Interlocked.Increment(ref _leaked);
                log.LogWarning("S7 {Role} connection: the driver opened a new connection without closing the faulted one ({Reason}). " +
                               "It stays open on the PLC until the PLC drops it.", role, e.Exception?.Message ?? "no reason given");
            }
            if (change.Delta == 0) return;
            var open = Interlocked.Add(ref _open, change.Delta);
            if (change.Delta > 0 && open > Expected)
                log.LogWarning("PLC-Ki now holds {Open} S7 connections to {Address} (expected at most {Expected}).", open, client.Options.Address, Expected);
            log.LogDebug("S7 {Role} connection {What}; {Open} open.", role, change.Delta > 0 ? "opened" : "closed", open);
        };
        client.CommunicationError += (_, e) =>
            log.LogDebug("S7 {Role} connection: {Operation} failed ({Transient}): {Error}", role, e.Exception.Operation,
                e.Exception.IsTransient ? "the driver marks the connection faulted" : "the connection stays", e.Exception.Message);
    }
}

/// <summary>The bookkeeping of one client's sessions, kept apart from the driver so it can be tested.</summary>
public sealed class SessionTracker
{
    private bool _open;

    public readonly record struct Change(int Delta, bool Leaked);

    public Change On(S7CommPlusConnectionState from, S7CommPlusConnectionState to)
    {
        // Faulted -> Connecting is the driver's silent reconnect: the old session is dropped without being closed
        if (to is S7CommPlusConnectionState.Connecting or S7CommPlusConnectionState.Reconnecting
            && from == S7CommPlusConnectionState.Faulted && _open)
        {
            _open = false;              // the leaked one stays counted as open: it is still a TCP connection
            return new Change(0, true);
        }
        if (to == S7CommPlusConnectionState.Connected)
        {
            _open = true;
            return new Change(1, false);
        }
        if (to == S7CommPlusConnectionState.Disconnected && _open)
        {
            _open = false;
            return new Change(-1, false);
        }
        return new Change(0, false);
    }
}
