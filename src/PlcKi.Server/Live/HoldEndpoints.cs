using PlcKi.Core.Status;
using PlcKi.Plant;
using PlcKi.Server.Hardware;
using PlcKi.Server.Jobs;

namespace PlcKi.Server.Live;

public sealed record HoldRequest(string? Symbol, string? Value, string? Reason, bool? Understood = null);
public sealed record ReleaseRequest(string? Symbol, bool? All);

/// <summary>
/// "Hold on PLCSIM": an input set through the PLCSIM Advanced API stays until it is set again, unlike a one-time
/// change of %I, which the next cycle overwrites. This is not a CPU force: TIA's force table does not see it, and
/// it cannot hold an output. Administrators only (/api/admin/hold); the rules are ChangeRules (shared with
/// "Change a value"): PLCSIM only, confirmed by the TIA helper (a running instance at that address, RUN/STOP
/// agreeing); inputs only; safety inputs after "I understand" under the warning.
/// Every hold, release and refusal goes into the change log (value-changes.jsonl) and the machine's events.
/// GET /api/held (any logged-in user) is the list for the banner on every page.
/// </summary>
public static class HoldEndpoints
{
    private sealed record Plan(string Plc, string? Refused, IoAddress? Address, string Datatype, string? Instance, string? Rule = null, string? Warning = null);

    public static WebApplication MapHolds(this WebApplication app)
    {
        app.MapGet("/api/held", (PlcsimInputs inputs) =>
        {
            var held = inputs.Held;
            return Results.Ok(new
            {
                count = held.Count,
                items = held.Select(h => new { h.Symbol, h.Address, h.Value, h.Before, h.User, h.Time, h.Instance }),
            });
        });

        var api = app.MapGroup("/api/admin/hold");     // administrators only (UsePlant)

        api.MapGet("/check", async (string? symbol, PlcKiOptions opt, ProgramService program, HardwareService hardware, TiaConnection tia,
            LiveState live, PlcsimInputs inputs, CancellationToken ct) =>
        {
            var s = (symbol ?? "").Trim();
            var p = await PlanAsync(s, opt, program, hardware, tia, live, ct);
            string? now = null, refused = p.Refused;
            if (refused == null)
            {
                try { now = await inputs.ReadAsync(p.Plc, p.Address!, p.Datatype, ct); }
                catch (Exception ex) when (ex is InvalidOperationException or HttpRequestException or TaskCanceledException or JobException)
                { refused = "PLC-Ki cannot read this input from PLCSIM: " + ex.Message; }
            }
            var held = inputs.Find(s);
            return Results.Ok(new
            {
                symbol = s, plc = p.Plc, allowed = refused == null, why = refused, address = p.Address?.Text, datatype = p.Datatype,
                instance = p.Instance, now, warning = p.Warning, rule = p.Rule, held = held == null ? null : new { held.Value, held.Before, held.User, held.Time },
            });
        });

        api.MapGet("", async (PlcsimInputs inputs, PlantContext ctx, HttpContext http, CancellationToken ct) =>
        {
            try
            {
                var (kept, changed) = await inputs.CheckAsync(ct);
                foreach (var (h, now) in changed)
                    WriteEndpoints.Append(ctx, new WriteLogEntry(DateTimeOffset.Now, http.User.Identity?.Name ?? "?", h.Plc, "plcsim", h.Symbol, now, h.Value, now,
                        null, "Changed outside PLC-Ki: no longer held", true, null, "changed-outside", h.Address));
                return Results.Ok(new
                {
                    checkedNow = true,
                    items = kept.Select(k => new { k.Held.Symbol, k.Held.Address, k.Held.Datatype, k.Held.Value, k.Held.Before, k.Held.User, k.Held.Time, k.Held.Instance, k.Held.Reason, now = k.Now }),
                    changed = changed.Select(c => new { c.Held.Symbol, c.Held.Address, c.Held.Value, now = c.Now }),
                });
            }
            catch (Exception ex) when (ex is InvalidOperationException or HttpRequestException or TaskCanceledException or JobException)
            {
                // PLCSIM not reachable: show the list as PLC-Ki remembers it
                return Results.Ok(new
                {
                    checkedNow = false, error = ex.Message,
                    items = inputs.Held.Select(h => new { h.Symbol, h.Address, h.Datatype, h.Value, h.Before, h.User, h.Time, h.Instance, h.Reason, now = (string?)null }),
                    changed = Array.Empty<object>(),
                });
            }
        });

        api.MapPost("", async (HoldRequest req, PlcKiOptions opt, ProgramService program, HardwareService hardware, TiaConnection tia, LiveState live,
            PlcsimInputs inputs, PlantContext ctx, MachineHistory history, HttpContext http, ILoggerFactory lf, CancellationToken ct) =>
        {
            var user = http.User.Identity?.Name ?? "?";
            var symbol = (req.Symbol ?? "").Trim();
            var text = (req.Value ?? "").Trim();
            var reason = string.IsNullOrWhiteSpace(req.Reason) ? null : req.Reason.Trim();
            var p = await PlanAsync(symbol, opt, program, hardware, tia, live, ct);
            var refused = p.Refused ?? (text.Length == 0 ? "Type the value to hold."
                : p.Warning != null && req.Understood != true ? p.Warning + " Click \"I understand\" to hold it anyway." : null);
            object? value = null;
            if (refused == null)
            {
                try { value = WriteRules.Parse(text, HoldRules.ClrType(p.Datatype)); }
                catch (FormatException ex) { refused = ex.Message; }
            }
            if (refused != null)
            {
                WriteEndpoints.Append(ctx, new WriteLogEntry(DateTimeOffset.Now, user, p.Plc, "plcsim", symbol, text, null, null, null, reason, false, refused, "hold", p.Address?.Text));
                return Results.Json(new { error = refused }, statusCode: 409);
            }
            try
            {
                var (before, now) = await inputs.HoldAsync(p.Plc, p.Instance!, symbol, p.Address!, p.Datatype, value!, user, reason, ct);
                var shown = WriteRules.Show(value);
                lf.CreateLogger("hold").LogWarning("{User} holds {Symbol} ({Address}) at {Value} on PLCSIM {Instance} (was {Before}, reads {Now}). Reason: {Reason}",
                    user, symbol, p.Address!.Text, shown, p.Instance, before, now, reason ?? "-");
                WriteEndpoints.Append(ctx, new WriteLogEntry(DateTimeOffset.Now, user, p.Plc, "plcsim", symbol, shown, before, now, now == shown, reason, true, null, "hold", p.Address.Text,
                    p.Rule));
                history.Note(new MachineEvent(DateTimeOffset.Now, "write",
                    $"{user} holds the input {symbol} ({p.Address.Text}) at {shown} on PLCSIM (was {before})." + (reason != null ? $" Reason: {reason}" : "")));
                return Results.Ok(new { symbol, address = p.Address.Text, before, value = shown, now, instance = p.Instance });
            }
            catch (Exception ex) when (ex is InvalidOperationException or HttpRequestException or TaskCanceledException or JobException)
            {
                WriteEndpoints.Append(ctx, new WriteLogEntry(DateTimeOffset.Now, user, p.Plc, "plcsim", symbol, text, null, null, null, reason, false, ex.Message, "hold", p.Address?.Text));
                return Results.Json(new { error = "PLCSIM did not take the input: " + ex.Message }, statusCode: 502);
            }
        });

        // Release one ({symbol}) or all ({all: true}): each input gets back the value it had before the hold
        api.MapPost("/release", async (ReleaseRequest req, PlcsimInputs inputs, PlantContext ctx, MachineHistory history, HttpContext http, CancellationToken ct) =>
        {
            var user = http.User.Identity?.Name ?? "?";
            var all = req.All == true;
            var list = all ? inputs.Held.ToList() : inputs.Find((req.Symbol ?? "").Trim()) is { } one ? new List<HeldInput> { one } : new();
            if (list.Count == 0) return Results.Json(new { error = all ? "Nothing is held." : "This input is not held by PLC-Ki." }, statusCode: 404);
            var results = new List<object>();
            foreach (var h in list)
            {
                try
                {
                    var (now, note) = await inputs.ReleaseAsync(h, ct);
                    WriteEndpoints.Append(ctx, new WriteLogEntry(DateTimeOffset.Now, user, h.Plc, "plcsim", h.Symbol, h.Before, h.Value, now, null, note, true, null,
                        all ? "release-all" : "release", h.Address));
                    history.Note(new MachineEvent(DateTimeOffset.Now, "write", $"{user} released the input {h.Symbol} ({h.Address}) on PLCSIM" +
                        (note == null ? $": back to {h.Before}." : $". {note}")));
                    results.Add(new { h.Symbol, h.Address, ok = true, before = h.Value, now, note });
                }
                catch (Exception ex) when (ex is InvalidOperationException or HttpRequestException or TaskCanceledException or JobException or FormatException)
                {
                    WriteEndpoints.Append(ctx, new WriteLogEntry(DateTimeOffset.Now, user, h.Plc, "plcsim", h.Symbol, h.Before, h.Value, null, null, null, false, ex.Message,
                        all ? "release-all" : "release", h.Address));
                    results.Add(new { h.Symbol, h.Address, ok = false, error = ex.Message });
                }
            }
            return Results.Ok(new { results, left = inputs.Held.Count });
        });
        return app;
    }

    private static async Task<Plan> PlanAsync(string symbol, PlcKiOptions opt, ProgramService program, HardwareService hardware, TiaConnection tia,
        LiveState live, CancellationToken ct)
    {
        var plc = opt.Plc.Address.Trim();
        if (symbol.Length == 0) return new(plc, "No tag given.", null, "", null);
        if (!opt.Source.Equals("s7", StringComparison.OrdinalIgnoreCase))
            return new(plc, "PLC-Ki runs on test values, not a PLC, so there is nothing to hold.", null, "", null);
        if (plc.Length == 0) return new(plc, "No PLC address is set yet (Setup, Connections).", null, "", null);

        var loaded = program.Loaded.Plc;
        var (notConfirmed, instance) = await PlcsimConfirm.CheckAsync(opt, tia, live, true, ct);   // fresh, not the 3 s cache
        var (hw, source, _) = hardware.Load();
        var d = HoldRules.Decide(loaded, source == "hardware" ? hw : null, symbol, notConfirmed);
        var address = HoldRules.ParseAddress(WriteRules.Address(loaded, symbol));
        return new(plc, d.Refused, address, d.Datatype, instance, d.Rule, d.Warning);
    }
}
