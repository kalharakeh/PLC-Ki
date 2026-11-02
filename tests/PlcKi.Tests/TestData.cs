using System.Text.Json;
using PlcKi.Core.Model;
using PlcKi.Core.Status;

/// <summary>The demo model and the snapshot files from real watch tables, shared by the tests.</summary>
public static class TestData
{
    public static readonly MachineModel Model = MachineModel.Load(Path.Combine(AppContext.BaseDirectory, "models", "demo-mixing-station.json"));
    public static readonly StatusEngine Engine = new(Model);

    public static Snapshot Snapshot(string name, DateTimeOffset? time = null)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "models", "snapshots", name + ".json");
        var doc = JsonDocument.Parse(File.ReadAllText(path));
        var tags = Model.Tags.ToDictionary(t => t.Id);
        var snap = new Snapshot { Time = time ?? DateTimeOffset.Now };
        foreach (var p in doc.RootElement.EnumerateObject())
        {
            if (!tags.TryGetValue(p.Name, out var t)) continue;
            object? raw = p.Value.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                JsonValueKind.Number => p.Value.GetDouble(),
                JsonValueKind.String => p.Value.GetString(),
                _ => null,
            };
            snap.Values[p.Name] = TagValue.Normalize(t, raw);
        }
        return snap;
    }

    public static MachineStatus Evaluate(Snapshot snap, string state = "connected") =>
        Engine.Evaluate(snap, new PlcLinkInfo(state, "192.168.10.10", null, snap.Time, Model.Tags.Count, 0));

    public static MachineStatus Status(string name) => Evaluate(Snapshot(name), "snapshot");
}
