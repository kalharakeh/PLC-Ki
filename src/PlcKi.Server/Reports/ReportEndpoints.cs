using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using PlcKi.Core.Model;
using PlcKi.Core.Reports;
using PlcKi.Core.Status;
using PlcKi.Server.Live;

namespace PlcKi.Server.Reports;

/// <summary>
/// The Reports page: shift report with OEE, and the fault summary for maintenance. The production log and the
/// report settings live in the data folder under reports\&lt;machine&gt;\, so each machine model keeps its own.
/// </summary>
public sealed class ReportService
{
    private readonly object _lock = new();
    private ReportSettings _settings;

    public ReportService(MachineModel model, string dataDir, PlcKi.Core.Memory.AlarmMemory memory)
    {
        Folder = Path.Combine(dataDir, "reports", Slug(model.Name));
        SettingsFile = Path.Combine(Folder, "settings.json");
        _settings = ReportSettings.Load(SettingsFile);
        Log = new ProductionLog(model, Folder, () => Settings);
        // Alarm occurrences come from the machine memory (kept on disk, survives restarts)
        Alarms = new MemoryAlarmSpans(memory);
        Builder = new ReportBuilder(model, Log, Alarms, () => Settings);
        Handover = new HandoverNotes(HandoverNotes.FileFor(dataDir, Slug(model.Name)));
    }

    /// <summary>The shift handover notes (data folder\handover\&lt;machine&gt;.json; never pruned).</summary>
    public HandoverNotes Handover { get; }

    public string Folder { get; }
    public string SettingsFile { get; }
    public ProductionLog Log { get; }
    public IAlarmSpans Alarms { get; }
    public ReportBuilder Builder { get; }
    public ReportSettings Settings { get { lock (_lock) return _settings; } }

    public void Save(ReportSettings s)
    {
        lock (_lock)
        {
            s.Save(SettingsFile);
            _settings = s;
        }
    }

    public static string Slug(string name)
    {
        var s = Regex.Replace(name.ToLowerInvariant(), "[^a-z0-9]+", "-").Trim('-');
        return s.Length == 0 ? "machine" : s.Length > 60 ? s[..60] : s;
    }
}

/// <summary>PUT /api/reports/handover: the shift's note; empty text removes it.</summary>
public sealed record HandoverRequest(string? Day, int? Shift, string? Text);

public static class ReportEndpoints
{
    static readonly HashSet<string> Numeric = new(StringComparer.OrdinalIgnoreCase) { "Int", "DInt", "UInt", "UDInt", "Word", "DWord", "Real", "LReal", "SInt", "USInt", "LInt", "ULInt" };
    static readonly Regex CounterWords = new(@"count|counter|parts?\b|pieces?|good|reject|scrap|nok\b|\bok\b|z[aä]hler|st[uü]ck|gut|schlecht|ausschuss|teile", RegexOptions.IgnoreCase);
    static readonly Regex BadWords = new(@"reject|scrap|nok\b|bad|fail|schlecht|ausschuss|nio\b", RegexOptions.IgnoreCase);

    public static void MapReports(this WebApplication app)
    {
        app.MapGet("/api/reports/settings", (ReportService r, MachineModel m) => Results.Ok(SettingsView(r, m)));

        app.MapPut("/api/admin/reports/settings", (ReportSettings s, ReportService r, MachineModel m) =>
        {
            s.Shifts ??= new();
            foreach (var x in s.Shifts) { x.Name = (x.Name ?? "").Trim(); x.Start = (x.Start ?? "").Trim(); x.End = (x.End ?? "").Trim(); }
            s.GoodCountTag = Blank(s.GoodCountTag);
            s.BadCountTag = Blank(s.BadCountTag);
            s.CycleSequence = Blank(s.CycleSequence);
            var problems = s.Problems();
            foreach (var t in new[] { s.GoodCountTag, s.BadCountTag })
                if (t != null && !m.Tags.Any(x => x.Id == t)) problems.Add($"The tag {t} is not in the machine model.");
            if (s.CycleSequence != null && !m.Sequences.Any(x => x.Id == s.CycleSequence)) problems.Add($"The sequence {s.CycleSequence} is not in the machine model.");
            if (problems.Count > 0) return Results.Json(new { error = string.Join(" ", problems) }, statusCode: 400);
            r.Save(s);
            return Results.Ok(SettingsView(r, m));
        });

        app.MapGet("/api/reports/shift", (ReportService r, string? day, int? shift) =>
        {
            var now = DateTimeOffset.Now;
            var pick = Pick(r.Settings, day, shift, now);
            if (pick == null) return Results.Json(new { error = "No shift is set up. An administrator can add shifts on this page." }, statusCode: 400);
            return Results.Ok(r.Builder.Shift(pick.Value.Day, pick.Value.Index, now));
        });

        // The timeline strip on Plant: the machine's states, the marked events (alarms, halts, STOP, link, values changed)
        // and the latest few events of any kind. window: shift (this shift), 24h or 7d.
        app.MapGet("/api/timeline", (ReportService r, EventLog log, string? window) =>
        {
            var now = DateTimeOffset.Now;
            var w = TimelineWindow(r.Settings, window, now);
            var end = w.To < now ? w.To : now;
            var cfg = r.Settings;
            // Each mark says its shift, so a click opens that shift's log at that time
            var marks = log.Read(w.From, end, EventLog.Marked)
                .Select(e => (e, mark: EventLog.Mark(e))).Where(x => x.mark != null).TakeLast(1500)
                .Select(x => (x.e, x.mark, sh: cfg.Shifts.Count > 0 ? ReportBuilder.ShiftAt(cfg, x.e.Time) : null))
                .Select(x => new { time = x.e.Time, mark = x.mark, kind = x.e.Kind, text = x.e.Text, alarm = x.e.Alarm,
                    shift = x.sh is { } s ? new { day = s.Day.ToString("yyyy-MM-dd"), index = s.Index } : null });
            // The latest lines: the last hour is enough most of the time (steps are many), else the last day
            var recent = log.Read(now.AddHours(-1), now);
            if (recent.Count < 6) recent = log.Read(now.AddHours(-24), now);
            var latest = recent.TakeLast(6).Reverse().Select(EventEndpoints.Dto);
            return Results.Ok(new
            {
                window = w.Window, label = w.Label, from = w.From, to = w.To, now,
                shift = w.Day is { } d ? new { day = d.ToString("yyyy-MM-dd"), index = w.Index } : null,
                spans = r.Builder.States(w.From, w.To, now), marks, recent = latest,
            });
        });

        // Shift handover note (review 3, item 47): one per shift; anyone logged in writes it during that shift or the next one
        app.MapGet("/api/reports/handover", (ReportService r, string? day, int? shift) =>
        {
            var now = DateTimeOffset.Now;
            var pick = Pick(r.Settings, day, shift, now);
            if (pick == null) return Results.Json(new { error = "No shift is set up. An administrator can add shifts on this page." }, statusCode: 400);
            return Results.Ok(HandoverView(r, pick.Value.Day, pick.Value.Index, now));
        });

        app.MapPut("/api/reports/handover", (HandoverRequest req, ReportService r, HttpContext http) =>
        {
            var who = http.User.Identity?.IsAuthenticated == true ? http.User.Identity!.Name : null;
            if (string.IsNullOrEmpty(who))
                return Results.Json(new { error = "Log in to write the handover note, so the next shift knows who wrote it.", login = true }, statusCode: 401);
            var now = DateTimeOffset.Now;
            var cfg = r.Settings;
            var pick = Pick(cfg, req.Day, req.Shift, now);
            if (pick == null) return Results.Json(new { error = "No shift is set up. An administrator can add shifts on this page." }, statusCode: 400);
            var (d, i) = pick.Value;
            if (!HandoverNotes.CanWrite(cfg, d, i, now))
                return Results.Json(new { error = "This shift's handover note can no longer be changed: a note can be written during its shift and the next one." }, statusCode: 409);
            if ((req.Text ?? "").Trim().Length > HandoverNotes.MaxLength)
                return Results.Json(new { error = $"The note is too long: at most {HandoverNotes.MaxLength} characters." }, statusCode: 400);
            var (from, to) = ReportBuilder.Window(d, cfg.Shifts[i]);
            r.Handover.Set(d, i, cfg.Shifts[i].Name, from, to, req.Text, who, now);
            return Results.Ok(HandoverView(r, d, i, now));
        });

        // For every page: the note the last shift left, during the first hours of the next shift (null otherwise)
        app.MapGet("/api/handover/now", (ReportService r) =>
        {
            var n = r.Handover.ToShow(r.Settings, DateTimeOffset.Now);
            return Results.Ok(new { note = n });
        });

        app.MapGet("/api/reports/faults", (ReportService r, int? days) => Results.Ok(r.Builder.Faults(days ?? 7, DateTimeOffset.Now)));

        // Spreadsheet downloads (semicolons and a BOM, so Excel opens them right in German and English Windows)
        app.MapGet("/api/reports/shift.csv", (ReportService r, string? day, int? shift) =>
        {
            var now = DateTimeOffset.Now;
            var pick = Pick(r.Settings, day, shift, now);
            if (pick == null) return Results.BadRequest();
            var x = r.Builder.Shift(pick.Value.Day, pick.Value.Index, now);
            var csv = new Csv();
            csv.Row("Machine", x.Machine).Row("Shift", x.Shift).Row("Day", x.Day).Row("From", T(x.From)).Row("To", T(x.To)).Row("Finished", x.InProgress ? "no" : "yes").Row();
            csv.Row("OEE %", P(x.Oee.Value)).Row("Availability %", P(x.Oee.Availability)).Row("Performance %", P(x.Oee.Performance)).Row("Quality %", P(x.Oee.Quality));
            csv.Row("Parts", x.Parts.Total).Row("Good", x.Parts.Good).Row("Rejects", x.Parts.BadKnown ? x.Parts.Bad : "").Row("Parts counted from", x.Parts.From)
               .Row("Average cycle s", x.Parts.AvgCycleSeconds).Row("Ideal cycle s", x.Parts.IdealCycleSeconds).Row();
            csv.Row("Running min", x.Totals.Running).Row("Idle min", x.Totals.Idle).Row("Down min", x.Totals.Down).Row("No PLC link min", x.Totals.NoLink).Row("Not watched min", x.Totals.Off)
               .Row("Stops", x.Maint.Stops).Row("MTTR min", x.Maint.MttrMinutes).Row("MTBF min", x.Maint.MtbfMinutes).Row();
            csv.Row("Losses").Row("State", "Reason", "Times", "Minutes");
            foreach (var l in x.Losses) csv.Row(l.State, l.Why, l.Times, l.Minutes);
            csv.Row().Row("Alarms").Row("Alarm", "Text", "Severity", "Times", "Minutes", "Longest min", "Where");
            foreach (var f in x.Faults) csv.Row(f.Id, f.Text, f.Severity, f.Count, f.Minutes, f.LongestMinutes, f.Location);
            csv.Row().Row("Timeline").Row("From", "To", "State", "Reason");
            foreach (var s in x.Timeline) csv.Row(T(s.From), T(s.To), s.State, s.Why);
            foreach (var n in x.Notes) csv.Row("Note", n);
            return csv.File($"shift-{x.Day}-{ReportService.Slug(x.Shift)}.csv");
        });

        app.MapGet("/api/reports/faults.csv", (ReportService r, int? days) =>
        {
            var x = r.Builder.Faults(days ?? 7, DateTimeOffset.Now);
            var csv = new Csv();
            csv.Row("Machine", x.Machine).Row("From", T(x.From)).Row("To", T(x.To)).Row("Stops", x.Maint.Stops).Row("MTTR min", x.Maint.MttrMinutes).Row("MTBF min", x.Maint.MtbfMinutes).Row();
            csv.Row("Alarms").Row("Alarm", "Text", "Severity", "Times", "Minutes", "Longest min", "Last", "Where", "Likely causes", "Fix");
            foreach (var f in x.Faults) csv.Row(f.Id, f.Text, f.Severity, f.Count, f.Minutes, f.LongestMinutes, T(f.Last), f.Location, string.Join(" | ", f.Causes), string.Join(" | ", f.Fix));
            csv.Row().Row("Downtime reasons").Row("State", "Reason", "Times", "Minutes");
            foreach (var l in x.Losses) csv.Row(l.State, l.Why, l.Times, l.Minutes);
            csv.Row().Row("Shifts").Row("Day", "Shift", "OEE %", "Availability %", "Performance %", "Quality %", "Parts", "Down min", "Biggest loss");
            foreach (var s in x.Shifts) csv.Row(s.Day, s.Shift, P(s.Oee), P(s.Availability), P(s.Performance), P(s.Quality), s.Parts, s.DownMinutes, s.TopLoss);
            foreach (var n in x.Notes) csv.Row("Note", n);
            return csv.File($"faults-{DateTime.Now:yyyy-MM-dd}-{x.Shifts.Count}-shifts.csv");
        });
    }

    static object SettingsView(ReportService r, MachineModel m)
    {
        var s = r.Settings;
        var now = DateTimeOffset.Now;
        var cur = ReportBuilder.ShiftAt(s, now);
        var last = ReportBuilder.LastShift(s, now);
        // Counter suggestions: whole-number tags whose name or label sounds like a part counter
        var counters = m.Tags.Where(t => Numeric.Contains(t.Type)).Select(t =>
        {
            var text = t.Id + " " + t.Symbol + " " + t.Label;
            bool looks = CounterWords.IsMatch(text) && !t.Type.Contains("Real", StringComparison.OrdinalIgnoreCase);
            return new { id = t.Id, label = t.Label ?? t.Id, symbol = t.Symbol, suggest = looks ? BadWords.IsMatch(text) ? "bad" : "good" : null };
        }).OrderBy(x => x.suggest == null).ThenBy(x => x.label).ToList();
        return new
        {
            settings = s,
            problems = s.Problems(),
            current = cur == null ? null : new { day = cur.Value.Day.ToString("yyyy-MM-dd"), index = cur.Value.Index },
            last = last == null ? null : new { day = last.Value.Day.ToString("yyyy-MM-dd"), index = last.Value.Index },
            counters,
            sequences = m.Sequences.Select(q => new { id = q.Id, name = q.Name, home = q.Steps.FirstOrDefault()?.No, homeName = q.Steps.FirstOrDefault()?.Name }),
            folder = r.Folder,
        };
    }

    static (DateOnly Day, int Index)? Pick(ReportSettings s, string? day, int? shift, DateTimeOffset now)
    {
        if (s.Shifts.Count == 0) return null;
        if (day != null && DateOnly.TryParseExact(day, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d))
            return (d, Math.Clamp(shift ?? 0, 0, s.Shifts.Count - 1));
        return ReportBuilder.ShiftAt(s, now) ?? ReportBuilder.LastShift(s, now) ?? (DateOnly.FromDateTime(now.LocalDateTime), 0);
    }

    /// <summary>
    /// The strip's window: "shift" is the shift running now (the last 8 hours when no shift covers now or none is set up),
    /// "24h" and "7d" end now.
    /// </summary>
    public static (string Window, string Label, DateTimeOffset From, DateTimeOffset To, DateOnly? Day, int Index) TimelineWindow(
        ReportSettings cfg, string? window, DateTimeOffset now)
    {
        switch (window)
        {
            case "24h": return ("24h", "Last 24 hours", now.AddHours(-24), now, null, 0);
            case "7d": return ("7d", "Last 7 days", now.AddDays(-7), now, null, 0);
        }
        if (cfg.Shifts.Count > 0 && ReportBuilder.ShiftAt(cfg, now) is { } at)
        {
            var (from, to) = ReportBuilder.Window(at.Day, cfg.Shifts[at.Index]);
            return ("shift", cfg.Shifts[at.Index].Name + " shift", from, to, at.Day, at.Index);
        }
        return ("shift", "Last 8 hours", now.AddHours(-8), now, null, 0);
    }

    static object HandoverView(ReportService r, DateOnly day, int index, DateTimeOffset now)
    {
        var cfg = r.Settings;
        return new
        {
            day = day.ToString("yyyy-MM-dd"), shiftIndex = index,
            note = r.Handover.Get(day, index),
            canWrite = cfg.Shifts.Count > index && HandoverNotes.CanWrite(cfg, day, index, now),
            maxLength = HandoverNotes.MaxLength,
        };
    }

    static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
    static string T(DateTimeOffset t) => t.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
    static string P(double? x) => x is { } v ? Math.Round(v * 100, 1).ToString(CultureInfo.InvariantCulture) : "";

    sealed class Csv
    {
        private readonly StringBuilder _sb = new();
        public Csv Row(params object?[] cells)
        {
            _sb.AppendLine(string.Join(";", cells.Select(Cell)));
            return this;
        }
        static string Cell(object? c)
        {
            var s = c switch { null => "", double d => d.ToString("0.##", CultureInfo.InvariantCulture), IFormattable f => f.ToString(null, CultureInfo.InvariantCulture), _ => c.ToString() ?? "" };
            return s.IndexOfAny(new[] { ';', '"', '\n', '\r' }) >= 0 ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
        }
        public IResult File(string name) =>
            Results.File(Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(_sb.ToString())).ToArray(), "text/csv; charset=utf-8", name);
    }
}
