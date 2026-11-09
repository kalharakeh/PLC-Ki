using PlcKi.Core.Model;
using PlcKi.Core.Status;
using S7CommPlusDriver;
using S7CommPlusDriver.ClientApi;

namespace PlcKi.Server.Live;

/// <summary>
/// Reads tags by symbol over S7CommPlus (the protocol TIA Portal and HMIs use).
/// Uses DotNetProjects.S7CommPlusDriver. Security "Auto" tries TLS first and falls back to the older
/// challenge login, which CPUs without secure PG/PC communication (and PLCSIM) use.
/// Read-only: the driver's write gate (WriteEnabled) stays false, and this app calls no write or CPU start/stop API
/// (tests/PlcKi.Tests/ReadOnlyTests.cs checks the compiled app for that).
/// Used from the polling thread only, so the async calls are awaited synchronously.
///
/// Two connections at most: the polling connection (model tags, fast capture, CPU mode) and, while a page asks for more,
/// the extra-values connection (ExtraReader). One rule holds for both: a client that faulted is never used again. The
/// driver marks a client Faulted after a timeout or a broken answer but leaves its connection open, and the next call on
/// that client silently opens a new one without closing the old (S7CommPlusClient.EnsureConnectedCoreAsync, driver 6.1
/// and 6.2). That is how PLC-Ki once ended up holding 5 connections to one PLC. So after any failure that leaves the
/// client not connected, it is disconnected here and the polling loop reconnects, with the reason in the log and History.
/// </summary>
public sealed class S7CommPlusSource : IPlcSource
{
    private readonly PlcOptions _opt;
    private readonly ILogger _log;
    private readonly FastRecorder? _fast;
    private readonly List<S7CommPlusTagSubscription> _subs = new();
    private readonly HashSet<string> _fastIds = new(StringComparer.Ordinal);
    private S7CommPlusClient? _client;
    private readonly List<(TagDef Def, PlcTag Tag)> _tags = new();
    private readonly ExtraReader<PlcTag> _extras;
    private LookupBudget? _budget;
    private const int LookupFailuresBeforeGivingUp = 3;
    /// <summary>Lookups for extra symbols per poll, and the time after which no new one starts.</summary>
    private const int MaxLookupsPerRead = 30;
    private static readonly TimeSpan LookupTime = TimeSpan.FromMilliseconds(1500);

    /// <param name="fast">Where the subscription puts every change; null = no subscription (probe, PLC-Ki Link).</param>
    public S7CommPlusSource(PlcOptions opt, ILogger log, FastRecorder? fast = null)
    {
        _opt = opt;
        _log = log;
        _fast = fast;
        _extras = new ExtraReader<PlcTag>(() => new S7ExtraLink(Options(ExtrasSecurity()), _log), _log);
    }

    /// <summary>Why the polling connection was last closed (null when it never was).</summary>
    public string? LastDropReason { get; private set; }

    /// <summary>The program changed (Refresh from TIA): forget which extra symbols exist and the CPU's text lists. Any thread.</summary>
    public void ForgetProgram()
    {
        _extras.ForgetProgram();
        _textListsStale = true;
    }

    private S7CommPlusClientOptions Options(S7CommPlusSecurityMode security) => new()
    {
        Address = _opt.Address,
        Password = Environment.GetEnvironmentVariable("PLCKI_PLC_PASSWORD") ?? _opt.Password ?? "",
        Username = _opt.Username ?? "",
        ConnectTimeout = TimeSpan.FromMilliseconds(_opt.TimeoutMs),
        RequestTimeout = TimeSpan.FromMilliseconds(_opt.TimeoutMs),
        AutoReconnect = false,          // the polling loop reconnects
        // WriteEnabled is left at its default (false): PLC-Ki is read-only. The tests forbid setting it.
        SecurityMode = security,
        Logger = _log,
    };

    /// <summary>The extra-values connection logs in the way the polling one did, so it does not try TLS first every time.</summary>
    private S7CommPlusSecurityMode ExtrasSecurity() =>
        NegotiatedSecurity != null && Enum.TryParse<S7CommPlusSecurityMode>(NegotiatedSecurity, out var m) ? m : ParseSecurity(_opt.Security);

    /// <summary>
    /// Runs a call on the polling connection. When it fails and the client is no longer connected, the connection is closed
    /// here (never used again, see the class comment) and an IOException with the reason goes to the polling loop.
    /// </summary>
    private T Main<T>(string what, Func<S7CommPlusClient, T> call)
    {
        var client = _client ?? throw new IOException("Not connected.");
        try { return call(client); }
        catch (Exception ex) when (!client.IsConnected)
        {
            var why = $"{what} failed: {Describe(ex)}";
            Disconnect(why);
            throw new IOException(why, ex);
        }
    }

    /// <summary>Model tags the PLC sends changes for (the subscription); the reads cover the rest.</summary>
    public IReadOnlySet<string> FastTagIds { get { lock (_fastIds) return new HashSet<string>(_fastIds, StringComparer.Ordinal); } }

    public string Kind => "s7";
    public string Address => _opt.Address;
    public bool IsConnected => _client?.IsConnected == true;

    /// <summary>Security mode actually used after connecting (Tls or LegacyChallenge).</summary>
    public string? NegotiatedSecurity { get; private set; }

    public static S7CommPlusSecurityMode ParseSecurity(string? text) =>
        Enum.TryParse<S7CommPlusSecurityMode>(text, ignoreCase: true, out var m) ? m : S7CommPlusSecurityMode.Auto;

    public Dictionary<string, string> Connect(IReadOnlyList<TagDef> tags) => Connect(tags, ParseSecurity(_opt.Security));

    public Dictionary<string, string> Connect(IReadOnlyList<TagDef> tags, S7CommPlusSecurityMode security)
    {
        Disconnect(null);
        var client = new S7CommPlusClient(Options(security));
        S7Connections.Track(client, "polling", _log);
        try
        {
            client.ConnectAsync().GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            Close(client);
            throw new IOException($"Connection to {_opt.Address} failed ({security}): {Describe(ex)}", ex);
        }
        // The client keeps its own copy of the options; read the mode it actually used from there
        NegotiatedSecurity = client.Options.NegotiatedSecurityMode?.ToString() ?? security.ToString();

        var missing = new Dictionary<string, string>();
        _tags.Clear();
        foreach (var def in tags)
        {
            try
            {
                var tag = client.GetTagBySymbolAsync(def.Symbol).GetAwaiter().GetResult();
                _tags.Add((def, tag));
            }
            catch (Exception ex)
            {
                // A lookup that faulted the client (timeout, broken answer): stop, close it, and let the loop retry later.
                // Looking up the next tag on it would open a second connection and leave this one open.
                if (!client.IsConnected)
                {
                    Close(client);
                    _tags.Clear();
                    throw new IOException($"Connection to {_opt.Address} dropped while looking up {def.Symbol}: {Describe(ex)}", ex);
                }
                missing[def.Id] = $"symbol {def.Symbol}: {Describe(ex)}";
                // When the first lookups all fail, the PLC is refusing lookups as such (the driver drops the
                // session and logs in again for each one). Stop here, so the polling loop waits before it retries.
                if (_tags.Count == 0 && missing.Count >= LookupFailuresBeforeGivingUp)
                    break;
            }
        }
        if (_tags.Count == 0 && tags.Count > 0)
        {
            var why = WhyNoTags(client, tags) ?? "the PLC answered no tag lookup. First error: " + (missing.Values.FirstOrDefault() ?? "none");
            Close(client);
            throw new PlcProgramException($"Connected to {_opt.Address}, but {why}");
        }
        _client = client;
        _log.LogInformation("Connected to {Address} ({Security}): {Found} of {Total} tags found.",
            _opt.Address, NegotiatedSecurity, _tags.Count, tags.Count);
        if (_fast != null) Subscribe(client);
        if (!client.IsConnected)
        {
            var why = "making the fast-capture subscription broke the connection: " + (_subscriptionProblem ?? "no reason given");
            Disconnect(why);
            throw new IOException(why);
        }
        return missing;
    }

    private void Close(S7CommPlusClient c)
    {
        try { c.DisconnectAsync().GetAwaiter().GetResult(); }
        catch (Exception ex) { _log.LogDebug(ex, "Disconnect error ignored."); }
        try { c.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
        catch (Exception ex) { _log.LogDebug(ex, "Dispose error ignored."); }
    }

    // ---------- fast data: a subscription, like an HMI's ----------
    // The CPU samples the subscribed tags every FastMs and sends only what changed, with its own time stamp.
    // Nothing changes on the PLC side (no trace, no extra block). It runs next to the normal reads; when it cannot be
    // made (old firmware, too many tags, a PLC that refuses it), the normal reads carry on and feed the recording.
    private const int TagsPerSubscription = 200;
    private DateTimeOffset _noSubscriptionUntil = DateTimeOffset.MinValue;
    private string? _subscriptionProblem;
    private readonly Queue<double> _skews = new();
    private double _skewMs;

    private void Subscribe(S7CommPlusClient client)
    {
        var fast = _fast!;
        lock (_fastIds) _fastIds.Clear();
        if (_opt.FastMs <= 0)
        {
            fast.SetMode("reads", _opt.PollMs, "switched off in the settings (Fast capture).");
            return;
        }
        if (DateTimeOffset.Now < _noSubscriptionUntil)
        {
            fast.SetMode("reads", _opt.PollMs, "the last try to make the subscription broke the connection (" + _subscriptionProblem +
                "). It is tried again at " + _noSubscriptionUntil.ToString("HH:mm") + ".");
            return;
        }
        var cycle = (ushort)Math.Clamp(_opt.FastMs, 10, 10_000);
        string? firstError = null;
        for (int i = 0; i < _tags.Count; i += TagsPerSubscription)
        {
            var chunk = _tags.Skip(i).Take(TagsPerSubscription).ToList();
            try
            {
                var sub = client.SubscribeTagsAsync(chunk.Select(c => c.Tag), new S7CommPlusSubscriptionOptions
                {
                    CycleTimeMilliseconds = cycle,
                    // A quiet machine sends nothing for a long time: that is not an error
                    MaxConsecutiveTimeoutsBeforeFault = 0,
                }).GetAwaiter().GetResult();
                var defs = new Dictionary<PlcTag, TagDef>(ReferenceEqualityComparer.Instance);
                foreach (var c in chunk) defs[c.Tag] = c.Def;
                sub.NotificationReceived += (_, e) => OnNotification(e.Notification, defs);
                sub.StateChanged += (_, e) =>
                {
                    if (e.NewState != S7CommPlusSubscriptionState.Faulted) return;
                    _log.LogWarning("Fast capture stopped: {Error}. The normal reads carry on.", e.Exception?.Message);
                    fast.SetMode("reads", _opt.PollMs, "the PLC stopped sending changes (" + (e.Exception?.Message ?? "no reason given") + "). It is tried again on the next connect.");
                    lock (_fastIds) _fastIds.Clear();
                };
                _subs.Add(sub);
                lock (_fastIds) foreach (var c in chunk) _fastIds.Add(c.Def.Id);
            }
            catch (Exception ex)
            {
                firstError ??= Describe(ex);
                _log.LogWarning("Fast capture: the PLC refused a subscription for {Count} tags: {Error}", chunk.Count, Describe(ex));
                if (!client.IsConnected)
                {
                    // The failed subscription faulted the connection: the polling loop reconnects, without fast capture
                    // for a while, so this does not turn into a reconnect loop
                    _noSubscriptionUntil = DateTimeOffset.Now.AddMinutes(10);
                    _subscriptionProblem = Describe(ex);
                    break;
                }
            }
        }
        if (_subs.Count > 0)
        {
            fast.SetMode("subscription", cycle, firstError == null ? null : "some tags are only read normally: " + firstError, _fastIds.Count);
            _log.LogInformation("Fast capture on: {Count} tags, sampled every {Cycle} ms by the PLC.", _fastIds.Count, cycle);
        }
        else
        {
            fast.SetMode("reads", _opt.PollMs, "the PLC did not accept a subscription (" + (firstError ?? "no tags") + ").");
        }
    }

    private void OnNotification(S7CommPlusTagNotification n, Dictionary<PlcTag, TagDef> defs)
    {
        var fast = _fast!;
        fast.Tick();
        var time = Align(n.Timestamp);
        foreach (var item in n.Items)
        {
            if (!item.IsSuccess || item.Tag == null || !defs.TryGetValue(item.Tag, out var def)) continue;
            object? value;
            try { value = TagValue.Normalize(def, RawOf(item.Value) ?? item.Tag.GetType().GetProperty("Value")?.GetValue(item.Tag)); }
            catch { continue; }
            if (value != null) fast.Add(time, def.Id, value);
        }
    }

    /// <summary>The driver hands the raw protocol value (ValueBool, ValueReal ...); they all have GetValue().</summary>
    private static object? RawOf(object? pvalue)
    {
        if (pvalue == null) return null;
        try { return pvalue.GetType().GetMethod("GetValue", Type.EmptyTypes)?.Invoke(pvalue, null); }
        catch { return null; }
    }

    /// <summary>
    /// The PLC's time stamp moved onto this PC's clock: the PLC clock can be off by seconds or hours (PLCSIM uses the
    /// VM's clock), but the spacing between its time stamps is exact. The offset is the smallest (receive time - PLC
    /// time) of the last 100 messages, so network delay does not shift it.
    /// </summary>
    private DateTimeOffset Align(DateTime plcUtc)
    {
        var now = DateTimeOffset.Now;
        if (plcUtc.Year < 2000) return now;
        var plc = new DateTimeOffset(DateTime.SpecifyKind(plcUtc, DateTimeKind.Utc));
        lock (_skews)
        {
            _skews.Enqueue((now - plc).TotalMilliseconds);
            while (_skews.Count > 100) _skews.Dequeue();
            _skewMs = _skews.Min();
        }
        return plc.AddMilliseconds(_skewMs).ToLocalTime();
    }

    private void StopSubscriptions()
    {
        foreach (var s in _subs)
        {
            try { s.StopAsync(CancellationToken.None).Wait(2000); } catch { /* the connection goes anyway */ }
        }
        _subs.Clear();
        lock (_fastIds) _fastIds.Clear();
        lock (_skews) _skews.Clear();
        _fast?.Break(DateTimeOffset.Now);
    }

    /// <summary>
    /// When no tag can be found, looks at the blocks on the PLC (read-only) to say why in plain words:
    /// no program at all (e.g. an empty PLCSIM instance), or a program without the model's data blocks.
    /// </summary>
    private string? WhyNoTags(S7CommPlusClient client, IReadOnlyList<TagDef> tags)
    {
        IReadOnlyList<S7CommPlusBlockInfo> blocks;
        try { blocks = client.BrowseBlocksAsync().GetAwaiter().GetResult(); }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Block list not available.");
            return "the PLC answered no tag lookup and no block list. Most likely no program is loaded: download the program to the PLC (for PLCSIM, from TIA Portal).";
        }
        if (blocks.Count == 0 || !blocks.Any(b => b.Type is S7CommPlusBlockType.DB or S7CommPlusBlockType.OB))
            return "the PLC has no program loaded. Download the program to the PLC (for PLCSIM, from TIA Portal).";

        var onPlc = blocks.Where(b => b.Type == S7CommPlusBlockType.DB).Select(b => b.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var wanted = tags.Select(t => DataBlockOf(t.Symbol)).Where(n => n != null).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var absent = wanted.Where(n => !onPlc.Contains(n!)).ToList();
        if (absent.Count > 0)
            return $"the program on the PLC is not the one in the machine model: data block(s) {string.Join(", ", absent)} are missing " +
                   $"(the PLC has {blocks.Count} blocks). Download the right program, or start PLC-Ki with the model for this machine.";
        return null;
    }

    /// <summary>"DB_IO" from "\"DB_IO\".PB_Start".</summary>
    private static string? DataBlockOf(string symbol)
    {
        if (!symbol.StartsWith('"')) return symbol.Split('.')[0];
        var end = symbol.IndexOf('"', 1);
        return end > 1 ? symbol[1..end] : null;
    }

    public Snapshot Read()
    {
        if (_client == null) throw new InvalidOperationException("Not connected.");
        // A new lookup allowance for this poll (ReadSymbols, for the pages), and close the extra connection when no page asks
        _budget = new LookupBudget(MaxLookupsPerRead, LookupTime);
        _extras.CloseIfIdle();
        var snap = new Snapshot { Time = DateTimeOffset.Now };
        if (_tags.Count == 0) return snap;

        // Any failure here means the polling connection is not usable: close it whether or not the driver still calls it connected
        S7CommPlusBatchResult<S7CommPlusTagReadResult> result;
        try { result = Main("Reading the model tags", c => c.ReadAsync(_tags.Select(t => t.Tag).ToList()).GetAwaiter().GetResult()); }
        catch (IOException) { throw; }
        catch (Exception ex)
        {
            var why = $"Reading the model tags failed: {Describe(ex)}";
            Disconnect(why);
            throw new IOException(why, ex);
        }

        for (int i = 0; i < _tags.Count; i++)
        {
            var (def, tag) = _tags[i];
            var item = i < result.Items.Count ? result.Items[i] : default;
            if (!item.IsSuccess || (tag.Quality & PlcTagQC.TAG_QUALITY_MASK) != PlcTagQC.TAG_QUALITY_GOOD)
            {
                snap.Errors[def.Id] = $"read error 0x{item.ItemError:X}, quality 0x{tag.Quality:X2}";
                snap.Values[def.Id] = null;
                continue;
            }
            object? raw = tag.GetType().GetProperty("Value")?.GetValue(tag);
            try { snap.Values[def.Id] = TagValue.Normalize(def, raw); }
            catch (Exception ex)
            {
                snap.Errors[def.Id] = $"cannot use value {raw}: {ex.Message}";
                snap.Values[def.Id] = null;
            }
        }
        return snap;
    }

    /// <summary>
    /// Operating mode and mode switch (a read; the driver's mode change is never called, see ReadOnlyTests).
    /// A failed read gives "Unknown"; when it cost the connection, an IOException goes to the polling loop instead.
    /// </summary>
    public CpuReading? ReadCpu()
    {
        if (_client == null) return null;
        try
        {
            var st = Main("Reading the CPU mode", c => c.GetCpuStateAsync(CancellationToken.None).GetAwaiter().GetResult());
            return new CpuReading(st.OperatingState.ToString(), string.IsNullOrWhiteSpace(st.StateSwitch) ? null : st.StateSwitch, DateTimeOffset.Now);
        }
        catch (IOException) when (_client == null) { throw; }
        catch (Exception ex)
        {
            return new CpuReading("Unknown", null, DateTimeOffset.Now, Describe(ex));
        }
    }

    // Text lists turn "@2%t#519K@" in system diagnostic texts into words. They belong to the program, so they are kept
    // across reconnects; loaded once (again after a failure 10 minutes later, or when the program changed)
    private S7CommPlusTextListCatalog? _textLists;
    private DateTimeOffset _textListsNext = DateTimeOffset.MinValue;
    private volatile bool _textListsStale;

    /// <summary>
    /// The CPU's active alarms with their texts (English when the CPU has it). Read-only. Read over the extra-values
    /// connection: the alarm and text-list services answer slowly on some CPUs, and a timeout there must not cost the
    /// polling connection.
    /// </summary>
    public IReadOnlyList<PlcKi.Core.Diagnostics.CpuAlarm>? ReadCpuAlarms()
    {
        if (_client == null) return null;
        if (_textListsStale) { _textListsStale = false; _textLists = null; _textListsNext = DateTimeOffset.MinValue; }
        if (_textLists == null && DateTimeOffset.Now >= _textListsNext)
        {
            _textListsNext = DateTimeOffset.Now.AddMinutes(10);
            try
            {
                _textLists = _extras.Run("loading the CPU's text lists", l => ((S7ExtraLink)l).Client.GetTextListsAsync().GetAwaiter().GetResult(), out var why);
                if (why != null) _log.LogDebug("No text lists from the CPU yet: {Why}", why);
            }
            catch (Exception ex) { _log.LogDebug(ex, "No text lists from the CPU; diagnostic texts may show placeholders."); }
        }
        var texts0 = _textLists;
        var alarms = _extras.Run("reading the CPU's alarm list", l =>
        {
            var c = ((S7ExtraLink)l).Client;
            return texts0 != null ? c.GetActiveAlarmsAsync(texts0).GetAwaiter().GetResult() : c.GetActiveAlarmsAsync().GetAwaiter().GetResult();
        }, out var error);
        if (alarms == null) throw new IOException(error ?? "The alarm list could not be read.");
        var list = new List<PlcKi.Core.Diagnostics.CpuAlarm>();
        foreach (var a in alarms)
        {
            // English (1033) first, then whatever the CPU has
            var texts = a.AlarmTextsByLanguage.TryGetValue(1033, out var en) ? en
                : a.AlarmTexts ?? a.AlarmTextsByLanguage.Values.FirstOrDefault();
            var extra = texts == null ? new List<string>() : new[]
            {
                texts.AdditionalText1, texts.AdditionalText2, texts.AdditionalText3, texts.AdditionalText4, texts.AdditionalText5,
                texts.AdditionalText6, texts.AdditionalText7, texts.AdditionalText8, texts.AdditionalText9,
            }.Where(t => !string.IsNullOrWhiteSpace(t)).Select(t => t.Trim()).ToList();
            var text = string.IsNullOrWhiteSpace(texts?.AlarmText) ? $"CPU alarm {a.CpuAlarmId}" : texts!.AlarmText.Trim();
            var change = a.StateChange;
            DateTimeOffset? since = change != null && change.Timestamp.Year > 2000 ? new DateTimeOffset(DateTime.SpecifyKind(change.Timestamp, DateTimeKind.Utc)) : null;
            bool? acked = change != null && change.AckTimestamp.Year > 2000 ? true : null;
            list.Add(new PlcKi.Core.Diagnostics.CpuAlarm(a.CpuAlarmId, text, string.IsNullOrWhiteSpace(texts?.Infotext) ? null : texts!.Infotext.Trim(),
                extra, a.AlarmClassName, a.AlarmDomain, a.HmiInfo?.Priority ?? 0, since, acked));
        }
        return list;
    }

    /// <summary>
    /// Symbols outside the model (Tags, Program and Hardware pages), over the extra-values connection. A name the PLC does not
    /// have says "Not on the PLC" and is not asked again for a while, reconnects included (ExtraReader).
    /// </summary>
    public Dictionary<string, (object? Value, string? Error)> ReadSymbols(IReadOnlyList<TagDef> symbols)
    {
        if (_client == null) return symbols.ToDictionary(s => s.Symbol, _ => ((object?)null, (string?)"Not connected to the PLC."));
        return _extras.Read(symbols, _budget ??= new LookupBudget(MaxLookupsPerRead, LookupTime));
    }

    public void Disconnect() => Disconnect(null);

    /// <summary>Closes both connections and the subscriptions. What was learned about the program is kept.</summary>
    private void Disconnect(string? why)
    {
        StopSubscriptions();
        _extras.Close();
        var c = _client;
        _client = null;
        if (c == null) return;
        if (why != null)
        {
            LastDropReason = why;
            _log.LogWarning("Closing the PLC connection to {Address}: {Why}", _opt.Address, why);
        }
        Close(c);
    }

    public void Dispose() => Disconnect(null);

    /// <summary>Exception message plus inner messages, which usually hold the useful part.</summary>
    private static string Describe(Exception ex)
    {
        var parts = new List<string>();
        for (var e = ex; e != null && parts.Count < 4; e = e.InnerException)
            if (!string.IsNullOrWhiteSpace(e.Message) && !parts.Contains(e.Message)) parts.Add(e.Message);
        return string.Join(" -> ", parts);
    }
}

/// <summary>Connected, but the PLC's program does not fit the model (or there is none). Retrying fast will not help.</summary>
public sealed class PlcProgramException : IOException
{
    public PlcProgramException(string message) : base(message) { }
}
