using System.Text.Json;
using PlcKi.Core.Status;
using PlcKi.Plant;
using PlcKi.Server.Jobs;

namespace PlcKi.Server.Live;

/// <summary>
/// The only place in PLC-Ki that sets a simulated input of PLCSIM ("Hold on PLCSIM", tests/PlcKi.Tests/HoldTests.cs
/// checks that no other code calls VmClient.WritePlcsimInputAsync). It does not decide whether a hold is allowed:
/// HoldEndpoints does that with HoldRules first. It keeps the list of held inputs in held-inputs.json in the data
/// folder, with the value each had before, because a hold lives in PLCSIM and outlives a restart of PLC-Ki.
/// </summary>
public sealed class PlcsimInputs
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly SemaphoreSlim _one = new(1, 1);
    private readonly TiaConnection _tia;
    private readonly string _file;
    private readonly object _lock = new();
    private List<HeldInput> _held;

    public PlcsimInputs(TiaConnection tia, PlantContext ctx)
    {
        _tia = tia;
        _file = Path.Combine(ctx.DataDir, "held-inputs.json");
        _held = Load(_file);
    }

    /// <summary>The inputs PLC-Ki holds now, oldest first (no PLCSIM call: for the banner on every page).</summary>
    public IReadOnlyList<HeldInput> Held { get { lock (_lock) return _held.ToList(); } }

    public HeldInput? Find(string symbol) { lock (_lock) return _held.FirstOrDefault(h => Same(h.Symbol, symbol)); }

    /// <summary>The input as PLCSIM has it now, shown as its type (TRUE, 1234, 1.5).</summary>
    public async Task<string> ReadAsync(string ip, IoAddress a, string datatype, CancellationToken ct)
    {
        var json = await _tia.Client().ReadPlcsimInputAsync(ip, a.Byte, a.Bit, a.Bit != null ? 1 : a.Size, ct);
        return Value(json, datatype);
    }

    /// <summary>
    /// Sets the simulated input and remembers it. When it is held already, the first "before" is kept, so Release
    /// still puts back the value it had before PLC-Ki touched it. Returns (before, now as read back).
    /// </summary>
    public async Task<(string Before, string Now)> HoldAsync(string ip, string instance, string symbol, IoAddress a, string datatype,
        object value, string user, string? reason, CancellationToken ct)
    {
        await _one.WaitAsync(ct);
        try
        {
            var before = Find(symbol)?.Before ?? await ReadAsync(ip, a, datatype, ct);
            var now = await WriteAsync(ip, a, datatype, value, ct);
            var held = new HeldInput(symbol, a.Text, datatype, WriteRules.Show(value), before, user, DateTimeOffset.Now, ip, instance, reason);
            lock (_lock)
            {
                _held.RemoveAll(h => Same(h.Symbol, symbol));
                _held.Add(held);
                Save();
            }
            return (before, now);
        }
        finally { _one.Release(); }
    }

    /// <summary>
    /// Puts back the value the input had before the hold and forgets the hold. When someone else changed the input
    /// since (not the held value any more), it is left as it is and only forgotten. Returns (now, note).
    /// </summary>
    public async Task<(string? Now, string? Note)> ReleaseAsync(HeldInput h, CancellationToken ct)
    {
        await _one.WaitAsync(ct);
        try
        {
            var a = HoldRules.ParseAddress(h.Address) ?? throw new InvalidOperationException($"{h.Address} is not an input address.");
            var current = await ReadAsync(h.Plc, a, h.Datatype, ct);
            string? note = null;
            string now = current;
            if (current == h.Value)
                now = await WriteAsync(h.Plc, a, h.Datatype, WriteRules.Parse(h.Before, HoldRules.ClrType(h.Datatype)), ct);
            else
                note = $"It was {current} already (changed outside PLC-Ki), so PLC-Ki left it as it is.";
            Forget(h.Symbol);
            return (now, note);
        }
        finally { _one.Release(); }
    }

    /// <summary>Each held input with its value in PLCSIM now; holds changed outside PLC-Ki are forgotten and returned apart.</summary>
    public async Task<(List<(HeldInput Held, string Now)> Kept, List<(HeldInput Held, string Now)> Changed)> CheckAsync(CancellationToken ct)
    {
        var kept = new List<(HeldInput, string)>();
        var changed = new List<(HeldInput, string)>();
        foreach (var h in Held)
        {
            var a = HoldRules.ParseAddress(h.Address);
            if (a == null) { Forget(h.Symbol); continue; }
            var now = await ReadAsync(h.Plc, a, h.Datatype, ct);
            if (now == h.Value) kept.Add((h, now));
            else { changed.Add((h, now)); Forget(h.Symbol); }
        }
        return (kept, changed);
    }

    private async Task<string> WriteAsync(string ip, IoAddress a, string datatype, object value, CancellationToken ct)
    {
        var bytes = HoldRules.Encode(value, datatype);
        var json = a.Bit != null
            ? await _tia.Client().WritePlcsimInputAsync(ip, a.Byte, a.Bit, bytes[0] != 0, null, ct)
            : await _tia.Client().WritePlcsimInputAsync(ip, a.Byte, null, null, Convert.ToHexString(bytes), ct);
        return Value(json, datatype);
    }

    /// <summary>The helper's answer {bit: true} or {data: "00FF"} as the PLC type.</summary>
    private static string Value(string json, string datatype)
    {
        using var doc = JsonDocument.Parse(json);
        var e = doc.RootElement;
        if (e.TryGetProperty("bit", out var bit) && bit.ValueKind is JsonValueKind.True or JsonValueKind.False)
            return HoldRules.Decode(new[] { bit.GetBoolean() ? (byte)1 : (byte)0 }, datatype);
        if (e.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.String)
            return HoldRules.Decode(Convert.FromHexString(data.GetString()!), datatype);
        throw new InvalidOperationException("The TIA helper gave no input value.");
    }

    private void Forget(string symbol)
    {
        lock (_lock) { if (_held.RemoveAll(h => Same(h.Symbol, symbol)) > 0) Save(); }
    }

    private static bool Same(string a, string b) => string.Equals(a.Trim('"', ' '), b.Trim('"', ' '), StringComparison.OrdinalIgnoreCase);

    /// <summary>Writes a temporary file first and swaps it in, so a crash never leaves half a list.</summary>
    private void Save()
    {
        var tmp = _file + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(_held, Json));
        File.Move(tmp, _file, overwrite: true);
    }

    private static List<HeldInput> Load(string file)
    {
        try { return File.Exists(file) ? JsonSerializer.Deserialize<List<HeldInput>>(File.ReadAllText(file), Json) ?? new() : new(); }
        catch (JsonException) { return new(); }
    }
}
