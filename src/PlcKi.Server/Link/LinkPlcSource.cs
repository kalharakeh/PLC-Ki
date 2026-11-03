using System.Text.Json;
using PlcKi.Core.Model;
using PlcKi.Core.Status;
using PlcKi.Server.Live;

namespace PlcKi.Server.Link;

/// <summary>
/// PLC values that PLC-Ki Link reads on the user's laptop and sends here. Read-only like every source:
/// Link uses the same read-only driver, and nothing goes back to the PLC.
/// </summary>
public sealed class LinkPlcSource : IPlcSource
{
    private readonly LinkHub _hub;
    private readonly PlcOptions _opt;
    private IReadOnlyList<TagDef> _tags = Array.Empty<TagDef>();
    private Dictionary<string, TagDef> _byId = new();
    private long _seq;
    private bool _connected;

    public LinkPlcSource(LinkHub hub, PlcOptions opt)
    {
        _hub = hub;
        _opt = opt;
    }

    public string Kind => "link";
    public string Address => _hub.Machine is { Length: > 0 } m ? $"{_opt.Address} via PLC-Ki Link on {m}" : $"{_opt.Address} via PLC-Ki Link";
    public bool IsConnected => _connected;

    public Dictionary<string, string> Connect(IReadOnlyList<TagDef> tags)
    {
        _tags = tags;
        _byId = tags.ToDictionary(t => t.Id, StringComparer.Ordinal);
        _hub.Tags = tags;
        _connected = true;      // the real check is in Read: are values coming in?
        return new Dictionary<string, string>();
    }

    public Snapshot Read()
    {
        var wait = TimeSpan.FromMilliseconds(Math.Max(3000, _opt.PollMs * 3));
        var (v, seq) = _hub.WaitValues(_seq, wait);
        if (v == null)
            throw new InvalidOperationException(_hub.Connected
                ? "PLC-Ki Link is connected but sends no PLC values."
                : "PLC-Ki Link is not connected. Start it on the laptop that reaches the PLC (Setup, Connections).");
        _seq = seq;
        if (!v.Connected)
            throw new InvalidOperationException("The laptop cannot read the PLC: " + (v.Error ?? "no answer") + ".");

        var snap = new Snapshot { Time = v.Time == default ? DateTimeOffset.Now : v.Time };
        foreach (var (id, el) in v.Values ?? new())
        {
            if (!_byId.TryGetValue(id, out var tag)) continue;
            object? raw = el.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                JsonValueKind.Number => el.GetDouble(),
                JsonValueKind.String => el.GetString(),
                _ => null,
            };
            try { snap.Values[id] = TagValue.Normalize(tag, raw); } catch (FormatException) { snap.Values[id] = null; }
        }
        foreach (var (id, why) in v.Errors ?? new()) snap.Errors[id] = why;
        foreach (var t in _tags) snap.Values.TryAdd(t.Id, null);
        return snap;
    }

    public void Disconnect() => _connected = false;
    public void Dispose() => Disconnect();
}
