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

    public void Disconnect() => _connected = false;
    public void Dispose() => Disconnect();
}
