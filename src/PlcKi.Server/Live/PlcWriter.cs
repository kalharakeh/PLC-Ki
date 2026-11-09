using PlcKi.Core.Status;
using S7CommPlusDriver;
using S7CommPlusDriver.ClientApi;

namespace PlcKi.Server.Live;

/// <summary>What one change did: the value before, the value written, and what the PLC holds a moment later.</summary>
public sealed record WriteResult(string Before, string Written, string? After, bool Held, string PlcType);

/// <summary>
/// The only place in PLC-Ki that writes to a PLC (tests/PlcKi.Tests/ReadOnlyTests.cs checks that no other code
/// calls the driver's write functions). It does not decide whether a change is allowed: WriteEndpoints does that
/// (admin, safety program, PLCSIM or a confirmed real PLC) before it calls Write.
/// Each change opens its own short connection with the driver's write gate open, so the polling connection that
/// reads the machine stays read-only. One change at a time.
/// </summary>
public sealed class PlcWriter
{
    private static readonly SemaphoreSlim One = new(1, 1);
    private readonly PlcKiOptions _opt;
    private readonly ILogger<PlcWriter> _log;

    public PlcWriter(PlcKiOptions opt, ILogger<PlcWriter> log)
    {
        _opt = opt;
        _log = log;
    }

    /// <summary>The driver tag types PLC-Ki can change. Arrays, structs, dates and characters are left alone.</summary>
    private static readonly HashSet<string> Writable = new(StringComparer.Ordinal)
    {
        "PlcTagBool", "PlcTagByte", "PlcTagWord", "PlcTagDWord", "PlcTagLWord", "PlcTagSInt", "PlcTagUSInt", "PlcTagInt", "PlcTagUInt",
        "PlcTagDInt", "PlcTagUDInt", "PlcTagLInt", "PlcTagULInt", "PlcTagReal", "PlcTagLReal", "PlcTagTime", "PlcTagString", "PlcTagWString",
    };

    /// <summary>Writes the typed value to the symbol. Throws WriteException with a plain message when it cannot.</summary>
    public async Task<WriteResult> WriteAsync(string symbol, string text, CancellationToken ct)
    {
        if (!await One.WaitAsync(TimeSpan.FromSeconds(20), ct)) throw new WriteException("Another change is still being written. Try again in a moment.", 409);
        try { return await Task.Run(() => Write(symbol, text), ct); }
        finally { One.Release(); }
    }

    private WriteResult Write(string symbol, string text)
    {
        var password = Environment.GetEnvironmentVariable("PLCKI_PLC_PASSWORD") ?? _opt.Plc.Password ?? "";
        var options = new S7CommPlusClientOptions
        {
            Address = _opt.Plc.Address,
            Password = password,
            Username = _opt.Plc.Username ?? "",
            ConnectTimeout = TimeSpan.FromMilliseconds(_opt.Plc.TimeoutMs),
            RequestTimeout = TimeSpan.FromMilliseconds(_opt.Plc.TimeoutMs),
            AutoReconnect = false,
            SecurityMode = S7CommPlusSource.ParseSecurity(_opt.Plc.Security),
            WriteEnabled = true,        // only on this short connection, after WriteEndpoints allowed the change
            Logger = _log,
        };
        var client = new S7CommPlusClient(options);
        S7Connections.Track(client, "change", _log);
        try
        {
            try { client.ConnectAsync().GetAwaiter().GetResult(); }
            catch (Exception ex) { throw new WriteException($"PLC-Ki could not connect to {_opt.Plc.Address} to write: {Describe(ex)}", 502); }

            PlcTag tag;
            try { tag = client.GetTagBySymbolAsync(symbol).GetAwaiter().GetResult(); }
            catch (Exception ex) { throw new WriteException($"{symbol} was not found on the PLC: {Describe(ex)}", 404); }
            var kind = tag.GetType().Name;
            var plcType = kind.StartsWith("PlcTag") ? kind[6..] : kind;
            if (!Writable.Contains(kind)) throw new WriteException($"PLC-Ki cannot change {plcType} values (only single numbers, times, texts and TRUE/FALSE).", 400);
            var prop = tag.GetType().GetProperty("Value") ?? throw new WriteException($"PLC-Ki cannot change {plcType} values.", 400);

            var before = ReadOne(client, tag, prop, "before the change");
            object value;
            try { value = WriteRules.Parse(text, prop.PropertyType, isTime: kind == "PlcTagTime"); }
            catch (FormatException ex) { throw new WriteException(ex.Message, 400); }
            prop.SetValue(tag, value);

            S7CommPlusBatchResult<S7CommPlusWriteResult> result;
            try { result = client.WriteAsync(new[] { tag }).GetAwaiter().GetResult(); }
            catch (Exception ex) { throw new WriteException($"The PLC did not take the value: {Describe(ex)}", 502); }
            var item = result.Items.Count > 0 ? result.Items[0] : default;
            if (result.Items.Count == 0 || !item.IsSuccess)
                throw new WriteException($"The PLC refused the value (error 0x{item.ItemError:X}). It may be write-protected, or the CPU's access level does not allow writing.", 502);

            // Read it back a moment later: if the program writes this value every cycle, the change does not stay
            Thread.Sleep(600);
            string? after = null;
            try { after = ReadOne(client, tag, prop, "after the change"); }
            catch (WriteException ex) { _log.LogDebug("Read back failed: {Error}", ex.Message); }
            var written = WriteRules.Show(value);
            return new WriteResult(before, written, after, after == null || after == written, plcType);
        }
        finally
        {
            try { client.DisconnectAsync().GetAwaiter().GetResult(); } catch (Exception ex) { _log.LogDebug(ex, "Disconnect error ignored."); }
            try { client.DisposeAsync().AsTask().GetAwaiter().GetResult(); } catch (Exception ex) { _log.LogDebug(ex, "Dispose error ignored."); }
        }
    }

    private static string ReadOne(S7CommPlusClient client, PlcTag tag, System.Reflection.PropertyInfo prop, string when)
    {
        S7CommPlusBatchResult<S7CommPlusTagReadResult> r;
        try { r = client.ReadAsync(new[] { tag }).GetAwaiter().GetResult(); }
        catch (Exception ex) { throw new WriteException($"Reading {tag.Name} {when} failed: {Describe(ex)}", 502); }
        if (r.Items.Count == 0 || !r.Items[0].IsSuccess || (tag.Quality & PlcTagQC.TAG_QUALITY_MASK) != PlcTagQC.TAG_QUALITY_GOOD)
            throw new WriteException($"Reading {tag.Name} {when} failed (quality 0x{tag.Quality:X2}).", 502);
        return WriteRules.Show(prop.GetValue(tag));
    }

    private static string Describe(Exception ex)
    {
        var parts = new List<string>();
        for (var e = ex; e != null && parts.Count < 4; e = e.InnerException)
            if (!string.IsNullOrWhiteSpace(e.Message) && !parts.Contains(e.Message)) parts.Add(e.Message);
        return string.Join(" -> ", parts);
    }
}

/// <summary>A change that was refused or failed, with a plain message and an HTTP status for the page.</summary>
public sealed class WriteException : Exception
{
    public int Status { get; }
    public WriteException(string message, int status = 400) : base(message) { Status = status; }
}
