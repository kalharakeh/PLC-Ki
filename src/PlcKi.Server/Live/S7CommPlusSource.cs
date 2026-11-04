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
/// </summary>
public sealed class S7CommPlusSource : IPlcSource
{
    private readonly PlcOptions _opt;
    private readonly ILogger _log;
    private S7CommPlusClient? _client;
    private readonly List<(TagDef Def, PlcTag Tag)> _tags = new();
    private const int LookupFailuresBeforeGivingUp = 3;

    public S7CommPlusSource(PlcOptions opt, ILogger log)
    {
        _opt = opt;
        _log = log;
    }

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
        Disconnect();
        var password = Environment.GetEnvironmentVariable("PLCKI_PLC_PASSWORD") ?? _opt.Password ?? "";
        var options = new S7CommPlusClientOptions
        {
            Address = _opt.Address,
            Password = password,
            Username = _opt.Username ?? "",
            ConnectTimeout = TimeSpan.FromMilliseconds(_opt.TimeoutMs),
            RequestTimeout = TimeSpan.FromMilliseconds(_opt.TimeoutMs),
            AutoReconnect = false,          // the polling loop reconnects
            // WriteEnabled is left at its default (false): PLC-Ki is read-only. The tests forbid setting it.
            SecurityMode = security,
            Logger = _log,
        };
        var client = new S7CommPlusClient(options);
        try
        {
            client.ConnectAsync().GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            try { client.DisposeAsync().AsTask().GetAwaiter().GetResult(); } catch { /* ignore */ }
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
            try { client.DisposeAsync().AsTask().GetAwaiter().GetResult(); } catch { /* ignore */ }
            throw new PlcProgramException($"Connected to {_opt.Address}, but {why}");
        }
        _client = client;
        _log.LogInformation("Connected to {Address} ({Security}): {Found} of {Total} tags found.",
            _opt.Address, NegotiatedSecurity, _tags.Count, tags.Count);
        return missing;
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
        var client = _client ?? throw new InvalidOperationException("Not connected.");
        var snap = new Snapshot { Time = DateTimeOffset.Now };
        if (_tags.Count == 0) return snap;

        S7CommPlusBatchResult<S7CommPlusTagReadResult> result;
        try
        {
            result = client.ReadAsync(_tags.Select(t => t.Tag).ToList()).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            Disconnect();
            throw new IOException($"Read failed: {Describe(ex)}", ex);
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

    /// <summary>Operating mode and mode switch (a read; the driver's mode change is never called, see ReadOnlyTests).</summary>
    public CpuReading? ReadCpu()
    {
        var client = _client;
        if (client == null) return null;
        try
        {
            var st = client.GetCpuStateAsync(CancellationToken.None).GetAwaiter().GetResult();
            return new CpuReading(st.OperatingState.ToString(), string.IsNullOrWhiteSpace(st.StateSwitch) ? null : st.StateSwitch, DateTimeOffset.Now);
        }
        catch (Exception ex)
        {
            return new CpuReading("Unknown", null, DateTimeOffset.Now, Describe(ex));
        }
    }

    // Text lists turn "@2%t#519K@" in system diagnostic texts into words; loaded once per connection
    private S7CommPlusTextListCatalog? _textLists;
    private bool _textListsTried;

    /// <summary>The CPU's active alarms with their texts (English when the CPU has it). Read-only.</summary>
    public IReadOnlyList<PlcKi.Core.Diagnostics.CpuAlarm>? ReadCpuAlarms()
    {
        var client = _client;
        if (client == null) return null;
        if (!_textListsTried)
        {
            _textListsTried = true;
            try { _textLists = client.GetTextListsAsync().GetAwaiter().GetResult(); }
            catch (Exception ex) { _log.LogDebug(ex, "No text lists from the CPU; diagnostic texts may show placeholders."); }
        }
        var alarms = _textLists != null
            ? client.GetActiveAlarmsAsync(_textLists).GetAwaiter().GetResult()
            : client.GetActiveAlarmsAsync().GetAwaiter().GetResult();
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

    /// <summary>Lookups for the Tags page, kept while connected: symbol -> tag, or why it cannot be read.</summary>
    private readonly Dictionary<string, (PlcTag? Tag, string? Error)> _extra = new();
    private const int MaxLookupsPerRead = 30;

    public Dictionary<string, (object? Value, string? Error)> ReadSymbols(IReadOnlyList<TagDef> symbols)
    {
        var result = new Dictionary<string, (object?, string?)>();
        var client = _client;
        if (client == null) { foreach (var s in symbols) result[s.Symbol] = (null, "Not connected to the PLC."); return result; }

        var readable = new List<(TagDef Def, PlcTag Tag)>();
        int lookups = 0;
        foreach (var def in symbols)
        {
            if (!_extra.TryGetValue(def.Symbol, out var found))
            {
                // Each lookup is a round trip (a failed one can even cost a new login): a few per poll, so the
                // model tags keep their pace; the rest follow in the next polls
                if (++lookups > MaxLookupsPerRead) continue;
                try { found = (client.GetTagBySymbolAsync(def.Symbol).GetAwaiter().GetResult(), null); }
                catch (Exception ex) { found = (null, "Not found on the PLC: " + Describe(ex)); }
                _extra[def.Symbol] = found;
            }
            if (found.Tag != null) readable.Add((def, found.Tag)); else result[def.Symbol] = (null, found.Error);
        }
        if (readable.Count == 0) return result;

        var read = client.ReadAsync(readable.Select(r => r.Tag).ToList()).GetAwaiter().GetResult();
        for (int i = 0; i < readable.Count; i++)
        {
            var (def, tag) = readable[i];
            var item = i < read.Items.Count ? read.Items[i] : default;
            if (!item.IsSuccess || (tag.Quality & PlcTagQC.TAG_QUALITY_MASK) != PlcTagQC.TAG_QUALITY_GOOD)
            {
                result[def.Symbol] = (null, $"read error 0x{item.ItemError:X}");
                continue;
            }
            object? raw = tag.GetType().GetProperty("Value")?.GetValue(tag);
            object? value;
            try { value = def.Type is "Bool" or "Real" or "DInt" or "Int" or "Time" ? TagValue.Normalize(def, raw) : raw?.ToString(); }
            catch { value = raw?.ToString(); }
            result[def.Symbol] = (value, null);
        }
        return result;
    }

    public void Disconnect()
    {
        _extra.Clear();
        _textLists = null;
        _textListsTried = false;
        var c = _client;
        _client = null;
        if (c == null) return;
        try { c.DisconnectAsync().GetAwaiter().GetResult(); }
        catch (Exception ex) { _log.LogDebug(ex, "Disconnect error ignored."); }
        try { c.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
        catch (Exception ex) { _log.LogDebug(ex, "Dispose error ignored."); }
    }

    public void Dispose() => Disconnect();

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
