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
    public AskOptions Ask { get; set; } = new();
    /// <summary>How many minutes of tag values to keep in memory for "what happened" questions.</summary>
    public double HistoryMinutes { get; set; } = 15;
    /// <summary>The program export (TiaImporter export, zip or folder) the model was built from: Code and Logic pages.</summary>
    public string Export { get; set; } = "";
    public TiaOptions Tia { get; set; } = new();
}

/// <summary>
/// Where TIA Portal is, for the import page and the block generator. PLC-Ki never talks to TIA itself:
/// "TiaImporter serve" runs next to TIA and does the work.
///   local  = TIA is on this PC; PLC-Ki starts the helper itself (ImporterPath) on localhost
///   remote = TIA is on another PC or a VM that runs "TiaImporter serve" (Url + Token)
///   link   = TIA is on the user's laptop, reached through PLC-Ki Link
/// </summary>
public sealed class TiaOptions
{
    public string Mode { get; set; } = "remote";
    public string Url { get; set; } = "";
    public string Token { get; set; } = "";
    /// <summary>local: path of TiaImporter.exe on this PC.</summary>
    public string ImporterPath { get; set; } = "";
    /// <summary>local: PLCSIM address the helper may download to (empty = no downloads).</summary>
    public string PlcsimIp { get; set; } = "";
    /// <summary>local: real PLC addresses the helper may download to after confirmation at its window (comma-separated).</summary>
    public string AllowReal { get; set; } = "";
    /// <summary>The TIA project (.ap20, path on the TIA PC) the block generator changes by default.</summary>
    public string Project { get; set; } = "";
    public int Port { get; set; } = 5090;
}

/// <summary>The "ask a question" assistant. The API key comes from the ANTHROPIC_API_KEY environment variable only.</summary>
public sealed class AskOptions
{
    public bool Enabled { get; set; } = true;
    public string Model { get; set; } = "claude-opus-5-5";
    /// <summary>low, medium, high or max: how hard Claude thinks before answering. Higher is slower.</summary>
    public string Effort { get; set; } = "medium";
    public int MaxTokens { get; set; } = 8000;
    /// <summary>Upper limit of tool rounds per question, so a question cannot loop forever.</summary>
    public int MaxToolRounds { get; set; } = 12;
}

public sealed class PlcOptions
{
    public string Address { get; set; } = "";
    /// <summary>Only needed when the CPU is protected. Better set with the PLCKI_PLC_PASSWORD environment variable.</summary>
    public string Password { get; set; } = "";
    public string Username { get; set; } = "";
    /// <summary>Auto (TLS, then the older challenge login), Tls, or LegacyChallenge.</summary>
    public string Security { get; set; } = "Auto";
    public int PollMs { get; set; } = 1000;
    public int TimeoutMs { get; set; } = 5000;
    /// <summary>"direct" = this PC reads the PLC; "link" = PLC-Ki Link on the user's laptop reads it and sends the values here.</summary>
    public string Via { get; set; } = "direct";
}
