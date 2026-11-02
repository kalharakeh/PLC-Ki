using PlcKi.Core.Model;
using PlcKi.Core.Status;
using S7CommPlusDriver;
using S7CommPlusDriver.ClientApi;

namespace PlcKi.Server.Live;

/// <summary>
/// Reads tags by symbol over S7CommPlus (the protocol TIA Portal itself uses), with TLS.
/// Needs S7-1500 firmware 2.9+ or S7-1200 4.3+. Nothing has to be set up in the PLC program.
/// Only Connect, symbol lookup and read are called; the driver copy has no write functions.
/// </summary>
public sealed class S7CommPlusSource : IPlcSource
{
    private readonly PlcOptions _opt;
    private readonly ILogger _log;
    private S7CommPlusConnection? _conn;
    private readonly List<(TagDef Def, PlcTag Tag)> _tags = new();

    public S7CommPlusSource(PlcOptions opt, ILogger log)
    {
        _opt = opt;
        _log = log;
    }

    public string Kind => "s7";
    public string Address => _opt.Address;
    public bool IsConnected => _conn != null;

    public Dictionary<string, string> Connect(IReadOnlyList<TagDef> tags)
    {
        Disconnect();
        var password = Environment.GetEnvironmentVariable("PLCKI_PLC_PASSWORD") ?? _opt.Password;
        var conn = new S7CommPlusConnection();
        int res = conn.Connect(_opt.Address, password ?? "", _opt.Username ?? "", _opt.TimeoutMs);
        if (res != 0)
            throw new IOException($"Connection to {_opt.Address} failed ({DescribeError(res)}).");

        var missing = new Dictionary<string, string>();
        _tags.Clear();
        foreach (var def in tags)
        {
            try
            {
                var tag = conn.getPlcTagBySymbol(def.Symbol);
                if (tag == null) missing[def.Id] = $"symbol {def.Symbol} not found in the PLC";
                else _tags.Add((def, tag));
            }
            catch (Exception ex)
            {
                missing[def.Id] = $"symbol {def.Symbol}: {ex.Message}";
            }
        }
        _conn = conn;
        _log.LogInformation("Connected to {Address}: {Found} of {Total} tags found.", _opt.Address, _tags.Count, tags.Count);
        return missing;
    }

    public Snapshot Read()
    {
        var conn = _conn ?? throw new InvalidOperationException("Not connected.");
        var snap = new Snapshot { Time = DateTimeOffset.Now };
        if (_tags.Count == 0) return snap;

        int res = conn.ReadTags(_tags.Select(t => t.Tag));
        if (res != 0)
        {
            Disconnect();
            throw new IOException($"Read failed ({DescribeError(res)}).");
        }

        foreach (var (def, tag) in _tags)
        {
            if ((tag.Quality & PlcTagQC.TAG_QUALITY_MASK) != PlcTagQC.TAG_QUALITY_GOOD)
            {
                snap.Errors[def.Id] = $"bad quality 0x{tag.Quality:X2}, error 0x{tag.LastReadError:X}";
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
        var c = _conn;
        _conn = null;
        if (c == null) return;
        try { c.Disconnect(); }
        catch (Exception ex) { _log.LogDebug(ex, "Disconnect error ignored."); }
    }

    public void Dispose() => Disconnect();

    private static string DescribeError(int code)
    {
        // A few common S7 client error codes; the rest is shown as hex.
        return code switch
        {
            0x00000001 => "could not create the TCP socket",
            0x00000002 => "TCP connection timeout: the PLC does not answer, check IP address and network",
            0x00000003 => "TCP connection refused or failed: check the IP address",
            0x00000004 => "receive timeout: the PLC stopped answering",
            0x00000005 => "receive error",
            0x00000006 => "send timeout",
            0x00000007 => "send error",
            0x00000008 => "connection reset by the PLC",
            0x00000009 => "not connected",
            0x00002751 => "host unreachable: check the network route to the PLC",
            0x00010000 => "ISO connection error",
            0x00030000 => "invalid answer from the PLC (firmware too old, or access protection: try a password)",
            _ => $"error 0x{code:X}",
        };
    }
}
