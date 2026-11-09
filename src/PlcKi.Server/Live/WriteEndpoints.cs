using System.Text.Json;
using PlcKi.Core.Status;
using PlcKi.Core.Storage;
using PlcKi.Plant;
using PlcKi.Server.Hardware;
using PlcKi.Server.Jobs;

namespace PlcKi.Server.Live;

public sealed record WriteRequest(string? Symbol, string? Value, string? Reason, string? Confirm, bool? Understood = null);

/// <summary>
/// One line of the change log (value-changes.jsonl in the data folder): every change, and every refused one.
/// Action: null or "change" (a one-time change), "hold", "release", "release-all" or "changed-outside" (Hold on PLCSIM).
/// Lines written before holds existed have no Action and read as changes.
/// Rule: the row of ChangeRules that allowed it ("plcsim-safety-input", "real-memory", ...); older lines have none.
/// </summary>
public sealed record WriteLogEntry(DateTimeOffset Time, string User, string Plc, string Target, string Symbol, string Value,
    string? Before, string? After, bool? Held, string? Reason, bool Ok, string? Error, string? Action = null, string? Address = null,
    string? Rule = null);

/// <summary>
/// Changing a value in the PLC from the Tags and Program pages. Administrators only (/api/admin).
/// What may be changed is decided in ChangeRules (shared with Hold); in short:
///   PLCSIM, confirmed by the TIA helper: inputs, outputs, memory and data block values, also safety inputs and
///             values the safety program reads, after "I understand" under the warning.
///   Real PLC: only when its address is on Settings' "real PLCs whose values may be changed" list, with a reason,
///             and with the PLC address typed again; standard memory and data block values only.
///   Never: F-blocks and their data (also on PLCSIM), or without a loaded program.
/// When PLC-Ki cannot confirm that the PLC is PLCSIM, it treats it as a real PLC. Every change, and every refused
/// one, is written to the change log (with the rule that allowed it) and shows in the machine's events.
/// </summary>
public static class WriteEndpoints
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>What the page needs before it asks: may this be changed, and how careful must it be.</summary>
    private sealed record Plan(string Target, string? Refused, string? Address, string? PlcsimWhy, string Plc, string? Rule = null, string? Warning = null);

    public static WebApplication MapWrites(this WebApplication app)
    {
        var api = app.MapGroup("/api/admin/write");     // administrators only (UsePlant)

        api.MapGet("/check", async (string? symbol, PlcKiOptions opt, ProgramService program, HardwareService hardware, TiaConnection tia, LiveState live,
            SettingsStore settings, CancellationToken ct) =>
        {
            var p = await PlanAsync(symbol ?? "", opt, program, hardware, tia, live, settings, false, ct);
            return Results.Ok(new
            {
                symbol,
                plc = p.Plc,
                target = p.Target,
                allowed = p.Refused == null,
                why = p.Refused,
                address = p.Address,
                plcsimWhy = p.PlcsimWhy,
                warning = p.Warning,
                rule = p.Rule,
                needsReason = p.Target == "real",
                needsConfirm = p.Target == "real",
            });
        });

        // What this PLC-Ki may change right now, for the top bar of every page. Reads only: it asks the same
        // questions as a change would (minus the ones about one tag) and changes nothing.
        //   values: "none" | "plcsim" | "real" (a real PLC on the allow list); startStop: PLCSIM Start/Stop works
        api.MapGet("/state", async (PlcKiOptions opt, TiaConnection tia, LiveState live, SettingsStore settings, CancellationToken ct) =>
        {
            var plc = opt.Plc.Address.Trim();
            var plcsim = await PlcKi.Server.Hardware.HardwareEndpoints.PlcsimAsync(opt, tia, false, ct);
            bool sim = (await PlcsimConfirm.CheckAsync(opt, tia, live, false, ct)).NotConfirmed == null;   // confirmed, as a change needs
            string values = !opt.Source.Equals("s7", StringComparison.OrdinalIgnoreCase) || opt.Plc.Via == "link" || plc.Length == 0 ? "none"
                : sim ? "plcsim" : RealAllowed(settings, plc) ? "real" : "none";
            return Results.Ok(new { values, startStop = plcsim["applies"] is true && plcsim["found"] is true, plc });
        });

        api.MapPost("", async (WriteRequest req, PlcKiOptions opt, ProgramService program, HardwareService hardware, TiaConnection tia, LiveState live,
            SettingsStore settings, PlantContext ctx, MachineHistory history, PlcWriter writer, HttpContext http, ILoggerFactory lf, CancellationToken ct) =>
        {
            var user = http.User.Identity?.Name ?? "?";
            var symbol = (req.Symbol ?? "").Trim();
            var value = (req.Value ?? "").Trim();
            var reason = string.IsNullOrWhiteSpace(req.Reason) ? null : req.Reason.Trim();
            var log = lf.CreateLogger("write");
            var p = await PlanAsync(symbol, opt, program, hardware, tia, live, settings, true, ct);   // fresh: PLCSIM confirmed now

            string? refused = p.Refused;
            if (refused == null && value.Length == 0) refused = "Type the new value.";
            if (refused == null && p.Warning != null && req.Understood != true) refused = p.Warning + " Click \"I understand\" to change it anyway.";
            if (refused == null && p.Target == "real")
            {
                if (reason == null || reason.Length < 5) refused = "Say why you change this value on a real machine (at least a few words). It goes into the change log.";
                else if (!string.Equals((req.Confirm ?? "").Trim(), opt.Plc.Address.Trim(), StringComparison.OrdinalIgnoreCase))
                    refused = $"Type the PLC address {opt.Plc.Address} to confirm the change on the real PLC.";
            }
            if (refused != null)
            {
                Append(ctx, new WriteLogEntry(DateTimeOffset.Now, user, p.Plc, p.Target, symbol, value, null, null, null, reason, false, refused));
                return Results.Json(new { error = refused }, statusCode: 409);
            }

            try
            {
                var r = await writer.WriteAsync(symbol, value, ct);
                var where = p.Target == "plcsim" ? "PLCSIM" : "the real PLC " + p.Plc;
                log.LogWarning("{User} changed {Symbol} on {Where} from {Before} to {Written} (now {After}). Reason: {Reason}",
                    user, symbol, where, r.Before, r.Written, r.After ?? "?", reason ?? "-");
                Append(ctx, new WriteLogEntry(DateTimeOffset.Now, user, p.Plc, p.Target, symbol, r.Written, r.Before, r.After, r.Held, reason, true, null,
                    Address: p.Address, Rule: p.Rule));
                history.Note(new MachineEvent(DateTimeOffset.Now, "write",
                    $"{user} changed {symbol} from {r.Before} to {r.Written} on {where}" + (r.Held ? "." : $", but the program set it back to {r.After}.")
                    + (reason != null ? $" Reason: {reason}" : "")));
                return Results.Ok(new
                {
                    symbol,
                    target = p.Target,
                    before = r.Before,
                    written = r.Written,
                    after = r.After,
                    held = r.Held,
                    type = r.PlcType,
                    note = r.Held ? null : $"The PLC program wrote {r.After} again right away: it sets this value every cycle, so the change does not stay. Look at where it is written (Program page) to see what drives it.",
                });
            }
            catch (WriteException ex)
            {
                Append(ctx, new WriteLogEntry(DateTimeOffset.Now, user, p.Plc, p.Target, symbol, value, null, null, null, reason, false, ex.Message));
                return Results.Json(new { error = ex.Message }, statusCode: ex.Status);
            }
        });

        api.MapGet("/log", (PlantContext ctx, int? max) => Results.Ok(ReadLog(ctx, Math.Clamp(max ?? 50, 1, 500))));
        return app;
    }

    private static async Task<Plan> PlanAsync(string symbol, PlcKiOptions opt, ProgramService program, HardwareService hardware, TiaConnection tia,
        LiveState live, SettingsStore settings, bool fresh, CancellationToken ct)
    {
        var plc = opt.Plc.Address.Trim();
        if (string.IsNullOrWhiteSpace(symbol)) return new("none", "No tag given.", null, null, plc);
        if (!opt.Source.Equals("s7", StringComparison.OrdinalIgnoreCase))
            return new("none", "PLC-Ki runs on test values, not a PLC, so there is nothing to change.", null, null, plc);
        if (opt.Plc.Via == "link")
            return new("none", "The PLC is read through PLC-Ki Link. Changing values through Link is not built yet.", null, null, plc);
        if (plc.Length == 0) return new("none", "No PLC address is set yet (Setup, Connections).", null, null, plc);

        var (notConfirmed, _) = await PlcsimConfirm.CheckAsync(opt, tia, live, fresh, ct);
        var target = notConfirmed == null ? ChangeTarget.Plcsim : RealAllowed(settings, plc) ? ChangeTarget.RealAllowed : ChangeTarget.RealNotAllowed;
        var (hw, source, _) = hardware.Load();
        var d = ChangeRules.Decide(program.Loaded.Plc, source == "hardware" ? hw : null, symbol, ChangeKind.Change, target, notConfirmed);
        return new(target == ChangeTarget.Plcsim ? "plcsim" : "real", d.Refused, d.Address,
            notConfirmed == null ? null : ChangeRules.CannotConfirm(notConfirmed), plc, d.Rule, d.Warning);
    }

    /// <summary>Is this real PLC on Settings' list of real PLCs whose values may be changed? (Read live: no restart needed.)</summary>
    private static bool RealAllowed(SettingsStore settings, string plc) =>
        (settings.Current().PlcAllowWrites ?? "").Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Contains(plc, StringComparer.OrdinalIgnoreCase);

    /// <summary>The change log: value-changes.jsonl in the data folder, with its older files next to it (see JsonLinesLog).</summary>
    public static JsonLinesLog ChangeLog(string dataDir) => JsonLinesLog.For(Path.Combine(dataDir, "value-changes.jsonl"));

    public static void Append(PlantContext ctx, WriteLogEntry e) => Append(ctx.DataDir, e);

    public static void Append(string dataDir, WriteLogEntry e) =>
        ChangeLog(dataDir).Append(JsonSerializer.Serialize(e, Json), e.Time);

    /// <summary>The newest entries first, across the live file and the older (rotated) ones.</summary>
    public static List<WriteLogEntry> ReadLog(PlantContext ctx, int max) => ReadLog(ctx.DataDir, max);

    public static List<WriteLogEntry> ReadLog(string dataDir, int max)
    {
        var list = new List<WriteLogEntry>();
        foreach (var line in ChangeLog(dataDir).NewestFirst())
        {
            if (list.Count >= max) break;
            if (Parse(line) is { } e) list.Add(e);
        }
        return list;
    }

    /// <summary>One line of the change log; null for a broken line (cut off by a power loss).</summary>
    public static WriteLogEntry? Parse(string line)
    {
        try { return JsonSerializer.Deserialize<WriteLogEntry>(line, Json); }
        catch (JsonException) { return null; }
    }
}
