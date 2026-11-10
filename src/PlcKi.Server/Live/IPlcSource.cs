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

    /// <summary>
    /// Reads extra symbols someone is looking at (the Tags page), outside the model: symbol -> (value, error).
    /// Sources that cannot look up arbitrary symbols say so per symbol.
    /// </summary>
    Dictionary<string, (object? Value, string? Error)> ReadSymbols(IReadOnlyList<TagDef> symbols) =>
        symbols.ToDictionary(s => s.Symbol, s => ((object?)null, (string?)"Live values for tags outside the machine model need a direct PLC connection."));

    /// <summary>
    /// The CPU's operating mode (RUN, STOP ...) and mode switch, read-only, for the PLC page. Sources that cannot
    /// tell return null. Called every few seconds from the polling loop, after Read.
    /// </summary>
    CpuReading? ReadCpu() => null;

    /// <summary>
    /// The CPU's active alarms, its system diagnostics included (PROFINET device failure, module and channel faults,
    /// drive faults). Read-only. Sources that cannot tell return null. Called every few seconds from the polling loop.
    /// </summary>
    IReadOnlyList<PlcKi.Core.Diagnostics.CpuAlarm>? ReadCpuAlarms() => null;

    /// <summary>
    /// The blocks in the PLC (name, kind, language), read-only, to see whether they still match the imported program.
    /// Sources that cannot list blocks return null (no check); a list that cannot be read right now throws.
    /// Called after a connect and then every 10 minutes.
    /// </summary>
    IReadOnlyList<PlcKi.Core.Status.PlcBlock>? BrowseBlocks() => null;

    /// <summary>Model tags the PLC sends every change for (fast capture); the normal reads feed the recording for the rest.</summary>
    IReadOnlySet<string> FastTagIds => NoTags;
    static readonly IReadOnlySet<string> NoTags = new HashSet<string>();

    void Disconnect();
}

/// <summary>What the CPU says about itself: State = Run, Stop, Startup, Halt, Defective, Unknown; Switch = RUN/STOP when it has one.</summary>
public sealed record CpuReading(string State, string? Switch, DateTimeOffset Time, string? Error = null);

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
    /// <summary>The program export (TiaImporter export, zip or folder) the model was built from: Program page.</summary>
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
    /// <summary>
    /// Fast capture: the PLC samples the model tags this often (ms) and sends every change, like an HMI subscription.
    /// Kept for the last minute and cut into a capture around each alarm. 0 = off (only the normal reads are recorded).
    /// </summary>
    public int FastMs { get; set; } = 100;
    public int TimeoutMs { get; set; } = 5000;
    /// <summary>"direct" = this PC reads the PLC; "link" = PLC-Ki Link on the user's laptop reads it and sends the values here.</summary>
    public string Via { get; set; } = "direct";
}
