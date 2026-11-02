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

    public void Disconnect()
    {
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
