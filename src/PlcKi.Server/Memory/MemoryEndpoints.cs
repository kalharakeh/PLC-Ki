using System.Globalization;
using System.Text;
using PlcKi.Core.Memory;
using PlcKi.Core.Model;

namespace PlcKi.Server.Memory;

/// <summary>Body of POST /api/memory/occurrences/{id}/note. All three empty removes the note.</summary>
public sealed record NoteRequest(string? Cause, string? Fix, string? Text);

/// <summary>
/// The machine memory for the pages: which alarms happen most, each occurrence with its context, and the note
/// a technician adds ("what caused it, what fixed it"). Reading is open to whoever may see the pages; adding a
/// note needs a login, so every note has a name on it.
/// </summary>
public static class MemoryEndpoints
{
    public static object Dto(AlarmOccurrence o) => new
    {
        o.Id, o.AlarmId, o.Text, o.Severity, o.Location, o.Came, o.Went,
        duration = o.Duration is { } d ? AlarmMemory.Span(d) : null,
        o.AtStart, o.EndUnknown, o.Repeats, o.Steps, o.Checks, o.Devices, o.AlsoActive, o.Note,
    };

    private static object SummaryDto(AlarmHistorySummary h) => new
    {
        h.AlarmId, h.Total, h.Last7Days, h.Last30Days, h.FirstSeen, h.Noted, h.Insight, h.UsualStep,
        averageDuration = h.AverageDuration is { } a ? AlarmMemory.Span(a) : null,
        downtime30Days = AlarmMemory.Span(h.Downtime30Days),
        causes = h.Causes,
        latest = h.Latest is { } l ? Dto(l) : null,
        lastNoted = h.LastNoted is { } n ? Dto(n) : null,
    };

    public static void MapMemory(this WebApplication app)
    {
        // The overview for the History page: most frequent alarms, downtime, what still needs a note
        app.MapGet("/api/memory", (AlarmMemory mem, double? days, string? by) =>
        {
            var now = DateTimeOffset.Now;
            var d = Math.Clamp(days ?? 7, 1, 3650);
            var from = now.AddDays(-d);
            var inWindow = mem.Occurrences(from, now);
            return Results.Ok(new
            {
                days = d,
                recorded = mem.Count,
                occurrences = inWindow.Count,
                noted = inWindow.Count(o => o.Note != null),
                downtime = AlarmMemory.Span(inWindow.Aggregate(TimeSpan.Zero, (s, o) => s + ((o.Went ?? now) - (o.Came < from ? from : o.Came)))),
                // by=downtime ranks by how long each alarm was on in total (History › Ranking); the default by how often
                top = mem.Top(from, now, by != null ? 25 : 10, by == "downtime").Select(r => new
                {
                    r.AlarmId, r.Text, r.Severity, r.Count, downtime = AlarmMemory.Span(r.Downtime), downtimeMinutes = Math.Round(r.Downtime.TotalMinutes, 1),
                    r.Last, r.Noted, r.TopCause,
                }),
                // Alarms that went in the last 12 hours and nobody wrote down what fixed them
                unnoted = mem.Unnoted(now.AddHours(-12), 5).Select(Dto),
                loadError = mem.LoadError,
            });
        });

        // One alarm: the summary line, the noted causes, and its occurrences
        app.MapGet("/api/memory/alarms/{id}", (string id, AlarmMemory mem, int? max) =>
        {
            var now = DateTimeOffset.Now;
            return Results.Ok(new
            {
                summary = SummaryDto(mem.Summary(id, now)),
                occurrences = mem.Occurrences(DateTimeOffset.MinValue, now, id, Math.Clamp(max ?? 20, 1, 500)).Select(Dto),
            });
        });

        // The log: every occurrence in the window, filtered by alarm, words, or "no note yet"
        app.MapGet("/api/memory/occurrences", (AlarmMemory mem, double? days, string? alarm, string? q, bool? unnoted, int? max) =>
        {
            var now = DateTimeOffset.Now;
            var list = Filter(mem, now, days ?? 7, alarm, q, unnoted == true);
            var take = Math.Clamp(max ?? 100, 1, 2000);
            return Results.Ok(new { total = list.Count, occurrences = list.Take(take).Select(Dto) });
        });

        app.MapPost("/api/memory/occurrences/{id}/note", (string id, NoteRequest req, AlarmMemory mem, HttpContext http) =>
        {
            var who = http.User.Identity?.IsAuthenticated == true ? http.User.Identity!.Name : null;
            if (string.IsNullOrEmpty(who))
                return Results.Json(new { error = "Log in to add a note, so the team knows who wrote it.", login = true }, statusCode: 401);
            var o = mem.SetNote(id, req.Cause, req.Fix, req.Text, who);
            return o == null ? Results.NotFound(new { error = "That alarm occurrence is not in the machine memory." }) : Results.Ok(Dto(o));
        });

        // The same log as a spreadsheet file for the maintenance team
        app.MapGet("/api/memory/export.csv", (AlarmMemory mem, MachineModel model, double? days, string? alarm, string? q, bool? unnoted) =>
        {
            var now = DateTimeOffset.Now;
            var d = Math.Clamp(days ?? 30, 1, 3650);
            var sb = new StringBuilder();
            sb.AppendLine("Alarm;Text;Severity;Came;Went;Duration (min);Step when it came;Failed checks;Cause;Fix;Note;Noted by;Noted at");
            foreach (var o in Filter(mem, now, d, alarm, q, unnoted == true))
                sb.AppendLine(string.Join(";", new[]
                {
                    o.AlarmId, o.Text, o.Severity, Time(o.Came), o.Went is { } w ? Time(w) + (o.EndUnknown ? " (or earlier)" : "") : "still on",
                    o.Duration is { } du ? du.TotalMinutes.ToString("0.0", CultureInfo.InvariantCulture) : "",
                    string.Join(" | ", o.Steps), string.Join(" | ", o.Checks),
                    o.Note?.Cause ?? "", o.Note?.Fix ?? "", o.Note?.Text ?? "", o.Note?.By ?? "", o.Note is { } n ? Time(n.At) : "",
                }.Select(Csv)));
            var name = new string((model.Name ?? "machine").Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray());
            // BOM so Excel opens umlauts and degree signs correctly
            return Results.File(Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(sb.ToString())).ToArray(),
                "text/csv; charset=utf-8", $"alarm-history-{name}-{now:yyyy-MM-dd}.csv");
        });
    }

    private static List<AlarmOccurrence> Filter(AlarmMemory mem, DateTimeOffset now, double days, string? alarm, string? q, bool unnoted)
    {
        var list = mem.Occurrences(now.AddDays(-Math.Clamp(days, 1, 3650)), now, string.IsNullOrWhiteSpace(alarm) ? null : alarm.Trim());
        if (unnoted) list = list.Where(o => o.Note == null && o.Went != null).ToList();
        if (!string.IsNullOrWhiteSpace(q))
        {
            var words = q.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            list = list.Where(o =>
            {
                var hay = string.Join(" ", new[] { o.AlarmId, o.Text, o.Location, o.Note?.Cause, o.Note?.Fix, o.Note?.Text, o.Note?.By }
                    .Concat(o.Steps).Concat(o.Devices));
                return words.All(w => hay.Contains(w, StringComparison.OrdinalIgnoreCase));
            }).ToList();
        }
        return list;
    }

    private static string Time(DateTimeOffset t) => t.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

    private static string Csv(string? s)
    {
        s ??= "";
        // A leading = + - @ would run as a formula in Excel
        if (s.Length > 0 && "=+-@".Contains(s[0])) s = "'" + s;
        return s.IndexOfAny(new[] { ';', '"', '\n', '\r' }) >= 0 ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
    }
}
