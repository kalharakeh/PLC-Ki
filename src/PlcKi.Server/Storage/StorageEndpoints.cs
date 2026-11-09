using PlcKi.Core.Model;
using PlcKi.Core.Status;
using PlcKi.Core.Storage;
using PlcKi.Plant;
using PlcKi.Server.Live;

namespace PlcKi.Server.Storage;

/// <summary>PUT /api/admin/storage/limits. DryRun: say what these limits would remove now, change nothing.</summary>
public sealed record LimitsRequest(RetentionLimits? Limits, bool DryRun);
public sealed record ClearRequest(string? Kind);

/// <summary>
/// Setup › Settings › Storage (administrators only: every path is under /api/admin). Reads what is kept, sets the
/// limits, exports, and clears one kind after the page's confirmation sheet.
/// </summary>
public static class StorageEndpoints
{
    public static WebApplication MapStorage(this WebApplication app)
    {
        var api = app.MapGroup("/api/admin/storage");

        api.MapGet("", (StorageService s, MachineHistory history, FastRecorder fast, PlcsimInputs held, PlantContext ctx) =>
        {
            var now = DateTimeOffset.Now;
            return Results.Ok(new
            {
                dataDir = ctx.DataDir,
                disk = s.Disk(),
                limits = s.Limits,
                defaults = RetentionLimits.Default,
                rows = s.Rows(now, history, fast, held.Held.Count),
                lastPrune = s.LastPrune.At,
                lastRemoved = s.LastPrune.Removed,
            });
        });

        // For the setup bar on every page: is the disk nearly full?
        api.MapGet("/disk", (StorageService s) => Results.Ok(s.Disk()));

        api.MapPut("/limits", (LimitsRequest req, StorageService s, SettingsStore settings) =>
        {
            var limits = req.Limits ?? RetentionLimits.Default;
            if (limits.Problem() is { } bad) return Results.Json(new { error = bad }, statusCode: 400);
            var now = DateTimeOffset.Now;
            if (req.DryRun) return Results.Ok(new { removed = s.Prune(now, dryRun: true, limits) });
            settings.SaveRetention(limits);
            return Results.Ok(new { removed = s.Prune(now, limits: limits), limits = s.Limits });
        });

        api.MapPost("/clear", (ClearRequest req, StorageService s, HttpContext http) =>
        {
            var kind = (req.Kind ?? "").Trim();
            if (!StorageService.Clearable.Contains(kind)) return Results.Json(new { error = "This cannot be cleared here." }, statusCode: 400);
            return Results.Ok(s.Clear(kind, http.User.Identity?.Name ?? "?", DateTimeOffset.Now));
        });

        api.MapGet("/production.csv", (StorageService s, MachineModel m, string? from, string? to) =>
        {
            var today = DateOnly.FromDateTime(DateTime.Today);
            var f = DateOnly.TryParse(from, out var a) ? a : today.AddDays(-30);
            var t = DateOnly.TryParse(to, out var b) ? b : today;
            if (t < f) (f, t) = (t, f);
            return Results.File(s.ProductionCsv(f, t), "text/csv; charset=utf-8", $"production-log-{f:yyyy-MM-dd}-to-{t:yyyy-MM-dd}.csv");
        });

        api.MapGet("/changes.csv", (StorageService s) =>
            Results.File(s.ChangesCsv(), "text/csv; charset=utf-8", $"change-log-{DateTime.Today:yyyy-MM-dd}.csv"));

        api.MapGet("/recordings.zip", (StorageService s) =>
        {
            var ms = new MemoryStream();
            s.RecordingsZip(ms);
            ms.Position = 0;
            return Results.File(ms, "application/zip", $"alarm-recordings-{DateTime.Today:yyyy-MM-dd}.zip");
        });
        return app;
    }
}
