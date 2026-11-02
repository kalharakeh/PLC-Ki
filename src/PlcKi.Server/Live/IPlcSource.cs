using PlcKi.Core.Model;
using PlcKi.Core.Status;

namespace PlcKi.Server.Live;

/// <summary>
/// Where tag values come from. Read-only by design: there is no write method,
/// and the driver's write gate stays closed.
/// Implementations are used from one thread only (the polling loop).
/// </summary>
public interface IPlcSource : IDisposable
{
    /// <summary>"s7" or "snapshot".</summary>
    string Kind { get; }
    string Address { get; }
    bool IsConnected { get; }

    /// <summary>Connects and looks up every tag. Tags that cannot be found are reported, not fatal.</summary>
    /// <returns>Tag id -> reason, for tags that could not be found.</returns>
    Dictionary<string, string> Connect(IReadOnlyList<TagDef> tags);

    /// <summary>Reads all tags once. Throws when the connection is lost.</summary>
    Snapshot Read();

    void Disconnect();
}

public sealed class PlcKiOptions
{
    /// <summary>Path to the machine model JSON.</summary>
    public string Model { get; set; } = "models/demo-mixing-station.json";
    /// <summary>"s7" = live PLC over S7CommPlus, "snapshot" = values from a JSON file (testing without a PLC).</summary>
    public string Source { get; set; } = "s7";
    public string SnapshotFile { get; set; } = "models/snapshots/step10-xv101-not-open.json";
    public PlcOptions Plc { get; set; } = new();
}

public sealed class PlcOptions
{
    public string Address { get; set; } = "192.168.10.10";
    /// <summary>Only needed when the CPU is protected. Better set with the PLCKI_PLC_PASSWORD environment variable.</summary>
    public string Password { get; set; } = "";
    public string Username { get; set; } = "";
    /// <summary>Auto (TLS, then the older challenge login), Tls, or LegacyChallenge.</summary>
    public string Security { get; set; } = "Auto";
    public int PollMs { get; set; } = 1000;
    public int TimeoutMs { get; set; } = 5000;
}
