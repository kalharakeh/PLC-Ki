using System.Buffers;
using System.Globalization;
using System.Text;
using PlcKi.Core.Status;

namespace PlcKi.Server.Live;

/// <summary>
/// What happened on the machine, from the event day files (EventLog): GET /api/events for the pages and
/// GET /api/events.csv ("Download for Excel"). Read-only.
/// </summary>
public static class EventEndpoints
{
    /// <summary>Each kind in plain words, for the spreadsheet.</summary>
    public static readonly IReadOnlyDictionary<string, string> KindNames = new Dictionary<string, string>
    {
        ["alarm"] = "Alarm", ["step"] = "Step", ["halt"] = "Halt", ["blocked"] = "Blocked", ["output"] = "Output",
        ["state"] = "Device state", ["cpu"] = "PLC mode", ["link"] = "PLC connection", ["write"] = "Value changed",
        ["monitor"] = "PLC-Ki",
    };

    /// <summary>
    /// The time window asked for. from/to (ISO times) win; else the last N days; else a default that depends on the
    /// kinds: steps and outputs are many, so without a window only the last day of them is read.
    /// </summary>
    public static (DateTimeOffset From, DateTimeOffset To) Window(string? from, string? to, double? days, IReadOnlySet<string>? kinds, DateTimeOffset now)
    {
        var t = Parse(to) ?? now;
        if (Parse(from) is { } f) return f <= t ? (f, t) : (t, f);
        if (days is > 0) return (t - TimeSpan.FromDays(Math.Min(days.Value, 3660)), t);
        bool busy = kinds == null || kinds.Any(EventLog.Busy.Contains);
        return (t - TimeSpan.FromDays(busy ? 1 : 30), t);
    }

    public static IReadOnlySet<string>? Kinds(string? kind) =>
        string.IsNullOrEmpty(kind) ? null : kind.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).ToHashSet();

    static DateTimeOffset? Parse(string? s) =>
        DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var v) ? v : null;

    public static object Dto(MachineEvent e) => new
    {
        time = e.Time, kind = e.Kind, text = e.Text, mark = EventLog.Mark(e), alarm = e.Alarm, seq = e.Seq, step = e.Step, device = e.Device, tag = e.Tag,
    };

    public static WebApplication MapEvents(this WebApplication app)
    {
        // Newest first. kind: one or several, comma-separated ("alarm,cpu"). max: up to 500, or 5000 with a time window.
        app.MapGet("/api/events", (EventLog log, string? kind, int? max, string? from, string? to, double? days) =>
        {
            var kinds = Kinds(kind);
            var (f, t) = Window(from, to, days, kinds, DateTimeOffset.Now);
            int cap = from != null || days != null ? 5000 : 500;
            var list = log.Read(f, t, kinds);
            list.Reverse();
            return Results.Ok(list.Take(Math.Clamp(max ?? 50, 1, cap)).Select(Dto));
        });

        // Download for Excel: semicolons and UTF-8 with BOM, so German Excel opens it as it is. Written day by day.
        app.MapGet("/api/events.csv", async (EventLog log, HttpContext http, string? kind, string? from, string? to, double? days) =>
        {
            var kinds = Kinds(kind);
            var (f, t) = Window(from, to, days, kinds, DateTimeOffset.Now);
            http.Response.ContentType = "text/csv; charset=utf-8";
            http.Response.Headers.ContentDisposition = $"attachment; filename=\"machine-events-{f.LocalDateTime:yyyy-MM-dd}-to-{t.LocalDateTime:yyyy-MM-dd}.csv\"";
            // Through the response pipe (Write, then FlushAsync per day): the read-only guard test allows no WriteAsync outside PlcWriter
            var w = http.Response.BodyWriter;
            void Put(string text) => w.Write(Encoding.UTF8.GetBytes(text));
            w.Write(Encoding.UTF8.GetPreamble());
            Put("Time;Kind;What happened;Alarm;Sequence;Step;Device;Tag\r\n");
            for (var day = f.LocalDateTime.Date; day <= t.LocalDateTime.Date; day = day.AddDays(1))
            {
                var a = new DateTimeOffset(day, TimeZoneInfo.Local.GetUtcOffset(day));
                var b = a.AddDays(1).AddTicks(-1);
                foreach (var e in log.Read(a < f ? f : a, b > t ? t : b, kinds))
                    Put(string.Join(";", new[]
                    {
                        e.Time.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
                        KindNames.GetValueOrDefault(e.Kind, e.Kind), e.Text, e.Alarm ?? "", e.Seq ?? "",
                        e.Step?.ToString(CultureInfo.InvariantCulture) ?? "", e.Device ?? "", e.Tag ?? "",
                    }.Select(Cell)) + "\r\n");
                await w.FlushAsync();
            }
        });
        return app;
    }

    static string Cell(string s) => s.IndexOfAny(new[] { ';', '"', '\n', '\r' }) >= 0 ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
}
