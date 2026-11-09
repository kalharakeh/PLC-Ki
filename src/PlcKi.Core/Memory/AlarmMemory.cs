using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using PlcKi.Core.Status;

namespace PlcKi.Core.Memory;

/// <summary>What a technician wrote about one alarm occurrence: what caused it and what fixed it.</summary>
public sealed record AlarmNote(string? Cause, string? Fix, string? Text, string By, DateTimeOffset At);

/// <summary>
/// One time an alarm was on: when it came and went, what the machine was doing when it came, and the note a
/// technician added. Quick re-trips of the same alarm (back within a few seconds) count as one occurrence.
/// </summary>
public sealed class AlarmOccurrence
{
    public string Id { get; set; } = "";
    public string AlarmId { get; set; } = "";
    public string Text { get; set; } = "";
    public string Severity { get; set; } = "";
    public string? Location { get; set; }
    public DateTimeOffset Came { get; set; }
    /// <summary>When it went; null while it is still on.</summary>
    public DateTimeOffset? Went { get; set; }
    /// <summary>The last read that still showed it on (kept so a restart can close it at about the right time).</summary>
    public DateTimeOffset LastSeen { get; set; }
    /// <summary>It was already on when PLC-Ki started watching, so Came is when PLC-Ki first saw it.</summary>
    public bool AtStart { get; set; }
    /// <summary>It went while PLC-Ki was not watching; Went is the last time it was seen on.</summary>
    public bool EndUnknown { get; set; }
    /// <summary>How often it went and came back within a few seconds (merged into this occurrence).</summary>
    public int Repeats { get; set; }
    /// <summary>Where each sequence was when it came, e.g. "Mixing: step 30 Mix and heat, halted".</summary>
    public List<string> Steps { get; set; } = new();
    /// <summary>The program's checks behind the alarm that failed, with the values from the read before it came.</summary>
    public List<string> Checks { get; set; } = new();
    /// <summary>Devices in fault or blocked when it came.</summary>
    public List<string> Devices { get; set; } = new();
    /// <summary>Other alarms that were on when it came.</summary>
    public List<string> AlsoActive { get; set; } = new();
    public AlarmNote? Note { get; set; }

    [JsonIgnore] public TimeSpan? Duration => Went is { } w ? w - Came : null;

    public AlarmOccurrence Copy() => (AlarmOccurrence)MemberwiseClone();
}

/// <summary>A noted cause and how often it was written down.</summary>
public sealed record CauseCount(string Cause, int Count, string? LastFix, DateTimeOffset Last);

/// <summary>What the memory knows about one alarm: how often, how long, and what fixed it before.</summary>
public sealed record AlarmHistorySummary(
    string AlarmId, int Total, int Last7Days, int Last30Days, DateTimeOffset? FirstSeen,
    AlarmOccurrence? Latest, AlarmOccurrence? LastNoted, TimeSpan? AverageDuration, TimeSpan Downtime30Days,
    List<CauseCount> Causes, string? UsualStep, int Noted, string Insight);

/// <summary>One row of "which alarms happen most".</summary>
public sealed record AlarmRank(string AlarmId, string Text, string Severity, int Count, TimeSpan Downtime, DateTimeOffset Last,
    int Noted, string? TopCause);

/// <summary>
/// The machine's memory of alarms: every occurrence with its context and the technician's note, kept on disk
/// across restarts (one JSON file per machine in the data folder). Fed with every status from the polling loop.
/// Only counts reads where the PLC link is up, so a lost connection does not end or start alarms. Thread-safe.
/// </summary>
public sealed class AlarmMemory
{
    /// <summary>An alarm back within this time after it went counts as the same occurrence.</summary>
    public static readonly TimeSpan Flicker = TimeSpan.FromSeconds(10);
    /// <summary>The default limit (Setup › Settings › Storage can change it).</summary>
    public const int MaxOccurrences = 20_000;
    public const int MaxNoteLength = 1000;

    private readonly object _lock = new();
    private readonly string? _file;
    private readonly List<AlarmOccurrence> _items = new();   // oldest first
    private bool _synced;          // the first good read after start has been seen
    private bool _dirty;
    private DateTimeOffset _lastSave = DateTimeOffset.MinValue;

    /// <param name="file">Where to keep the memory; null keeps it in memory only (tests).</param>
    public AlarmMemory(string? file, string? machine = null)
    {
        _file = file;
        Machine = machine;
        if (file != null && File.Exists(file))
        {
            try
            {
                var doc = JsonSerializer.Deserialize<MemoryFile>(File.ReadAllText(file), FileJson);
                if (doc?.Occurrences != null) _items.AddRange(doc.Occurrences.OrderBy(o => o.Came));
            }
            catch (Exception ex)
            {
                // Keep the unreadable file for a person to look at, and start a fresh one next to it
                LoadError = $"The alarm memory file could not be read ({ex.Message}). It was kept as {Path.GetFileName(file)}.bad.";
                try { File.Copy(file, file + ".bad", overwrite: true); } catch { /* best effort */ }
            }
        }
    }

    public string? Machine { get; }
    /// <summary>At most this many occurrences are kept. Occurrences with a note are never removed by this limit.</summary>
    public int Max { get; private set; } = MaxOccurrences;
    /// <summary>Set when the file on disk could not be read at start.</summary>
    public string? LoadError { get; }

    /// <summary>The file for one machine under the data folder: memory/&lt;machine name&gt;.json.</summary>
    public static string FileFor(string dataDir, string machine)
    {
        var slug = new string((machine ?? "machine").Select(c => char.IsLetterOrDigit(c) ? char.ToLowerInvariant(c) : '-').ToArray()).Trim('-');
        while (slug.Contains("--")) slug = slug.Replace("--", "-");
        if (slug.Length == 0) slug = "machine";
        if (slug.Length > 60) slug = slug[..60];
        return Path.Combine(dataDir, "memory", slug + ".json");
    }

    // ---------- recording ----------

    /// <summary>Takes one status from the polling loop: opens, continues and closes occurrences.</summary>
    public void Observe(MachineStatus status)
    {
        if (status.Connection.State is not ("connected" or "snapshot")) return;
        // A read where no tag came back (all errors) says nothing about the alarms
        if (status.Raw != null && status.Raw.Count > 0 && status.Raw.Values.All(v => v == null)) return;
        var t = status.Time;
        bool changed = false;
        lock (_lock)
        {
            var active = status.ActiveAlarms.GroupBy(a => a.Id).ToDictionary(g => g.Key, g => g.First());
            var open = _items.Where(o => o.Went == null).GroupBy(o => o.AlarmId).ToDictionary(g => g.Key, g => g.Last());

            if (!_synced)
            {
                // Occurrences still open from the last run: the same alarm if it is still on, else it went unseen
                foreach (var o in open.Values.Where(o => !active.ContainsKey(o.AlarmId)))
                {
                    o.Went = o.LastSeen;
                    o.EndUnknown = true;
                    changed = true;
                }
                open = open.Where(kv => active.ContainsKey(kv.Key)).ToDictionary(kv => kv.Key, kv => kv.Value);
            }

            foreach (var (id, a) in active)
            {
                if (open.TryGetValue(id, out var o))
                {
                    o.LastSeen = t;
                    continue;
                }
                var recent = _items.LastOrDefault(x => x.AlarmId == id);
                if (recent is { Went: { } went, EndUnknown: false } && t - went <= Flicker && t >= went)
                {
                    recent.Went = null;
                    recent.Repeats++;
                    recent.LastSeen = t;
                }
                else
                {
                    _items.Add(Open(a, status, t, atStart: !_synced || a.AtStart));
                }
                changed = true;
            }

            foreach (var o in open.Values.Where(o => !active.ContainsKey(o.AlarmId)))
            {
                o.Went = t;
                o.LastSeen = t;
                changed = true;
            }

            _synced = true;
            if (changed) { _dirty = true; Trim(); }
            // Came and went are saved at once; "still on" times only every minute
            else if (t - _lastSave > TimeSpan.FromMinutes(1) && _items.Any(o => o.Went == null)) _dirty = true;
        }
        Save(t);
    }

    private static AlarmOccurrence Open(AlarmStatus a, MachineStatus s, DateTimeOffset t, bool atStart) => new()
    {
        Id = Guid.NewGuid().ToString("N")[..12],
        AlarmId = a.Id,
        Text = a.Text,
        Severity = a.Severity,
        Location = a.Location,
        Came = a.Since is { } since && !atStart && since <= t ? since : t,
        LastSeen = t,
        AtStart = atStart,
        Steps = s.Sequences.Where(q => q.Step != null)
            .Select(q => $"{q.Name}: step {q.Step} {q.StepName}".TrimEnd() + (q.Halted ? ", halted" : "")).ToList(),
        Checks = (a.Checks ?? new()).Where(c => c.Met == false)
            .Select(c => c.Text + (c.Values.Count > 0 ? " (" + string.Join(", ", c.Values.Select(v => $"{v.Label} {v.Text}")) + ")" : ""))
            .Take(6).ToList(),
        Devices = s.Equipment.Where(e => e.BlockedBy != null || e.Level is "fault" or "bad")
            .Select(e => $"{e.Name}: {e.State}").Take(6).ToList(),
        AlsoActive = s.ActiveAlarms.Where(x => x.Id != a.Id).Select(x => x.Id).Take(10).ToList(),
    };

    private void Trim() => TrimLocked(Max, dryRun: false);

    /// <summary>The oldest occurrences without a note, beyond the limit. Notes are the valuable part: they are never removed here.</summary>
    private int TrimLocked(int max, bool dryRun)
    {
        if (_items.Count <= max) return 0;
        var drop = _items.Where(o => o.Note == null && o.Went != null).Take(_items.Count - max).ToList();
        if (!dryRun) foreach (var o in drop) _items.Remove(o);
        return drop.Count;
    }

    /// <summary>Sets the limit and removes what is over it now (oldest first, never noted ones). Returns how many were (or would be) removed.</summary>
    public int Prune(int max, bool dryRun = false)
    {
        int n;
        lock (_lock)
        {
            if (!dryRun) Max = Math.Max(1, max);
            n = TrimLocked(Math.Max(1, max), dryRun);
            if (n > 0 && !dryRun) _dirty = true;
        }
        if (n > 0 && !dryRun) Save();
        return n;
    }

    /// <summary>
    /// Clears the memory on an administrator's confirmed request: every occurrence that has ended and has no note.
    /// Occurrences with a note stay (the technicians' knowledge), and so do alarms still on. Returns how many went.
    /// </summary>
    public int Clear()
    {
        int n;
        lock (_lock)
        {
            n = _items.RemoveAll(o => o.Went != null && o.Note == null);
            if (n > 0) _dirty = true;
        }
        if (n > 0) Save();
        return n;
    }

    /// <summary>When the oldest occurrence came, and how many have a note.</summary>
    public (DateTimeOffset? Oldest, int Noted) Stats()
    {
        lock (_lock) return (_items.Count == 0 ? null : _items.Min(o => o.Came), _items.Count(o => o.Note != null));
    }

    public string? FilePath => _file;

    /// <summary>Writes the file now if anything changed (atomic: temp file, then rename).</summary>
    public void Save(DateTimeOffset? now = null)
    {
        if (_file == null) { lock (_lock) { _dirty = false; _lastSave = now ?? DateTimeOffset.Now; } return; }
        string json;
        lock (_lock)
        {
            if (!_dirty) return;
            json = JsonSerializer.Serialize(new MemoryFile { Version = 1, Machine = Machine, Occurrences = _items }, FileJson);
            _dirty = false;
            _lastSave = now ?? DateTimeOffset.Now;
        }
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
            var tmp = _file + ".tmp";
            File.WriteAllText(tmp, json, new UTF8Encoding(false));
            File.Move(tmp, _file, overwrite: true);
        }
        catch
        {
            lock (_lock) _dirty = true;   // try again with the next change
        }
    }

    // ---------- notes ----------

    /// <summary>Adds or replaces the note on one occurrence. Returns the updated occurrence, or null if there is none with that id.</summary>
    public AlarmOccurrence? SetNote(string occurrenceId, string? cause, string? fix, string? text, string by, DateTimeOffset? at = null)
    {
        static string? Clean(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim() is var x && x.Length > MaxNoteLength ? x[..MaxNoteLength] : s.Trim();
        cause = Clean(cause); fix = Clean(fix); text = Clean(text);
        AlarmOccurrence? copy;
        lock (_lock)
        {
            var o = _items.FirstOrDefault(x => x.Id == occurrenceId);
            if (o == null) return null;
            o.Note = cause == null && fix == null && text == null ? null
                : new AlarmNote(cause, fix, text, string.IsNullOrWhiteSpace(by) ? "someone" : by.Trim(), at ?? DateTimeOffset.Now);
            _dirty = true;
            copy = o.Copy();
        }
        Save(at);
        return copy;
    }

    // ---------- reading ----------

    public AlarmOccurrence? Get(string occurrenceId)
    {
        lock (_lock) return _items.FirstOrDefault(x => x.Id == occurrenceId)?.Copy();
    }

    /// <summary>Occurrences that were on at any time between from and to, newest first.</summary>
    public List<AlarmOccurrence> Occurrences(DateTimeOffset from, DateTimeOffset to, string? alarmId = null, int max = int.MaxValue)
    {
        lock (_lock)
            return _items.Where(o => (alarmId == null || o.AlarmId.Equals(alarmId, StringComparison.OrdinalIgnoreCase))
                                     && o.Came <= to && (o.Went ?? DateTimeOffset.MaxValue) >= from)
                .Reverse().Take(max).Select(o => o.Copy()).ToList();
    }

    /// <summary>Occurrences that went since a time and have no note yet, newest first: "what fixed it?" prompts.</summary>
    public List<AlarmOccurrence> Unnoted(DateTimeOffset since, int max = 5)
    {
        lock (_lock)
            return _items.Where(o => o.Went is { } w && w >= since && o.Note == null && !o.EndUnknown)
                .Reverse().Take(max).Select(o => o.Copy()).ToList();
    }

    public int Count { get { lock (_lock) return _items.Count; } }

    /// <summary>Everything the memory knows about one alarm.</summary>
    public AlarmHistorySummary Summary(string alarmId, DateTimeOffset now)
    {
        List<AlarmOccurrence> all;
        lock (_lock) all = _items.Where(o => o.AlarmId.Equals(alarmId, StringComparison.OrdinalIgnoreCase)).Select(o => o.Copy()).ToList();
        all.Reverse();   // newest first

        var last30 = all.Where(o => o.Came >= now.AddDays(-30)).ToList();
        var durations = all.Where(o => o.Went != null && !o.EndUnknown && !o.AtStart).Select(o => o.Duration!.Value).ToList();
        var downtime = last30.Aggregate(TimeSpan.Zero, (sum, o) => sum + ((o.Went ?? now) - o.Came));
        var causes = Causes(all);
        var lastNoted = all.FirstOrDefault(o => o.Note != null);

        string? usualStep = null;
        var steps = all.Where(o => o.Steps.Count > 0 && !o.AtStart).Select(o => o.Steps[0]).ToList();
        if (steps.Count >= 2)
        {
            var top = steps.GroupBy(s => StepOnly(s)).OrderByDescending(g => g.Count()).First();
            if (top.Count() * 2 >= steps.Count && top.Count() >= 2) usualStep = $"{top.Key} ({top.Count()} of {steps.Count} times)";
        }

        var summary = new AlarmHistorySummary(alarmId, all.Count,
            all.Count(o => o.Came >= now.AddDays(-7)), last30.Count, all.LastOrDefault()?.Came,
            all.FirstOrDefault(), lastNoted,
            durations.Count > 0 ? TimeSpan.FromTicks((long)durations.Average(d => d.Ticks)) : null,
            downtime, causes, usualStep, all.Count(o => o.Note != null), "");
        return summary with { Insight = Insight(summary, now) };
    }

    /// <summary>The alarms that happened most in the window, with their downtime.</summary>
    /// <summary>The alarms of a window ranked by how often they came, or (byDowntime) by how long they were on in total.</summary>
    public List<AlarmRank> Top(DateTimeOffset from, DateTimeOffset now, int max = 10, bool byDowntime = false)
    {
        List<AlarmOccurrence> list;
        lock (_lock) list = _items.Where(o => o.Came >= from || (o.Went ?? now) >= from).Select(o => o.Copy()).ToList();
        return list.GroupBy(o => o.AlarmId).Select(g =>
            {
                var latest = g.OrderBy(o => o.Came).Last();
                var down = g.Aggregate(TimeSpan.Zero, (sum, o) => sum + ((o.Went ?? now) - (o.Came < from ? from : o.Came)));
                return new AlarmRank(g.Key, latest.Text, latest.Severity, g.Count(), down, latest.Came,
                    g.Count(o => o.Note != null), Causes(g).FirstOrDefault()?.Cause);
            })
            .OrderByDescending(r => byDowntime ? r.Downtime.Ticks : r.Count).ThenByDescending(r => byDowntime ? r.Count : r.Downtime.Ticks).Take(max).ToList();
    }

    private static List<CauseCount> Causes(IEnumerable<AlarmOccurrence> list) =>
        list.Where(o => o.Note?.Cause != null)
            .GroupBy(o => o.Note!.Cause!.Trim().TrimEnd('.').ToLowerInvariant())
            .Select(g =>
            {
                var newest = g.OrderBy(o => o.Came).Last();
                return new CauseCount(newest.Note!.Cause!.Trim().TrimEnd('.'), g.Count(),
                    g.OrderBy(o => o.Came).LastOrDefault(o => o.Note!.Fix != null)?.Note!.Fix, newest.Came);
            })
            .OrderByDescending(c => c.Count).ThenByDescending(c => c.Last).ToList();

    private static string StepOnly(string s) => s.EndsWith(", halted") ? s[..^", halted".Length] : s;

    /// <summary>The one plain line shown next to the alarm and given to Ask: "Happened 4 times...; last time...".</summary>
    public static string Insight(AlarmHistorySummary h, DateTimeOffset now)
    {
        // The open occurrence is the one happening now: "before" means the others
        int before = h.Total - (h.Latest?.Went == null ? 1 : 0);
        if (h.Total == 0) return "PLC-Ki has not seen this alarm before.";
        if (before <= 0) return "First time PLC-Ki has seen this alarm.";
        var sb = new StringBuilder();
        sb.Append(h.Last30Days > 1 || before > 1
            ? $"Happened {Times(h.Last30Days)} in the last 30 days" + (h.Total > h.Last30Days ? $" ({Times(h.Total)} in all)" : "") + "."
            : $"Happened once before, {Date(h.Latest!.Went == null ? h.FirstSeen ?? h.Latest.Came : h.Latest.Came, now)}.");
        if (h.LastNoted?.Note is { } n)
        {
            sb.Append($" Last time ({Date(h.LastNoted.Came, now)})");
            if (n.Cause != null) sb.Append($" the cause was: {n.Cause.TrimEnd('.')}");
            if (n.Fix != null) sb.Append(n.Cause != null ? $"; fixed by: {n.Fix.TrimEnd('.')}" : $" it was fixed by: {n.Fix.TrimEnd('.')}");
            if (n.Cause == null && n.Fix == null && n.Text != null) sb.Append($" the note says: {n.Text.TrimEnd('.')}");
            sb.Append('.');
        }
        if (h.Causes.Count > 0 && h.Causes[0].Count >= 2)
            sb.Append($" Most often noted cause: {h.Causes[0].Cause} ({Times(h.Causes[0].Count)}).");
        if (h.UsualStep != null) sb.Append($" Usually comes in {h.UsualStep}.");
        return sb.ToString();
    }

    private static string Times(int n) => n == 1 ? "once" : n == 2 ? "twice" : $"{n} times";

    private static string Date(DateTimeOffset t, DateTimeOffset now)
    {
        var local = t.ToLocalTime();
        var days = (now.ToLocalTime().Date - local.Date).Days;
        return days == 0 ? "today " + local.ToString("HH:mm", CultureInfo.InvariantCulture)
            : days == 1 ? "yesterday " + local.ToString("HH:mm", CultureInfo.InvariantCulture)
            : local.ToString("d MMM", CultureInfo.InvariantCulture);
    }

    /// <summary>Plain text for a duration: "45 s", "12 min", "3 h 5 min", "2 d 4 h".</summary>
    public static string Span(TimeSpan d)
    {
        if (d < TimeSpan.Zero) d = TimeSpan.Zero;
        if (d.TotalSeconds < 60) return $"{(int)d.TotalSeconds} s";
        if (d.TotalMinutes < 60) return $"{(int)d.TotalMinutes} min";
        if (d.TotalHours < 24) return $"{(int)d.TotalHours} h" + (d.Minutes > 0 ? $" {d.Minutes} min" : "");
        return $"{(int)d.TotalDays} d" + (d.Hours > 0 ? $" {d.Hours} h" : "");
    }

    public static readonly JsonSerializerOptions FileJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = false,
    };

    private sealed class MemoryFile
    {
        public int Version { get; set; }
        public string? Machine { get; set; }
        public List<AlarmOccurrence>? Occurrences { get; set; }
    }
}
