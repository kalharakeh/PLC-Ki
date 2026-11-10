using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using PlcKi.Core.Memory;
using PlcKi.Core.Reports;
using PlcKi.Core.Status;
using PlcKi.Core.Storage;
using PlcKi.Server.Live;

namespace PlcKi.Server.Storage;

/// <summary>One row of the Storage section: one kind of data PLC-Ki keeps.</summary>
public sealed record StorageRow(string Kind, string Title, string What, string Where, string? Folder, int? Count, string CountText,
    long? Bytes, DateTimeOffset? Oldest, string Limit, string? Clear, bool Never = false);

/// <summary>Free space on the drive of the data folder. Low: under 2 GB, or under 10 % on a small disk (under 10 GB free).</summary>
public sealed record DiskSpace(string Drive, long Free, long Total, bool Low, string? Warning);

/// <summary>
/// What PLC-Ki keeps and how much (Setup › Settings › Storage), and the limits that stop it growing forever:
/// pruning at start and once a day (StoragePruning), export, and clear on an administrator's confirmed request.
/// Pruning only ever touches: production log day files (reports\*\yyyy-MM-dd.jsonl), event day files
/// (events\*\yyyy-MM-dd.jsonl and events\*\steps\yyyy-MM-dd.jsonl), the older change log files,
/// alarm recordings (captures\*\*.json) and alarm memory occurrences without a note. Never: notes, documents,
/// logins, settings, keys, models, the hold list or the report settings.
/// </summary>
public sealed class StorageService
{
    public const long LowBytes = 2L * 1024 * 1024 * 1024;
    public const double LowShare = 0.10;
    /// <summary>10 % of a big disk is still plenty for PLC-Ki: the share only counts below this.</summary>
    public const long ShareCountsBelow = 10L * 1024 * 1024 * 1024;

    private readonly string _dataDir;
    private readonly Func<RetentionLimits> _limits;
    private readonly AlarmMemory? _memory;
    private readonly AlarmCaptureBook? _book;
    private readonly ProductionLog? _production;
    private readonly ILogger? _log;
    private readonly object _lock = new();

    public StorageService(string dataDir, Func<RetentionLimits> limits, AlarmMemory? memory = null, AlarmCaptureBook? book = null,
        ProductionLog? production = null, ILogger? log = null)
    {
        _dataDir = Path.GetFullPath(dataDir);
        _limits = limits;
        _memory = memory;
        _book = book;
        _production = production;
        _log = log;
    }

    public string DataDir => _dataDir;
    public RetentionLimits Limits => _limits();
    private string ReportsDir => Path.Combine(_dataDir, "reports");
    private string CapturesDir => Path.Combine(_dataDir, "captures");
    private string EventsDir => Path.Combine(_dataDir, "events");
    public JsonLinesLog ChangeLog => WriteEndpoints.ChangeLog(_dataDir);

    /// <summary>The last automatic pruning: when, and what it removed.</summary>
    public (DateTimeOffset? At, List<Removed> Removed) LastPrune { get; private set; } = (null, new());

    // ---------- pruning ----------

    /// <summary>
    /// Applies the limits: removes what is over them. With dryRun nothing changes and the list says what would go
    /// (for the confirmation before tighter limits are saved). Returns only the kinds where something goes.
    /// </summary>
    public List<Removed> Prune(DateTimeOffset now, bool dryRun = false, RetentionLimits? limits = null)
    {
        var r = limits ?? _limits();
        var list = new List<Removed>();
        lock (_lock)
        {
            // Production log: whole day files older than N days, in every machine's folder
            var cutoff = DateOnly.FromDateTime(now.LocalDateTime.Date.AddDays(-r.ProductionDays));
            var days = DayFiles().Where(d => d.Day < cutoff).ToList();
            long dayBytes = days.Sum(d => Size(d.Path));
            if (!dryRun) foreach (var d in days) TryDelete(d.Path);
            if (days.Count > 0)
                list.Add(new Removed("production", days.Count, dayBytes, $"{Plural(days.Count, "day file")} up to {days.Max(d => d.Day):yyyy-MM-dd}"));

            // Machine events and, kept shorter, steps and outputs: whole day files
            foreach (var (busy, keep) in new[] { (false, r.EventDays), (true, r.StepEventDays) })
            {
                var evCut = DateOnly.FromDateTime(now.LocalDateTime.Date.AddDays(-keep));
                var old = EventLog.DayFiles(EventsDir, busy).Where(d => d.Day < evCut).ToList();
                long evBytes = old.Sum(d => Size(d.Path));
                if (!dryRun) foreach (var d in old) TryDelete(d.Path);
                if (old.Count > 0)
                    list.Add(new Removed(busy ? "steps" : "events", old.Count, evBytes, $"{Plural(old.Count, busy ? "day file of steps" : "day file of events", busy ? "day files of steps" : "day files of events")} up to {old.Max(d => d.Day):yyyy-MM-dd}"));
            }

            var ch = ChangeLog.Prune(r.ChangeLogDays, r.ChangeLogMb * 1024L * 1024, now, dryRun);
            if (ch.Count > 0) list.Add(ch);

            // Alarm recordings: the newest N per machine model. The running model's folder goes through its book,
            // so the recordings it holds in memory go too.
            int recs = 0;
            long recBytes = 0;
            var live = _book?.Folder is { } bf ? Path.GetFullPath(bf) : null;
            bool liveSeen = false;
            foreach (var dir in Folders(CapturesDir))
            {
                var over = Directory.GetFiles(dir, "*.json").OrderByDescending(f => Path.GetFileName(f), StringComparer.Ordinal).Skip(r.Recordings).ToList();
                recs += over.Count;
                recBytes += over.Sum(Size);
                bool isLive = live != null && string.Equals(Path.GetFullPath(dir), live, StringComparison.OrdinalIgnoreCase);
                liveSeen |= isLive;
                if (dryRun) continue;
                if (isLive) _book!.Prune(r.Recordings);
                else foreach (var f in over) TryDelete(f);
            }
            if (_book != null && _book.Folder == null) recs += _book.Prune(r.Recordings, dryRun);
            else if (_book != null && !liveSeen && !dryRun) _book.Prune(r.Recordings);     // no folder yet: just the new limit
            if (recs > 0) list.Add(new Removed("recordings", recs, recBytes, $"{Plural(recs, "recording")} (the oldest)"));

            if (_memory != null && _memory.Prune(r.AlarmOccurrences, dryRun) is var occ and > 0)
                list.Add(new Removed("memory", occ, 0, $"{Plural(occ, "alarm occurrence")} without a note (the oldest)"));
        }
        if (!dryRun)
        {
            LastPrune = (now, list);
            foreach (var x in list) _log?.LogInformation("Storage limits: removed {Kind}: {Text}, {Bytes} bytes.", x.Kind, x.Text, x.Bytes);
            if (list.Count == 0) _log?.LogInformation("Storage limits: nothing over the limits.");
        }
        return list;
    }

    // ---------- clear ----------

    public static readonly string[] Clearable = { "production", "events", "steps", "changes", "recordings", "memory" };

    /// <summary>
    /// Removes one kind completely, on an administrator's confirmed request, and writes it into the change log with
    /// their name (after the deletion, so clearing the change log records itself).
    /// </summary>
    public Removed Clear(string kind, string user, DateTimeOffset now)
    {
        Removed done;
        lock (_lock)
        {
            switch (kind)
            {
                case "production":
                    var days = DayFiles();
                    long bytes = days.Sum(d => Size(d.Path));
                    int n = days.Count(d => TryDelete(d.Path));
                    _production?.Restart();
                    done = new Removed(kind, n, bytes, Plural(n, "day file"));
                    break;
                case "events":
                case "steps":
                    var evFiles = EventLog.DayFiles(EventsDir, kind == "steps");
                    long eb = evFiles.Sum(d => Size(d.Path));
                    int en = evFiles.Count(d => TryDelete(d.Path));
                    done = new Removed(kind, en, eb, Plural(en, "day file"));
                    break;
                case "changes":
                    done = ChangeLog.Clear() with { Kind = kind };
                    break;
                case "recordings":
                    var files = Folders(CapturesDir).SelectMany(d => Directory.GetFiles(d, "*.json")).ToList();
                    long rb = files.Sum(Size);
                    int rn = files.Count(TryDelete);
                    _book?.Clear();
                    done = new Removed(kind, rn, rb, Plural(rn, "recording"));
                    break;
                case "memory":
                    if (_memory == null) throw new ArgumentException("No alarm memory.");
                    int m = _memory.Clear();
                    done = new Removed(kind, m, 0, $"{Plural(m, "alarm occurrence")} without a note");
                    break;
                default:
                    throw new ArgumentException($"There is nothing called '{kind}' to clear.");
            }
        }
        WriteEndpoints.Append(_dataDir, new WriteLogEntry(now, user, "", "none", Title(kind), done.Text, null, null, null,
            "Cleared in Setup › Settings › Storage.", true, null, Action: "clear"));
        _log?.LogWarning("{User} cleared {Kind}: {Text}, {Bytes} bytes.", user, kind, done.Text, done.Bytes);
        return done;
    }

    private static string Title(string kind) => kind switch
    {
        "production" => "Production log",
        "events" => "Machine events",
        "steps" => "Steps and outputs",
        "changes" => "Change log",
        "recordings" => "Alarm recordings",
        "memory" => "Alarm memory",
        _ => kind,
    };

    // ---------- overview ----------

    public DiskSpace Disk()
    {
        try
        {
            var d = new DriveInfo(Path.GetPathRoot(_dataDir)!);
            long free = d.AvailableFreeSpace, total = d.TotalSize;
            bool low = IsLow(free, total);
            return new DiskSpace(d.Name, free, total, low, low
                ? $"Only {Bytes(free)} free on {d.Name.TrimEnd('\\')}. PLC-Ki stops keeping new records when the disk is full. Lower the limits below, export and clear what you no longer need, or free space on the PC."
                : null);
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
            return new DiskSpace("", 0, 0, false, null);
        }
    }

    public static bool IsLow(long free, long total) => free < LowBytes || (total > 0 && free < total * LowShare && free < ShareCountsBelow);

    /// <summary>One row per kind of data: what it is, where, how much, the oldest entry, the limit.</summary>
    public List<StorageRow> Rows(DateTimeOffset now, MachineHistory? history = null, FastRecorder? fast = null, int? heldCount = null)
    {
        var r = _limits();
        var rows = new List<StorageRow>();
        string Rel(string p) => Path.GetRelativePath(_dataDir, p);

        if (history != null)
        {
            var snaps = history.Snapshots(history.KeepValues, now);
            rows.Add(new StorageRow("values", "Values", "Every value, for the charts and \"what happened before the alarm\".", "memory", null,
                snaps.Count, Plural(snaps.Count, "read"), null, history.OldestValueTime,
                $"the last {history.KeepValues.TotalMinutes:0} minutes (History kept, above)", null));
        }
        if (fast != null)
            rows.Add(new StorageRow("fast", "Fast recording", "Every change the PLC sends, to cut the recording around an alarm.", "memory", null,
                fast.Count, Plural(fast.Count, "change"), null, fast.Oldest, $"the last {fast.Keep.TotalSeconds:0} seconds", null));

        // Alarm recordings
        var recFiles = Folders(CapturesDir).SelectMany(d => Directory.GetFiles(d, "*.json")).ToList();
        var recOldest = recFiles.Select(CaptureTime).Where(t => t != null).Min();
        rows.Add(new StorageRow("recordings", "Alarm recordings", "The seconds around each alarm, value by value.", "disk", Rel(CapturesDir),
            recFiles.Count, Plural(recFiles.Count, "recording"), recFiles.Sum(Size), recOldest,
            $"the newest {N(r.Recordings)} per machine", recFiles.Count == 0 ? null
                : $"all {Plural(recFiles.Count, "alarm recording")} ({Bytes(recFiles.Sum(Size))}) in {Rel(CapturesDir)}. The alarm memory and its notes stay."));

        // Alarm memory
        var memDir = Path.Combine(_dataDir, "memory");
        var memFiles = Directory.Exists(memDir) ? Directory.GetFiles(memDir, "*.json") : Array.Empty<string>();
        var (memOldest, noted) = _memory?.Stats() ?? (null, 0);
        int memCount = _memory?.Count ?? 0;
        int clearable = memCount - noted - (_memory?.Occurrences(now, now).Count(o => o.Went == null && o.Note == null) ?? 0);
        rows.Add(new StorageRow("memory", "Alarm memory", $"Every alarm occurrence with its context, and the notes technicians wrote ({Plural(noted, "note")}).",
            "disk", Rel(memDir), memCount, Plural(memCount, "occurrence"), memFiles.Sum(Size), memOldest,
            $"{N(r.AlarmOccurrences)} occurrences; ones with a note are never removed",
            clearable <= 0 ? null : $"{Plural(clearable, "alarm occurrence")} without a note, from this machine's alarm memory. The {Plural(noted, "note")} technicians wrote stay, and so do alarms that are on now."));

        // Production log
        var days = DayFiles();
        rows.Add(new StorageRow("production", "Production log", "What the machine did (running, idle, down), parts and cycles, for the shift, OEE and fault reports.",
            "disk", Rel(ReportsDir), days.Count, Plural(days.Count, "day"), days.Sum(d => Size(d.Path)),
            days.Count > 0 ? new DateTimeOffset(days[0].Day.ToDateTime(TimeOnly.MinValue)) : null,
            $"{N(r.ProductionDays)} days", days.Count == 0 ? null
                : $"all {Plural(days.Count, "day file")} ({days[0].Day:yyyy-MM-dd} to {days[^1].Day:yyyy-MM-dd}, {Bytes(days.Sum(d => Size(d.Path)))}) in {Rel(ReportsDir)}. The shift, OEE and fault reports start again from now. The report settings stay."));

        // Machine events, and the steps and outputs kept apart for a shorter time
        foreach (var (busy, keep) in new[] { (false, r.EventDays), (true, r.StepEventDays) })
        {
            var ev = EventLog.DayFiles(EventsDir, busy);
            var evBytes = ev.Sum(d => Size(d.Path));
            string kind = busy ? "steps" : "events";
            rows.Add(new StorageRow(kind, busy ? "Steps and outputs" : "Machine events",
                busy ? "Every step change and every output switched on or off, for the timeline and the shift log."
                     : "Alarms that came and went, halts, blocked devices, CPU mode, connection drops, values changed from PLC-Ki.",
                "disk", Rel(EventsDir) + (busy ? @"\…\" + EventLog.StepsFolder : ""), ev.Count, Plural(ev.Count, "day"), evBytes,
                ev.Count > 0 ? new DateTimeOffset(ev[0].Day.ToDateTime(TimeOnly.MinValue)) : null,
                $"{N(keep)} days", ev.Count == 0 ? null
                    : $"all {Plural(ev.Count, "day file")} ({ev[0].Day:yyyy-MM-dd} to {ev[^1].Day:yyyy-MM-dd}, {Bytes(evBytes)}) of {(busy ? "steps and outputs" : "machine events")}. The timeline and the shift log start again from now."));
        }

        // Change log
        var chFiles = ChangeLog.Files();
        int chCount = 0;
        DateTimeOffset? chOldest = null;
        foreach (var f in chFiles)
            try
            {
                foreach (var line in File.ReadLines(f))
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    chCount++;
                    if (chOldest == null && WriteEndpoints.Parse(line) is { } e) chOldest = e.Time;
                }
            }
            catch (IOException) { }
        long chBytes = chFiles.Sum(Size);
        rows.Add(new StorageRow("changes", "Change log", "Every value changed, held or refused from PLC-Ki, with who and why. Also every clear on this page.",
            "disk", Rel(ChangeLog.Live) + (chFiles.Count > 1 ? $" and {Plural(chFiles.Count - 1, "older file")}" : ""), chCount, Plural(chCount, "entry", "entries"), chBytes, chOldest,
            $"{N(r.ChangeLogDays)} days or {N(r.ChangeLogMb)} MB", chCount == 0 ? null
                : $"all {Plural(chCount, "entry", "entries")} ({Bytes(chBytes)}): who changed which value, when and why. One new entry then says that you cleared it."));

        // Kept, never removed automatically
        var held = Path.Combine(_dataDir, "held-inputs.json");
        rows.Add(new StorageRow("holds", "Hold list", "Inputs held on PLCSIM.", "disk", Rel(held), heldCount, heldCount is { } h ? Plural(h, "input") : "",
            File.Exists(held) ? Size(held) : 0, null, "until released (Tags page)", null, Never: true));
        var docs = Path.Combine(_dataDir, "documents");
        var docFiles = Directory.Exists(docs) ? Directory.GetFiles(docs, "*", SearchOption.AllDirectories) : Array.Empty<string>();
        rows.Add(new StorageRow("documents", "Documents", "Manuals, drawings and fault lists.", "disk", Rel(docs), docFiles.Length, Plural(docFiles.Length, "file"),
            docFiles.Sum(Size), null, "kept until deleted on the Documents page", null, Never: true));
        var cfg = new[] { "settings.json", "users.json", "model-exports.json" }.Select(f => Path.Combine(_dataDir, f)).Where(File.Exists)
            .Concat(Directory.Exists(Path.Combine(_dataDir, "keys")) ? Directory.GetFiles(Path.Combine(_dataDir, "keys")) : Array.Empty<string>()).ToList();
        rows.Add(new StorageRow("config", "Settings, logins and keys", "settings.json, users.json and the encryption keys.", "disk", "settings.json, users.json, keys",
            null, "", cfg.Sum(Size), null, "always kept", null, Never: true));
        return rows;
    }

    // ---------- export ----------

    /// <summary>Production log as CSV for a date range (semicolons, UTF-8 with BOM: German Excel opens it as it is).</summary>
    public byte[] ProductionCsv(DateOnly from, DateOnly to)
    {
        var sb = new StringBuilder("Machine;Time;Record;State;Why;Alarm;Good;Bad;Sequence;Cycle (s)\r\n");
        foreach (var dir in Folders(ReportsDir))
            foreach (var (day, path) in ProductionLog.DayFiles(dir).Where(d => d.Day >= from && d.Day <= to))
                foreach (var line in SafeLines(path))
                {
                    ProdRecord? p;
                    try { p = JsonSerializer.Deserialize<ProdRecord>(line); } catch (JsonException) { continue; }
                    if (p == null) continue;
                    sb.AppendLine(string.Join(";", new[]
                    {
                        Path.GetFileName(dir), Time(p.T), p.K, p.S ?? "", p.Why ?? "", p.Alarm ?? "", p.Good?.ToString(CultureInfo.InvariantCulture) ?? "",
                        p.Bad?.ToString(CultureInfo.InvariantCulture) ?? "", p.Seq ?? "", p.Sec?.ToString("0.0", CultureInfo.InvariantCulture) ?? "",
                    }.Select(Csv)));
                }
        return Bom(sb);
    }

    /// <summary>The whole change log, oldest first, as CSV.</summary>
    public byte[] ChangesCsv()
    {
        var sb = new StringBuilder("Time;Who;PLC;Target;Action;Tag;Address;Before;Value;After;Stayed;Why;OK;Refused because;Rule\r\n");
        foreach (var line in ChangeLog.OldestFirst())
            if (WriteEndpoints.Parse(line) is { } e)
                sb.AppendLine(string.Join(";", new[]
                {
                    Time(e.Time), e.User, e.Plc, e.Target, e.Action ?? "change", e.Symbol, e.Address ?? "", e.Before ?? "", e.Value, e.After ?? "",
                    e.Held is { } h ? (h ? "yes" : "no") : "", e.Reason ?? "", e.Ok ? "yes" : "no", e.Error ?? "", e.Rule ?? "",
                }.Select(Csv)));
        return Bom(sb);
    }

    /// <summary>Every alarm recording as one zip (a folder per machine model).</summary>
    public void RecordingsZip(Stream output)
    {
        using var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true);
        foreach (var dir in Folders(CapturesDir))
            foreach (var f in Directory.GetFiles(dir, "*.json"))
                try { zip.CreateEntryFromFile(f, Path.GetFileName(dir) + "/" + Path.GetFileName(f), CompressionLevel.Optimal); }
                catch (IOException) { /* removed meanwhile */ }
    }

    // ---------- helpers ----------

    private List<(DateOnly Day, string Path)> DayFiles() =>
        Folders(ReportsDir).SelectMany(ProductionLog.DayFiles).OrderBy(d => d.Day).ToList();

    private static IEnumerable<string> Folders(string root) => Directory.Exists(root) ? Directory.GetDirectories(root) : Array.Empty<string>();

    private static DateTimeOffset? CaptureTime(string file)
    {
        var name = Path.GetFileName(file);
        return name.Length >= 15 && DateTime.TryParseExact(name[..15], "yyyyMMdd-HHmmss", CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var t) ? new DateTimeOffset(t, TimeSpan.Zero) : null;
    }

    private static long Size(string f)
    {
        try { return new FileInfo(f).Length; } catch (IOException) { return 0; }
    }

    private static bool TryDelete(string f)
    {
        try { File.Delete(f); return true; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    private static IEnumerable<string> SafeLines(string f)
    {
        string[] lines;
        try { lines = File.ReadAllLines(f); } catch (IOException) { yield break; }
        foreach (var l in lines) if (!string.IsNullOrWhiteSpace(l)) yield return l;
    }

    private static string N(int n) => n.ToString("N0", CultureInfo.InvariantCulture);
    public static string Plural(int n, string one, string? many = null) => $"{N(n)} {(n == 1 ? one : many ?? one + "s")}";

    public static string Bytes(long b) => b switch
    {
        >= 1L << 30 => (b / (double)(1L << 30)).ToString("0.0", CultureInfo.InvariantCulture) + " GB",
        >= 1L << 20 => (b / (double)(1L << 20)).ToString("0.0", CultureInfo.InvariantCulture) + " MB",
        >= 1L << 10 => (b / 1024.0).ToString("0", CultureInfo.InvariantCulture) + " KB",
        _ => b.ToString(CultureInfo.InvariantCulture) + " bytes",
    };

    private static string Time(DateTimeOffset t) => t.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
    private static string Csv(string s) => s.Contains(';') || s.Contains('"') || s.Contains('\n') ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
    private static byte[] Bom(StringBuilder sb) => Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(sb.ToString())).ToArray();
}

/// <summary>Applies the storage limits at start and once a day.</summary>
public sealed class StoragePruning : BackgroundService
{
    private readonly StorageService _storage;
    private readonly ILogger<StoragePruning> _log;

    public StoragePruning(StorageService storage, ILogger<StoragePruning> log)
    {
        _storage = storage;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        try { await Task.Delay(TimeSpan.FromSeconds(10), stop); }   // let PLC-Ki start first
        catch (OperationCanceledException) { return; }
        while (!stop.IsCancellationRequested)
        {
            try { _storage.Prune(DateTimeOffset.Now); }
            catch (Exception ex) { _log.LogWarning(ex, "Storage limits could not be applied; trying again tomorrow."); }
            try { await Task.Delay(TimeSpan.FromDays(1), stop); }
            catch (OperationCanceledException) { return; }
        }
    }
}
