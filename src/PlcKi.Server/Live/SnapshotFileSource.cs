using System.Text.Json;
using PlcKi.Core.Model;
using PlcKi.Core.Status;

namespace PlcKi.Server.Live;

/// <summary>
/// Test source without a PLC: values come from a JSON file such as
///   { "Step": 10, "StepTime": "T#27S", "Level": 16.2, "\"DB_IO\".EStop_OK": true }
/// Keys are tag ids or full symbols. The file is read again on every poll, so it can be edited live.
/// </summary>
public sealed class SnapshotFileSource : IPlcSource
{
    private readonly string _path;
    private IReadOnlyList<TagDef> _tags = Array.Empty<TagDef>();
    private bool _connected;

    public SnapshotFileSource(string path) => _path = path;

    public string Kind => "snapshot";
    public string Address => Path.GetFileName(_path);
    public bool IsConnected => _connected;

    public Dictionary<string, string> Connect(IReadOnlyList<TagDef> tags)
    {
        if (!File.Exists(_path)) throw new FileNotFoundException($"Snapshot file {_path} not found.");
        _tags = tags;
        _connected = true;
        return new Dictionary<string, string>();
    }

    public Snapshot Read()
    {
        var doc = JsonDocument.Parse(File.ReadAllText(_path), new JsonDocumentOptions
        {
            CommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
        });
        var bySymbol = _tags.ToDictionary(t => t.Symbol, t => t.Id, StringComparer.Ordinal);
        var byId = _tags.ToDictionary(t => t.Id, StringComparer.Ordinal);
        var snap = new Snapshot { Time = DateTimeOffset.Now };

        foreach (var prop in doc.RootElement.EnumerateObject())
        {
            var id = byId.ContainsKey(prop.Name) ? prop.Name : bySymbol.GetValueOrDefault(prop.Name);
            if (id == null) continue;
            object? raw = prop.Value.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                JsonValueKind.Number => prop.Value.GetDouble(),
                JsonValueKind.String => prop.Value.GetString(),
                _ => null,
            };
            snap.Values[id] = TagValue.Normalize(byId[id], raw);
        }
        return snap;
    }

    /// <summary>Test files may say the CPU mode: "$cpu": "Run" (or "Stop"), "$switch": "RUN".</summary>
    public CpuReading? ReadCpu()
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(_path), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            var r = doc.RootElement;
            var state = r.TryGetProperty("$cpu", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() : null;
            if (state == null) return null;
            var sw = r.TryGetProperty("$switch", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString() : null;
            return new CpuReading(state, sw, DateTimeOffset.Now);
        }
        catch (Exception) { return null; }
    }

    /// <summary>
    /// Symbols (drive values, the Tags page) come from the same file. The full symbol is tried first;
    /// otherwise the model tag with that symbol (exact, then ignoring case) supplies its id as the key.
    /// </summary>
    public Dictionary<string, (object? Value, string? Error)> ReadSymbols(IReadOnlyList<TagDef> symbols)
    {
        var result = new Dictionary<string, (object?, string?)>();
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(_path), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            foreach (var s in symbols)
            {
                if (!TryFindValue(doc.RootElement, s.Symbol, out var v)) { result[s.Symbol] = (null, "Not in the test file."); continue; }
                object? raw = v.ValueKind switch
                {
                    JsonValueKind.True => true, JsonValueKind.False => false,
                    JsonValueKind.Number => v.GetDouble(), JsonValueKind.String => v.GetString(), _ => null,
                };
                result[s.Symbol] = (raw, null);
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException) { foreach (var s in symbols) result[s.Symbol] = (null, ex.Message); }
        return result;
    }

    private bool TryFindValue(JsonElement root, string symbol, out JsonElement value)
    {
        if (root.TryGetProperty(symbol, out value)) return true;
        var tag = _tags.FirstOrDefault(t => string.Equals(t.Symbol, symbol, StringComparison.Ordinal))
               ?? _tags.FirstOrDefault(t => string.Equals(t.Symbol, symbol, StringComparison.OrdinalIgnoreCase));
        if (tag != null && root.TryGetProperty(tag.Id, out value)) return true;
        value = default;
        return false;
    }

    /// <summary>
    /// Test files may hold CPU alarms (system diagnostics) too:
    ///   "$cpuAlarms": [ { "text": "IO device failure", "info": "...", "extra": ["G120_Conveyor"], "domain": 1 } ]
    /// </summary>
    public IReadOnlyList<PlcKi.Core.Diagnostics.CpuAlarm>? ReadCpuAlarms()
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(_path), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            if (!doc.RootElement.TryGetProperty("$cpuAlarms", out var arr) || arr.ValueKind != JsonValueKind.Array) return null;
            var list = new List<PlcKi.Core.Diagnostics.CpuAlarm>();
            ulong n = 0;
            foreach (var a in arr.EnumerateArray())
            {
                string? S(string k) => a.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
                var extra = a.TryGetProperty("extra", out var ex) && ex.ValueKind == JsonValueKind.Array
                    ? ex.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).ToList() : new List<string>();
                var domain = a.TryGetProperty("domain", out var d) && d.TryGetInt32(out var di) ? di : 1;
                list.Add(new PlcKi.Core.Diagnostics.CpuAlarm(++n, S("text") ?? "CPU alarm", S("info"), extra, S("class"), domain, 0, DateTimeOffset.Now, null));
            }
            return list;
        }
        catch (Exception) { return null; }
    }

    public void Disconnect() => _connected = false;
    public void Dispose() => Disconnect();
}
