using System.Text.Json;
using PlcKi.Plant;
using PlcKi.Server.Jobs;
using PlcKi.Server.Live;

namespace PlcKi.Server.Hardware;

public sealed record CpuModeRequest(string? Mode);

/// <summary>
/// The PLC page (CPU front, mode, LEDs) and the Hardware page (racks, modules, channels with tags).
/// Everything here reads, except POST /api/admin/cpu/mode: it asks the TIA helper to switch its PLCSIM CPU
/// between RUN and STOP. That only works when the PLC PLC-Ki reads IS that PLCSIM CPU (same address as the
/// helper's --plcsim-ip); a real PLC is never stopped or started from PLC-Ki.
/// </summary>
public static class HardwareEndpoints
{
    // The helper's answer is kept a few seconds: the PLC page asks every two seconds
    private static readonly object CacheLock = new();
    private static (DateTime At, Dictionary<string, object?> State)? _plcsim;

    public static WebApplication MapHardware(this WebApplication app)
    {
        app.MapGet("/api/hardware", (HardwareService hw) => Results.Ok(hw.View()));

        app.MapGet("/api/cpu", async (LiveState live, PlcKiOptions opt, HardwareService hw, TiaConnection tia, HttpContext http, CancellationToken ct) =>
        {
            var (config, _, _) = hw.Load();
            var station = config == null ? null : HardwareService.PlcStation(config, opt.Plc.Address);
            var cpu = station?.Modules.FirstOrDefault(m => m.Kind == "cpu");
            var plcsim = await PlcsimAsync(opt, tia, false, ct);
            var admin = http.User.IsInRole(UserStore.Admin);
            var status = live.Status;
            var reading = live.Cpu;
            return Results.Ok(new
            {
                address = opt.Plc.Address,
                connection = status?.Connection.State,
                source = opt.Source,
                mode = reading == null ? null : new { state = reading.State, @switch = reading.Switch, time = reading.Time, error = reading.Error },
                station = station?.Name,
                stationIps = station?.Ips,
                cpu = cpu == null ? null : new { cpu.Name, cpu.Title, cpu.OrderNumber, cpu.Firmware, cpu.Family, cpu.WidthMm, cpu.Safety, cpu.Display, cpu.Ips, cpu.Ports },
                modules = station?.Modules.Count ?? 0,
                alarms = status?.ActiveAlarms.Count ?? 0,
                faults = status?.ActiveAlarms.Count(a => !string.Equals(a.Severity, "WARNING", StringComparison.OrdinalIgnoreCase)) ?? 0,
                plcsim,
                canSwitch = admin && plcsim["applies"] is true && plcsim["found"] is true,
                admin,
            });
        });

        var api = app.MapGroup("/api/admin");     // administrators only (UsePlant)
        api.MapPost("/cpu/mode", async (CpuModeRequest req, PlcKiOptions opt, TiaConnection tia, HttpContext http, ILoggerFactory lf, CancellationToken ct) =>
        {
            var mode = (req.Mode ?? "").Trim().ToLowerInvariant();
            if (mode is not ("run" or "stop")) return Results.Json(new { error = "Say run or stop." }, statusCode: 400);
            var plcsim = await PlcsimAsync(opt, tia, true, ct);
            if (plcsim["applies"] is not true)
                return Results.Json(new { error = (plcsim["why"] as string) ?? "Start and Stop only work on the PLCSIM CPU of the TIA helper." }, statusCode: 409);
            if (plcsim["found"] is not true)
                return Results.Json(new { error = (plcsim["error"] as string) ?? "The PLCSIM CPU was not found." }, statusCode: 409);
            try
            {
                var text = await tia.Client().SwitchPlcsimAsync(mode, opt.Plc.Address.Trim(), ct);
                lf.CreateLogger("cpu").LogWarning("PLCSIM CPU {Address} set to {Mode} by {User}.", opt.Plc.Address, mode.ToUpperInvariant(), http.User.Identity?.Name ?? "?");
                lock (CacheLock) _plcsim = null;
                return Results.Text(text, "application/json");
            }
            catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or TaskCanceledException or JobException)
            {
                return Results.Json(new { error = ex.Message }, statusCode: 502);
            }
        });

        // Quick: only the racks, modules and channels (a minute or two on the TIA PC, not a full program export)
        api.MapPost("/hardware/read", (HardwareService hw, JobService jobs, TiaConnection tia, PlantContext ctx, HttpContext http) =>
        {
            try
            {
                var out_ = hw.SidecarPath ?? throw new JobException("No program is loaded yet. Import the program first (Setup › Program).");
                var archive = ArchiveFor(ctx, hw.ExportPath!) ?? throw new JobException("The TIA archive (.zap20) of this program is not on this PC any more. Upload it again on Setup › Program.");
                tia.Client();   // checks the TIA settings
                var job = jobs.Start("export", $"Read the hardware from {Path.GetFileName(archive)}", http.User.Identity?.Name ?? "?",
                    new[] { "export", "--tia", tia.Url!, "--archive", archive, "--out", out_, "--hardware-only", "yes" },
                    tia.JobEnvironment(), new() { ["hardware"] = out_ });
                return Results.Ok(job.View(0));
            }
            catch (JobException ex) { return Results.Json(new { error = ex.Message }, statusCode: ex.Status); }
        });
        return app;
    }

    /// <summary>The archive the export came from (same name), else the newest archive.</summary>
    private static string? ArchiveFor(PlantContext ctx, string export)
    {
        var dir = Path.Combine(ctx.WorkDir, "archives");
        if (!Directory.Exists(dir)) return null;
        var files = new DirectoryInfo(dir).GetFiles("*.zap*").Where(f => !f.Name.EndsWith(".part")).OrderByDescending(f => f.LastWriteTimeUtc).ToList();
        var stem = Path.GetFileNameWithoutExtension(export.TrimEnd('\\', '/'));
        return (files.FirstOrDefault(f => Path.GetFileNameWithoutExtension(f.Name).Equals(stem, StringComparison.OrdinalIgnoreCase)) ?? files.FirstOrDefault())?.FullName;
    }

    /// <summary>
    /// Is the PLC PLC-Ki reads the PLCSIM CPU of the TIA helper, and what state is it in?
    /// {applies, why, found, state, leds, name, error, plcsimIp}
    /// </summary>
    private static async Task<Dictionary<string, object?>> PlcsimAsync(PlcKiOptions opt, TiaConnection tia, bool fresh, CancellationToken ct)
    {
        lock (CacheLock)
            if (!fresh && _plcsim is { } c && DateTime.UtcNow - c.At < TimeSpan.FromSeconds(3)) return c.State;
        var r = new Dictionary<string, object?> { ["applies"] = false, ["found"] = false };
        if (tia.NotSetUp() is { } why) r["why"] = "No TIA helper is set up, so PLC-Ki cannot tell whether this is PLCSIM. " + why;
        else if (string.IsNullOrWhiteSpace(opt.Plc.Address)) r["why"] = "No PLC address is set yet.";
        else
        {
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromSeconds(4));
                using var doc = JsonDocument.Parse(await tia.Client().PlcsimStateAsync(cts.Token));
                var e = doc.RootElement;
                string? S(string n) => e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
                var ip = S("plcsimIp");
                r["plcsimIp"] = ip;
                r["applies"] = ip != null && ip.Equals(opt.Plc.Address.Trim(), StringComparison.OrdinalIgnoreCase);
                if (r["applies"] is not true)
                    r["why"] = ip == null ? "The TIA helper has no PLCSIM address, so Start and Stop are off."
                        : $"The PLC at {opt.Plc.Address} is not the PLCSIM CPU of the TIA helper ({ip}). PLC-Ki never starts or stops a real PLC.";
                r["found"] = e.TryGetProperty("found", out var f) && f.ValueKind == JsonValueKind.True;
                r["state"] = S("state");
                r["name"] = S("name");
                r["error"] = S("error");
                if (e.TryGetProperty("leds", out var leds) && leds.ValueKind == JsonValueKind.Object)
                    r["leds"] = leds.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.ToString());
            }
            catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or TaskCanceledException or JsonException or JobException)
            {
                r["why"] = ex.Message.Contains("404") ? "The TIA helper is older than this page: update it (Update-TIA-helper on the TIA PC) to get Start and Stop for PLCSIM."
                    : "The TIA helper does not answer, so PLC-Ki cannot tell whether this is PLCSIM: " + ex.Message;
            }
        }
        lock (CacheLock) _plcsim = (DateTime.UtcNow, r);
        return r;
    }
}
