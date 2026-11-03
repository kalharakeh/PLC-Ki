using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using PlcKi.Core.Model;

namespace PlcKi.Plant;

/// <summary>What the settings page shows and sends. Passwords and keys are write-only: never sent back.</summary>
public sealed class PlantSettings
{
    public string Source { get; set; } = "s7";
    public string Address { get; set; } = "";
    public string Security { get; set; } = "Auto";
    public string Username { get; set; } = "";
    public int PollMs { get; set; } = 1000;
    public int TimeoutMs { get; set; } = 5000;
    public string Model { get; set; } = "";
    public string SnapshotFile { get; set; } = "";
    public double HistoryMinutes { get; set; } = 15;
    public bool AskEnabled { get; set; } = true;
    public string AskEffort { get; set; } = "medium";
    public int Port { get; set; } = 5080;
    public bool AllowRemote { get; set; }
    public bool PublicView { get; set; }
    public string Export { get; set; } = "";
    /// <summary>An administrator picked the setup (Get started or Connections), so the Getting started list counts it as done.</summary>
    public bool SetupChosen { get; set; }

    /// <summary>"direct" or "link" (the PLC is read by PLC-Ki Link on the user's laptop).</summary>
    public string PlcVia { get; set; } = "direct";
    /// <summary>Where TIA is: "local", "remote" or "link".</summary>
    public string TiaMode { get; set; } = "remote";
    public string TiaUrl { get; set; } = "";
    public string TiaImporterPath { get; set; } = "";
    public string TiaPlcsimIp { get; set; } = "";
    public string TiaAllowReal { get; set; } = "";
    public string TiaProject { get; set; } = "";
    /// <summary>New token for TiaImporter serve. Null or empty keeps the saved one.</summary>
    public string? TiaToken { get; set; }
    public bool ClearTiaToken { get; set; }

    /// <summary>New PLC password. Null or empty keeps the saved one.</summary>
    public string? PlcPassword { get; set; }
    public bool ClearPlcPassword { get; set; }
    /// <summary>New Claude API key. Null or empty keeps the saved one.</summary>
    public string? ApiKey { get; set; }
    public bool ClearApiKey { get; set; }

    /// <summary>The settings that only take effect after a restart (everything but PublicView).</summary>
    internal string RestartKey() => string.Join('|', Source, Address, Security, Username, PollMs, TimeoutMs, Model,
        SnapshotFile, HistoryMinutes, AskEnabled, AskEffort, Port, AllowRemote, Export, PlcVia,
        TiaMode, TiaUrl, TiaImporterPath, TiaPlcsimIp, TiaAllowReal);
}

/// <summary>
/// settings.json in the data folder: the values changed on the settings page. It sits on top of
/// appsettings.json; environment variables and command-line options still win over it.
/// Secrets (PLC password, Claude API key) are stored encrypted with ASP.NET Data Protection;
/// on Windows its keys are in turn encrypted with DPAPI for this machine.
/// </summary>
public sealed class SettingsStore
{
    public const string SecretsSection = "PlantSecrets";
    private const string Purpose = "PlcKi.Plant.Secrets.v1";
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    private readonly PlantContext _ctx;
    private readonly IConfiguration _config;
    private readonly IDataProtector _protector;
    private readonly object _lock = new();

    public SettingsStore(PlantContext ctx, IConfiguration config, IDataProtectionProvider dp)
    {
        _ctx = ctx;
        _config = config;
        _protector = dp.CreateProtector(Purpose);
    }

    public static IDataProtector Protector(IDataProtectionProvider dp) => dp.CreateProtector(Purpose);

    /// <summary>The values in force (appsettings + settings.json + environment + command line).</summary>
    public PlantSettings Current()
    {
        var c = _config;
        var urls = c["Urls"] ?? c["urls"] ?? "";
        return new PlantSettings
        {
            Source = c["PlcKi:Source"] ?? "s7",
            Address = c["PlcKi:Plc:Address"] ?? "",
            Security = c["PlcKi:Plc:Security"] ?? "Auto",
            Username = c["PlcKi:Plc:Username"] ?? "",
            PollMs = c.GetValue("PlcKi:Plc:PollMs", 1000),
            TimeoutMs = c.GetValue("PlcKi:Plc:TimeoutMs", 5000),
            Model = c["PlcKi:Model"] ?? "",
            SnapshotFile = c["PlcKi:SnapshotFile"] ?? "",
            HistoryMinutes = c.GetValue("PlcKi:HistoryMinutes", 15.0),
            AskEnabled = c.GetValue("PlcKi:Ask:Enabled", true),
            AskEffort = c["PlcKi:Ask:Effort"] ?? "medium",
            Port = c.GetValue("Plant:Port", PortFromUrls(urls) ?? 5080),
            AllowRemote = c.GetValue("Plant:AllowRemote", urls.Contains("*") || urls.Contains("0.0.0.0") || urls.Contains("[::]")),
            PublicView = c.GetValue("Plant:PublicView", false),
            Export = c["PlcKi:Export"] ?? "",
            PlcVia = c["PlcKi:Plc:Via"] ?? "direct",
            TiaMode = c["PlcKi:Tia:Mode"] ?? "remote",
            TiaUrl = c["PlcKi:Tia:Url"] ?? "",
            TiaImporterPath = c["PlcKi:Tia:ImporterPath"] ?? "",
            TiaPlcsimIp = c["PlcKi:Tia:PlcsimIp"] ?? "",
            TiaAllowReal = c["PlcKi:Tia:AllowReal"] ?? "",
            TiaProject = c["PlcKi:Tia:Project"] ?? "",
            // Settings saved before this flag existed were saved by someone who set PLC-Ki up
            SetupChosen = c.GetValue("Plant:SetupChosen", File.Exists(_ctx.SettingsFile)),
        };
    }

    public void Save(PlantSettings s)
    {
        Validate(s);
        lock (_lock)
        {
            var before = Current();
            var root = Load();
            var plcki = Obj(root, "PlcKi");
            var plc = Obj(plcki, "Plc");
            var ask = Obj(plcki, "Ask");
            var plant = Obj(root, "Plant");
            var tia = Obj(plcki, "Tia");
            var secrets = Obj(root, SecretsSection);

            plcki["Source"] = s.Source;
            plcki["Model"] = s.Model;
            plcki["SnapshotFile"] = s.SnapshotFile;
            plcki["HistoryMinutes"] = s.HistoryMinutes;
            plcki["Export"] = s.Export?.Trim() ?? "";
            plc["Via"] = s.PlcVia;
            tia["Mode"] = s.TiaMode;
            tia["Url"] = s.TiaUrl?.Trim() ?? "";
            tia["ImporterPath"] = s.TiaImporterPath?.Trim() ?? "";
            tia["PlcsimIp"] = s.TiaPlcsimIp?.Trim() ?? "";
            tia["AllowReal"] = s.TiaAllowReal?.Trim() ?? "";
            tia["Project"] = s.TiaProject?.Trim() ?? "";
            tia.Remove("Token");                    // never in plain text: see PlantSecrets
            plc["Address"] = s.Address.Trim();
            plc["Security"] = s.Security;
            plc["Username"] = s.Username.Trim();
            plc["PollMs"] = s.PollMs;
            plc["TimeoutMs"] = s.TimeoutMs;
            plc.Remove("Password");                 // never in plain text: see PlantSecrets
            ask["Enabled"] = s.AskEnabled;
            ask["Effort"] = s.AskEffort;
            plant["Port"] = s.Port;
            plant["AllowRemote"] = s.AllowRemote;
            plant["PublicView"] = s.PublicView;
            plant["SetupChosen"] = s.SetupChosen || before.SetupChosen;
            root["Urls"] = $"http://{(s.AllowRemote ? "*" : "localhost")}:{s.Port}";

            bool secretChanged = false;
            if (s.ClearPlcPassword) { secretChanged |= secrets.Remove("PlcPassword"); _ctx.PlcPasswordInSettings = false; }
            else if (!string.IsNullOrEmpty(s.PlcPassword)) { secrets["PlcPassword"] = _protector.Protect(s.PlcPassword); secretChanged = true; _ctx.PlcPasswordInSettings = true; }
            if (s.ClearApiKey) { secretChanged |= secrets.Remove("AnthropicApiKey"); _ctx.ApiKeyInSettings = false; }
            else if (!string.IsNullOrWhiteSpace(s.ApiKey)) { secrets["AnthropicApiKey"] = _protector.Protect(s.ApiKey.Trim()); secretChanged = true; _ctx.ApiKeyInSettings = true; }

            if (s.ClearTiaToken) { secretChanged |= secrets.Remove("TiaToken"); }
            else if (!string.IsNullOrWhiteSpace(s.TiaToken)) { secrets["TiaToken"] = _protector.Protect(s.TiaToken.Trim()); secretChanged = true; }
            _ctx.TiaTokenInSettings = secrets.ContainsKey("TiaToken");

            AtomicFile.Save(_ctx.SettingsFile, root.ToJsonString(Json));
            if (secretChanged || before.RestartKey() != s.RestartKey()) _ctx.RestartNeeded = true;
            (_config as IConfigurationRoot)?.Reload();
        }
    }

    private JsonObject Load()
    {
        if (!File.Exists(_ctx.SettingsFile)) return new JsonObject();
        return JsonNode.Parse(File.ReadAllText(_ctx.SettingsFile),
            documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }) as JsonObject
            ?? new JsonObject();
    }

    private static JsonObject Obj(JsonObject parent, string name)
    {
        if (parent[name] is JsonObject o) return o;
        var created = new JsonObject();
        parent[name] = created;
        return created;
    }

    public void Validate(PlantSettings s)
    {
        if (s.Source is not ("s7" or "snapshot")) throw new PlantException("Source is 's7' (live PLC) or 'snapshot' (test file).");
        var addr = s.Address?.Trim() ?? "";
        if (addr.Length == 0 || (!IPAddress.TryParse(addr, out _) && Uri.CheckHostName(addr) == UriHostNameType.Unknown))
            throw new PlantException("Enter the PLC address as an IP address (for example 192.168.0.10) or a host name.");
        if (s.Security is not ("Auto" or "Tls" or "LegacyChallenge")) throw new PlantException("Security is Auto, Tls or LegacyChallenge.");
        if (s.PollMs is < 100 or > 60_000) throw new PlantException("Read interval: 100 to 60000 ms.");
        if (s.TimeoutMs is < 500 or > 60_000) throw new PlantException("Timeout: 500 to 60000 ms.");
        if (s.HistoryMinutes is < 1 or > 240) throw new PlantException("History: 1 to 240 minutes.");
        if (s.AskEffort is not ("low" or "medium" or "high" or "max")) throw new PlantException("Effort is low, medium, high or max.");
        if (s.Port is < 1 or > 65535) throw new PlantException("Port: 1 to 65535.");
        if (string.IsNullOrWhiteSpace(s.Model)) throw new PlantException("Choose a machine model.");
        var model = _ctx.ResolveAppPath(s.Model);
        if (!File.Exists(model)) throw new PlantException($"The machine model file was not found: {s.Model}");
        try { MachineModel.Load(model); }
        catch (Exception ex) { throw new PlantException($"The machine model cannot be read: {ex.Message}"); }
        if (s.Source == "snapshot" && !File.Exists(_ctx.ResolveAppPath(s.SnapshotFile ?? "")))
            throw new PlantException($"The test values file was not found: {s.SnapshotFile}");
        if (s.PlcVia is not ("direct" or "link")) throw new PlantException("The PLC is read directly or through PLC-Ki Link.");
        if (s.TiaMode is not ("local" or "remote" or "link")) throw new PlantException("TIA is on this PC, on another PC, or reached through PLC-Ki Link.");
        if (s.TiaMode == "remote" && !string.IsNullOrWhiteSpace(s.TiaUrl) && !Uri.TryCreate(s.TiaUrl.Trim(), UriKind.Absolute, out _))
            throw new PlantException("Enter the TIA PC as an address like http://192.168.10.3:5090.");
        if (s.TiaToken is { Length: > 0 } tok && tok.Trim().Length < 8) throw new PlantException("The TIA helper's token needs at least 8 characters.");
        if (!string.IsNullOrWhiteSpace(s.Export) && !File.Exists(_ctx.ResolveAppPath(s.Export.Trim())) && !Directory.Exists(_ctx.ResolveAppPath(s.Export.Trim())))
            throw new PlantException($"The program export was not found: {s.Export}");
        if (s.ApiKey is { Length: > 0 } key && !key.Trim().StartsWith("sk-", StringComparison.Ordinal))
            throw new PlantException("That does not look like a Claude API key (they start with sk-).");
    }

    private static int? PortFromUrls(string urls)
    {
        var first = urls.Split(';', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        if (first == null) return null;
        var colon = first.LastIndexOf(':');
        return colon > 0 && int.TryParse(first[(colon + 1)..].TrimEnd('/'), out var p) ? p : null;
    }
}
