using System.Text.Json;
using PlcKi.Core.Status;
using PlcKi.Plant;
using PlcKi.Server.Jobs;

namespace PlcKi.Server.Live;

public sealed record WriteRequest(string? Symbol, string? Value, string? Reason, string? Confirm);

/// <summary>
/// One line of the change log (value-changes.jsonl in the data folder): every change, and every refused one.
/// Action: null or "change" (a one-time change), "hold", "release", "release-all" or "changed-outside" (Hold on PLCSIM).
/// Lines written before holds existed have no Action and read as changes.
/// </summary>
public sealed record WriteLogEntry(DateTimeOffset Time, string User, string Plc, string Target, string Symbol, string Value,
    string? Before, string? After, bool? Held, string? Reason, bool Ok, string? Error, string? Action = null, string? Address = null);

/// <summary>
/// Changing a value in the PLC from the Tags and Program pages. Administrators only (/api/admin).
///   PLCSIM (the PLC PLC-Ki reads is the TIA helper's PLCSIM CPU): any value, no extra questions.
///   Real PLC: only when its address is on Settings' "real PLCs whose values may be changed" list, with a reason,
///             and with the PLC address typed again; never inputs or outputs.
///   Never: a value the safety program uses, F-blocks and their data (also on PLCSIM), or without a loaded program.
/// When PLC-Ki cannot tell that the PLC is PLCSIM, it treats it as a real PLC. Every change, and every refused
/// one, is written to the change log and shows in the machine's events.
/// </summary>
public static class WriteEndpoints
{
    private static readonly object LogLock = new();
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>What the page needs before it asks: may this be changed, and how careful must it be.</summary>
    private sealed record Plan(string Target, string? Refused, string? Address, string? PlcsimWhy, string Plc);

    public static WebApplication MapWrites(this WebApplication app)
    {
        var api = app.MapGroup("/api/admin/write");     // administrators only (UsePlant)

        api.MapGet("/check", async (string? symbol, PlcKiOptions opt, ProgramService program, TiaConnection tia, SettingsStore settings, CancellationToken ct) =>
        {
            var p = await PlanAsync(symbol ?? "", opt, program, tia, settings, ct);
            return Results.Ok(new
            {
                symbol,
                plc = p.Plc,
                target = p.Target,
                allowed = p.Refused == null,
                why = p.Refused,
                address = p.Address,
                plcsimWhy = p.PlcsimWhy,
                needsReason = p.Target == "real",
                needsConfirm = p.Target == "real",
            });
        });

        // What this PLC-Ki may change right now, for the top bar of every page. Reads only: it asks the same
        // questions as a change would (minus the ones about one tag) and changes nothing.
        //   values: "none" | "plcsim" | "real" (a real PLC on the allow list); startStop: PLCSIM Start/Stop works
        api.MapGet("/state", async (PlcKiOptions opt, TiaConnection tia, SettingsStore settings, CancellationToken ct) =>
        {
            var plc = opt.Plc.Address.Trim();
            var plcsim = await PlcKi.Server.Hardware.HardwareEndpoints.PlcsimAsync(opt, tia, false, ct);
            bool sim = plcsim["applies"] is true;
            string values = !opt.Source.Equals("s7", StringComparison.OrdinalIgnoreCase) || opt.Plc.Via == "link" || plc.Length == 0 ? "none"
                : sim ? "plcsim" : RealAllowed(settings, plc) ? "real" : "none";
            return Results.Ok(new { values, startStop = sim && plcsim["found"] is true, plc });
        });

        api.MapPost("", async (WriteRequest req, PlcKiOptions opt, ProgramService program, TiaConnection tia, SettingsStore settings,
            PlantContext ctx, MachineHistory history, PlcWriter writer, HttpContext http, ILoggerFactory lf, CancellationToken ct) =>
        {
            var user = http.User.Identity?.Name ?? "?";
            var symbol = (req.Symbol ?? "").Trim();
            var value = (req.Value ?? "").Trim();
            var reason = string.IsNullOrWhiteSpace(req.Reason) ? null : req.Reason.Trim();
            var log = lf.CreateLogger("write");
            var p = await PlanAsync(symbol, opt, program, tia, settings, ct);

            string? refused = p.Refused;
            if (refused == null && value.Length == 0) refused = "Type the new value.";
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
                Append(ctx, new WriteLogEntry(DateTimeOffset.Now, user, p.Plc, p.Target, symbol, r.Written, r.Before, r.After, r.Held, reason, true, null));
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

    private static async Task<Plan> PlanAsync(string symbol, PlcKiOptions opt, ProgramService program, TiaConnection tia, SettingsStore settings, CancellationToken ct)
    {
        var plc = opt.Plc.Address.Trim();
        if (string.IsNullOrWhiteSpace(symbol)) return new("none", "No tag given.", null, null, plc);
        if (!opt.Source.Equals("s7", StringComparison.OrdinalIgnoreCase))
            return new("none", "PLC-Ki runs on test values, not a PLC, so there is nothing to change.", null, null, plc);
        if (opt.Plc.Via == "link")
            return new("none", "The PLC is read through PLC-Ki Link. Changing values through Link is not built yet.", null, null, plc);
        if (plc.Length == 0) return new("none", "No PLC address is set yet (Setup, Connections).", null, null, plc);

        var loaded = program.Loaded.Plc;
        var address = WriteRules.Address(loaded, symbol);
        var plcsim = await PlcKi.Server.Hardware.HardwareEndpoints.PlcsimAsync(opt, tia, false, ct);
        var target = plcsim["applies"] is true ? "plcsim" : "real";
        var plcsimWhy = target == "real" ? plcsim["why"] as string : null;

        if (WriteRules.SafetyReason(loaded, symbol) is { } safety) return new(target, safety, address, plcsimWhy, plc);
        if (target == "real")
        {
            if (!RealAllowed(settings, plc))
                return new(target, $"{plc} is a real PLC (or PLC-Ki cannot tell that it is PLCSIM), and it is not on the list of real PLCs whose values may be changed. " +
                    "An administrator can add it under Setup, Connections, More PLC options.", address, plcsimWhy, plc);
            if (WriteRules.RealIoReason(address) is { } io) return new(target, io, address, plcsimWhy, plc);
        }
        return new(target, null, address, plcsimWhy, plc);
    }

    /// <summary>Is this real PLC on Settings' list of real PLCs whose values may be changed? (Read live: no restart needed.)</summary>
    private static bool RealAllowed(SettingsStore settings, string plc) =>
        (settings.Current().PlcAllowWrites ?? "").Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Contains(plc, StringComparer.OrdinalIgnoreCase);

    private static string LogFile(PlantContext ctx) => Path.Combine(ctx.DataDir, "value-changes.jsonl");

    internal static void Append(PlantContext ctx, WriteLogEntry e)
    {
        lock (LogLock) File.AppendAllText(LogFile(ctx), JsonSerializer.Serialize(e, Json) + Environment.NewLine);
    }

    /// <summary>The newest entries first.</summary>
    internal static List<WriteLogEntry> ReadLog(PlantContext ctx, int max)
    {
        string[] lines;
        lock (LogLock) lines = File.Exists(LogFile(ctx)) ? File.ReadAllLines(LogFile(ctx)) : Array.Empty<string>();
        var list = new List<WriteLogEntry>();
        for (int i = lines.Length - 1; i >= 0 && list.Count < max; i--)
        {
            try { if (JsonSerializer.Deserialize<WriteLogEntry>(lines[i], Json) is { } e) list.Add(e); }
            catch (JsonException) { /* a broken line is skipped */ }
        }
        return list;
    }
}
