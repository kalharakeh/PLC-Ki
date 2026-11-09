using PlcKi.Core.Model;
using PlcKi.Core.Status;
using S7CommPlusDriver;
using S7CommPlusDriver.ClientApi;

namespace PlcKi.Server.Live;

/// <summary>
/// One connection for the values the pages ask for on top of the model (Tags, Program and Hardware pages, the CPU's alarm
/// list). It is kept apart from the polling connection on purpose: a lookup the PLC cannot answer, or a slow read, can
/// fault a driver connection, and then only this one is closed and opened again a little later. The polling connection,
/// the model values and the fast capture carry on.
/// </summary>
public interface IExtraLink<TTag> : IDisposable where TTag : class
{
    bool IsConnected { get; }

    /// <summary>The tag, or Tag == null with why the PLC does not have it. Throws when the connection broke.</summary>
    (TTag? Tag, string? NotFound) Lookup(string symbol);

    /// <summary>One result per tag, in order: the raw value, or an error for that tag. Throws when the connection broke.</summary>
    IReadOnlyList<(object? Raw, string? Error)> Read(IReadOnlyList<TTag> tags);
}

/// <summary>
/// Reads extra symbols over an IExtraLink: remembers what each symbol is (SymbolLookupCache, kept across reconnects),
/// looks up only a few new ones per poll (LookupBudget), and when the link breaks closes it, says why per symbol, and
/// waits before opening a new one (5 s, then longer). Used from the polling thread only.
/// </summary>
public sealed class ExtraReader<TTag> : IDisposable where TTag : class
{
    private readonly Func<IExtraLink<TTag>> _open;
    private readonly ILogger _log;
    private readonly Func<DateTimeOffset> _now;
    private IExtraLink<TTag>? _link;
    private DateTimeOffset _pausedUntil = DateTimeOffset.MinValue;
    private DateTimeOffset _lastUse;
    private TimeSpan _wait = FirstWait;
    private volatile bool _forget;

    public static readonly TimeSpan FirstWait = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan LongestWait = TimeSpan.FromSeconds(60);
    /// <summary>Closed when no page asked for anything this long.</summary>
    public static readonly TimeSpan IdleClose = TimeSpan.FromSeconds(30);

    public ExtraReader(Func<IExtraLink<TTag>> open, ILogger log, Func<DateTimeOffset>? now = null)
    {
        _open = open;
        _log = log;
        _now = now ?? (() => DateTimeOffset.Now);
    }

    public SymbolLookupCache<TTag> Cache { get; } = new();
    public bool IsOpen => _link != null;
    /// <summary>Why the link is paused (closed after a failure), or null.</summary>
    public string? Problem { get; private set; }
    /// <summary>Times the link was opened and closed after a failure, since start.</summary>
    public int Opens { get; private set; }
    public int Failures { get; private set; }

    /// <summary>The program changed (Refresh from TIA): what was learned about the symbols is old. Any thread.</summary>
    public void ForgetProgram() => _forget = true;

    public Dictionary<string, (object? Value, string? Error)> Read(IReadOnlyList<TagDef> symbols, LookupBudget budget)
    {
        var now = _now();
        if (_forget) { _forget = false; Cache.Clear(); }
        var result = new Dictionary<string, (object?, string?)>();
        if (symbols.Count == 0) return result;
        _lastUse = now;

        var link = Ensure(now);
        var readable = new List<(TagDef Def, TTag Tag)>();
        foreach (var def in symbols)
        {
            if (Cache.TryGet(def.Symbol, now, out var known))
            {
                if (known.Tag != null) readable.Add((def, known.Tag));
                else result[def.Symbol] = (null, known.Error);
                continue;
            }
            if (link == null) { result[def.Symbol] = (null, Paused()); continue; }
            // A few lookups per poll; the rest follow in the next polls (no entry yet = still being looked up)
            if (!budget.TryTake()) continue;
            try
            {
                var (tag, notFound) = link.Lookup(def.Symbol);
                if (tag != null) { Cache.Found(def.Symbol, tag, now); readable.Add((def, tag)); }
                else
                {
                    var why = "Not on the PLC: " + (notFound ?? "the PLC does not know this name.");
                    Cache.NotFound(def.Symbol, why, now);
                    result[def.Symbol] = (null, why);
                }
            }
            catch (Exception ex)
            {
                Broke(ex, "looking up " + def.Symbol, now);
                link = null;
                result[def.Symbol] = (null, Paused());
            }
        }

        if (readable.Count == 0) return result;
        if (link == null)
        {
            foreach (var (def, _) in readable) result[def.Symbol] = (null, Paused());
            return result;
        }
        IReadOnlyList<(object? Raw, string? Error)> read;
        try { read = link.Read(readable.Select(r => r.Tag).ToList()); }
        catch (Exception ex)
        {
            Broke(ex, $"reading {readable.Count} extra values", now);
            foreach (var (def, _) in readable) result[def.Symbol] = (null, Paused());
            return result;
        }
        for (int i = 0; i < readable.Count; i++)
        {
            var def = readable[i].Def;
            var (raw, error) = i < read.Count ? read[i] : (null, "no answer for this value");
            if (error != null) { result[def.Symbol] = (null, error); continue; }
            object? value;
            try { value = def.Type is "Bool" or "Real" or "DInt" or "Int" or "Time" ? TagValue.Normalize(def, raw) : raw?.ToString(); }
            catch { value = raw?.ToString(); }
            result[def.Symbol] = (value, null);
        }
        return result;
    }

    /// <summary>Runs something else on the link (the CPU's alarm list). Null with the reason when the link is paused or broke.</summary>
    public T? Run<T>(string what, Func<IExtraLink<TTag>, T> op, out string? error) where T : class
    {
        var now = _now();
        _lastUse = now;
        error = null;
        var link = Ensure(now);
        if (link == null) { error = Paused(); return null; }
        try { return op(link); }
        catch (Exception ex) when (!link.IsConnected)
        {
            Broke(ex, what, now);
            error = Paused();
            return null;
        }
    }

    /// <summary>Closes the link when no page asked for anything for a while, so an idle PLC-Ki holds one connection.</summary>
    public void CloseIfIdle()
    {
        if (_link != null && _now() - _lastUse > IdleClose)
        {
            _log.LogDebug("Extra values: no page asked for {Idle} s, closing that connection.", IdleClose.TotalSeconds);
            Close();
        }
    }

    public void Close()
    {
        var l = _link;
        _link = null;
        if (l == null) return;
        try { l.Dispose(); } catch (Exception ex) { _log.LogDebug(ex, "Closing the extra-values connection failed."); }
    }

    public void Dispose() => Close();

    private IExtraLink<TTag>? Ensure(DateTimeOffset now)
    {
        if (_link is { IsConnected: true }) return _link;
        if (_link != null) Close();                 // faulted: never use it again (the driver would leak a connection)
        if (now < _pausedUntil) return null;
        try
        {
            _link = _open();
            Opens++;
            if (Problem != null) _log.LogInformation("Extra values: connection open again.");
            Problem = null;
            _wait = FirstWait;
            return _link;
        }
        catch (Exception ex)
        {
            Pause("connecting: " + Describe(ex), now);
            return null;
        }
    }

    private void Broke(Exception ex, string what, DateTimeOffset now)
    {
        Close();
        Failures++;
        Pause(what + ": " + Describe(ex), now);
    }

    private void Pause(string why, DateTimeOffset now)
    {
        Problem = why;
        _pausedUntil = now + _wait;
        _log.LogWarning("Extra values (Tags, Program and Hardware pages): the connection for them failed while {Why}. " +
                        "Trying again in {Wait} s; the machine's own values are not affected.", why, _wait.TotalSeconds);
        _wait = TimeSpan.FromTicks(Math.Min(_wait.Ticks * 2, LongestWait.Ticks));
    }

    private string Paused() => "Extra values are paused for a moment: " + (Problem ?? "the connection for them is not open.");

    internal static string Describe(Exception ex)
    {
        var parts = new List<string>();
        for (var e = ex; e != null && parts.Count < 4; e = e.InnerException)
            if (!string.IsNullOrWhiteSpace(e.Message) && !parts.Contains(e.Message)) parts.Add(e.Message);
        return string.Join(" -> ", parts);
    }
}

/// <summary>The real IExtraLink: its own read-only S7CommPlus client (WriteEnabled stays false).</summary>
public sealed class S7ExtraLink : IExtraLink<PlcTag>
{
    private readonly ILogger _log;

    public S7ExtraLink(S7CommPlusClientOptions options, ILogger log)
    {
        _log = log;
        Client = new S7CommPlusClient(options);
        S7Connections.Track(Client, "extra-values", log);
        try { Client.ConnectAsync().GetAwaiter().GetResult(); }
        catch
        {
            Dispose();
            throw;
        }
    }

    public S7CommPlusClient Client { get; }
    public bool IsConnected => Client.IsConnected;

    public (PlcTag? Tag, string? NotFound) Lookup(string symbol)
    {
        try { return (Client.GetTagBySymbolAsync(symbol).GetAwaiter().GetResult(), null); }
        // Still connected: the PLC answered, it just has no such name. Otherwise the link broke: let the reader close it.
        catch (Exception ex) when (Client.IsConnected) { return (null, ExtraReader<PlcTag>.Describe(ex)); }
    }

    public IReadOnlyList<(object? Raw, string? Error)> Read(IReadOnlyList<PlcTag> tags)
    {
        var read = Client.ReadAsync(tags).GetAwaiter().GetResult();
        var list = new List<(object?, string?)>(tags.Count);
        for (int i = 0; i < tags.Count; i++)
        {
            var tag = tags[i];
            var item = i < read.Items.Count ? read.Items[i] : default;
            if (!item.IsSuccess || (tag.Quality & PlcTagQC.TAG_QUALITY_MASK) != PlcTagQC.TAG_QUALITY_GOOD)
                list.Add((null, $"read error 0x{item.ItemError:X}"));
            else
                list.Add((tag.GetType().GetProperty("Value")?.GetValue(tag), null));
        }
        return list;
    }

    public void Dispose()
    {
        try { Client.DisconnectAsync().GetAwaiter().GetResult(); } catch (Exception ex) { _log.LogDebug(ex, "Disconnect error ignored."); }
        try { Client.DisposeAsync().AsTask().GetAwaiter().GetResult(); } catch (Exception ex) { _log.LogDebug(ex, "Dispose error ignored."); }
    }
}
