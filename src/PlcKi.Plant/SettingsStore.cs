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
    /// <summary>Fast capture: the PLC sends every change, sampled this often (ms). 0 = off.</summary>
    public int FastMs { get; set; } = 100;
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
    internal string RestartKey() => string.Join('|', Source, Address, Security, Username, PollMs, FastMs, TimeoutMs, Model,
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
            FastMs = c.GetValue("PlcKi:Plc:FastMs", 100),
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

    /// <summary>
    /// A restart is needed when settings were saved here, or when what is saved now differs from what is running
    /// (for example settings.json was changed by another copy of PLC-Ki or by hand).
    /// </summary>
    public bool RestartNeeded() => _ctx.RestartNeeded || (_ctx.Running != null && _ctx.Running.RestartKey() != Current().RestartKey());

    // ---- which program export belongs to which model ----
    private string ExportMapFile => Path.Combine(_ctx.DataDir, "model-exports.json");
    private string Full(string p) => Path.GetFullPath(_ctx.ResolveAppPath(p.Trim()));

    /// <summary>The export a model was built from: recorded by the import, or named in the model file itself.</summary>
    public string? ExportFor(string model)
    {
        if (string.IsNullOrWhiteSpace(model)) return null;
        try
        {
            if (File.Exists(ExportMapFile) && JsonNode.Parse(File.ReadAllText(ExportMapFile)) is JsonObject map)
                foreach (var (k, v) in map)
                    if (string.Equals(Full(k), Full(model), StringComparison.OrdinalIgnoreCase) && v?.GetValue<string>() is { Length: > 0 } e) return e;
        }
        catch (Exception) { /* a broken map is ignored */ }
        try { return MachineModel.Load(_ctx.ResolveAppPath(model)).Export is { Length: > 0 } x ? x : null; }
        catch (Exception) { return null; }
    }

    /// <summary>Remember that this model was built from this export (called by the import).</summary>
    public void LinkExport(string model, string export)
    {
        lock (_lock)
        {
            JsonObject map;
            try { map = File.Exists(ExportMapFile) ? JsonNode.Parse(File.ReadAllText(ExportMapFile)) as JsonObject ?? new() : new(); }
            catch (Exception) { map = new(); }
            map[model] = export;
            Directory.CreateDirectory(_ctx.DataDir);
            File.WriteAllText(ExportMapFile, map.ToJsonString(Json));
        }
    }

    /// <summary>
    /// Saves a new program export that the program pages already show (they reload it themselves), so this change
    /// alone does not ask for a restart.
    /// </summary>
    public void SaveExportLive(string export)
    {
        var s = Current();
        var restartWas = _ctx.RestartNeeded;
        s.Export = export;
        Save(s);
        _ctx.RestartNeeded = restartWas;
        if (_ctx.Running != null) _ctx.Running.Export = Current().Export;   // what settings.json (or a command-line option over it) says now
    }

    public void Save(PlantSettings s)
    {
        // Another model was picked but the export was left as it was: take the model's own export, so Code and
        // Logic never show program A next to model B. No known export: none, and the pages say so.
        var was = Current();
        if (!string.Equals(s.Model?.Trim(), was.Model?.Trim(), StringComparison.Ordinal) && string.Equals(s.Export?.Trim() ?? "", was.Export?.Trim() ?? "", StringComparison.Ordinal))
            s.Export = ExportFor(s.Model ?? "") ?? "";
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
            plc["FastMs"] = s.FastMs;
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
        // Empty is allowed: "not set yet" on a fresh install
        if (AddressProblem(s.Address) is { } bad) throw new PlantException(bad);
        if (s.Security is not ("Auto" or "Tls" or "LegacyChallenge")) throw new PlantException("Security is Auto, Tls or LegacyChallenge.");
        if (s.PollMs is < 100 or > 60_000) throw new PlantException("Read every: 0.1 to 60 seconds (100 to 60000 ms).");
        if (s.FastMs is not 0 and (< 10 or > 10_000)) throw new PlantException("Fast capture: 0 (off) or 10 to 10000 ms.");
        if (s.TimeoutMs is < 500 or > 60_000) throw new PlantException("Wait for an answer: 0.5 to 60 seconds (500 to 60000 ms).");
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
            throw new PlantException("Enter the TIA PC as an address like http://192.168.0.20:5090.");
        if (s.TiaToken is { Length: > 0 } tok && tok.Trim().Length < 8) throw new PlantException("The TIA helper's token needs at least 8 characters.");
        if (!string.IsNullOrWhiteSpace(s.Export) && !File.Exists(_ctx.ResolveAppPath(s.Export.Trim())) && !Directory.Exists(_ctx.ResolveAppPath(s.Export.Trim())))
            throw new PlantException($"The program export was not found: {s.Export}");
        if (s.ApiKey is { Length: > 0 } key && !key.Trim().StartsWith("sk-", StringComparison.Ordinal))
            throw new PlantException("That does not look like a Claude API key (they start with sk-).");
    }

    /// <summary>Why a PLC address is not usable, in plain words; null when it is fine or empty.</summary>
    public static string? AddressProblem(string? address)
    {
        var addr = address?.Trim() ?? "";
        if (addr.Length == 0) return null;
        // Digits and dots: must be a full IPv4 address (999.1.1 would otherwise be looked up as a host name)
        if (System.Text.RegularExpressions.Regex.IsMatch(addr, @"^[\d.]+$"))
        {
            var parts = addr.Split('.');
            return parts.Length == 4 && parts.All(p => p.Length is > 0 and <= 3 && int.Parse(p) <= 255) ? null
                : $"{addr} is not an IP address. It has four numbers from 0 to 255 with dots between them, for example 192.168.0.10.";
        }
        return IPAddress.TryParse(addr, out _) || Uri.CheckHostName(addr) != UriHostNameType.Unknown ? null
            : $"{addr} is not an IP address or a computer name. Use the address from TIA (Device configuration › PROFINET interface), for example 192.168.0.10.";
    }

    private static int? PortFromUrls(string urls)
    {
        var first = urls.Split(';', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        if (first == null) return null;
        var colon = first.LastIndexOf(':');
        return colon > 0 && int.TryParse(first[(colon + 1)..].TrimEnd('/'), out var p) ? p : null;
    }
}
