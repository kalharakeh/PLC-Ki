using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using PlcKi.Core.Model;
using PlcKi.Core.Reports;
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
    }

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
