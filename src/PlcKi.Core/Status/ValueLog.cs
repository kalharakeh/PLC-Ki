using System.Globalization;
using System.Text;
using System.Text.Json;
using PlcKi.Core.Reports;

namespace PlcKi.Core.Status;

/// <summary>One time slice of one value: the lowest, the highest and the last value in it.</summary>
public readonly record struct ValueBucket(double Min, double Max, double Last);

/// <summary>
/// Values over a time range read back from disk, in <see cref="BucketMs"/> slices from <see cref="From"/> on
/// (a slice with no data is null). Tier "full": every read of the polling loop; "thin": one minute per line.
/// </summary>
public sealed record ValueRange(long From, long To, double BucketMs, string Tier, Dictionary<string, ValueBucket?[]> Series)
{
    public long BucketStart(int i) => From + (long)(i * BucketMs);
}

/// <summary>
/// Keeps the numeric values on disk, thinned out, so trends survive a restart and reach back hours or days.
/// Two files per machine and day (JSON lines, local days like the event files):
///   data folder\values\&lt;machine&gt;\full\2026-11-10.jsonl   every read: {"t":ms,"v":{"Temp":21.4}} with only the values that
///                                                     changed, and the whole set ("k":1) at the first read of each minute
///   data folder\values\&lt;machine&gt;\2026-11-10.jsonl        one line per minute: {"t":ms,"v":{"Temp":[min,max,last]}}
/// The full files are kept for a few days, the minute files much longer (Setup › Settings › Storage). Only numbers are
/// saved: booleans have the alarm recordings and the events. Writing never stops the live pages: a full disk or a locked
/// file only means values are missing. Thread-safe. Read-only towards the PLC.
/// </summary>
public sealed class ValueLog
{
    public const string FullFolder = "full";
    /// <summary>Full-rate files are used for windows up to this long (longer ones read the minute files).</summary>
    public static readonly TimeSpan FullWindowMax = TimeSpan.FromHours(2);

    private readonly object _lock = new();
    private readonly string? _dir;
    private readonly string[] _ids;
    private readonly Dictionary<string, string> _quoted;

    private readonly Dictionary<string, double> _written = new();     // what the full file says each value is now
    private readonly Dictionary<string, (double Min, double Max, double Last)> _acc = new();
    private long _minute = long.MinValue;                             // the minute being collected (ms since 1970 / 60000)

    /// <param name="dir">This machine's folder (data folder\values\&lt;machine&gt;); null saves nothing.</param>
    /// <param name="numericIds">The tags to save (the numeric ones of the model).</param>
    public ValueLog(string? dir, IEnumerable<string> numericIds)
    {
        _dir = dir;
        _ids = numericIds.Distinct().ToArray();
        _quoted = _ids.ToDictionary(i => i, i => JsonSerializer.Serialize(i));
    }

    public string? Folder => _dir;
    public static string FolderFor(string dataDir, string machineSlug) => Path.Combine(dataDir, "values", machineSlug);

    // ---------- writing ----------

    public void Add(Snapshot snap)
    {
        if (_dir == null) return;
        var vals = new List<(string Id, double V)>();
        foreach (var id in _ids)
            if (snap.Values.TryGetValue(id, out var o) && o is double d && double.IsFinite(d)) vals.Add((id, d));
        if (vals.Count == 0) return;
        long ms = snap.Time.ToUnixTimeMilliseconds(), minute = ms / 60_000;
        lock (_lock)
        {
            bool newMinute = minute != _minute;
            if (newMinute) { FlushBucket(); _minute = minute; _acc.Clear(); }
            foreach (var (id, v) in vals)
                _acc[id] = _acc.TryGetValue(id, out var a) ? (Math.Min(a.Min, v), Math.Max(a.Max, v), v) : (v, v, v);

            var sb = new StringBuilder(64 + vals.Count * 16);
            sb.Append("{\"t\":").Append(ms).Append(newMinute ? ",\"k\":1" : "").Append(",\"v\":{");
            int n = 0;
            foreach (var (id, v) in vals)
            {
                if (!newMinute && _written.TryGetValue(id, out var w) && w == Round(v)) continue;
                if (n++ > 0) sb.Append(',');
                sb.Append(_quoted[id]).Append(':').Append(Num(v));
                _written[id] = Round(v);
            }
            if (n == 0) return;
            sb.Append("}}");
            Append(Path.Combine(_dir, FullFolder, DayName(snap.Time)), sb.ToString());
        }
    }

    /// <summary>Writes the minute being collected (on stop, so a restart loses nothing). The next read starts a new minute line.</summary>
    public void Flush()
    {
        lock (_lock) { FlushBucket(); _minute = long.MinValue; _acc.Clear(); }
    }

    /// <summary>Forget what was written (the files were cleared): the next read starts new files with a full set.</summary>
    public void Restart()
    {
        lock (_lock) { _minute = long.MinValue; _acc.Clear(); _written.Clear(); }
    }

    private void FlushBucket()
    {
        if (_dir == null || _acc.Count == 0 || _minute == long.MinValue) return;
        var start = DateTimeOffset.FromUnixTimeMilliseconds(_minute * 60_000);
        var sb = new StringBuilder(32 + _acc.Count * 40);
        sb.Append("{\"t\":").Append(start.ToUnixTimeMilliseconds()).Append(",\"v\":{");
        int n = 0;
        foreach (var id in _ids)
        {
            if (!_acc.TryGetValue(id, out var a)) continue;
            if (n++ > 0) sb.Append(',');
            sb.Append(_quoted[id]).Append(":[").Append(Num(a.Min)).Append(',').Append(Num(a.Max)).Append(',').Append(Num(a.Last)).Append(']');
        }
        sb.Append("}}");
        Append(Path.Combine(_dir, DayName(start)), sb.ToString());
    }

    private static void Append(string file, string line)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.AppendAllText(file, line + "\n");
        }
        catch (IOException) { /* a full disk or a locked file must not stop the live pages */ }
        catch (UnauthorizedAccessException) { }
    }

    private static string DayName(DateTimeOffset t) => t.ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + ".jsonl";
    /// <summary>6 significant digits: a Real from the PLC carries 7 and the rest is noise that only makes the files bigger.</summary>
    private static double Round(double v) => double.Parse(v.ToString("G6", CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
    private static string Num(double v) => v.ToString("G6", CultureInfo.InvariantCulture);

    // ---------- reading ----------

    /// <summary>
    /// The values of <paramref name="ids"/> from <paramref name="from"/> to <paramref name="to"/>, in about
    /// <paramref name="buckets"/> slices. Windows of up to two hours that the full files cover are read from them (every read);
    /// longer ones, and ones that reach back past the full files, from the minute files. Slices with several values keep
    /// the lowest, the highest and the last, so a short spike still shows.
    /// </summary>
    public ValueRange Read(DateTimeOffset from, DateTimeOffset to, IReadOnlyCollection<string> ids, int buckets = 300)
    {
        long f = from.ToUnixTimeMilliseconds(), t = to.ToUnixTimeMilliseconds();
        buckets = Math.Clamp(buckets, 10, 2000);
        bool full = _dir != null && to - from <= FullWindowMax && File.Exists(Path.Combine(_dir, FullFolder, DayName(from)));
        // A slice is never shorter than a line of the minute files, or slices between two lines would look like gaps
        double size = Math.Max(full ? 1 : 60_000, (t - f) / (double)buckets);
        buckets = Math.Max(1, (int)Math.Ceiling((t - f) / size));
        var agg = ids.Distinct().ToDictionary(i => i, _ => new ValueBucket?[buckets]);
        void Add(string id, long at, double min, double max, double last)
        {
            if (at < f || at > t || !agg.TryGetValue(id, out var arr)) return;
            int i = Math.Min(buckets - 1, (int)((at - f) / size));
            arr[i] = arr[i] is { } b ? new ValueBucket(Math.Min(b.Min, min), Math.Max(b.Max, max), last) : new ValueBucket(min, max, last);
        }

        if (_dir != null)
            for (var day = from.ToLocalTime().Date; day <= to.ToLocalTime().Date; day = day.AddDays(1))
            {
                var file = full ? Path.Combine(_dir, FullFolder, DayName(day)) : Path.Combine(_dir, DayName(day));
                if (full) ReadFull(file, f, t, size, agg.Keys, Add);
                else ReadThin(file, agg.Keys, Add);
            }
        // The minute being collected is not in a file yet
        if (!full)
            lock (_lock)
                if (_minute != long.MinValue)
                    foreach (var (id, a) in _acc) Add(id, _minute * 60_000, a.Min, a.Max, a.Last);
        return new ValueRange(f, t, size, full ? "full" : "thin", agg);
    }

    /// <summary>The writer puts a line at least every minute, so a longer silence is a real gap (PLC-Ki was not running).</summary>
    const long HeldFor = 90_000;

    private static void ReadFull(string file, long from, long to, double slice, IEnumerable<string> ids, Action<string, long, double, double, double> add)
    {
        var want = ids.ToHashSet();
        var state = new Dictionary<string, double>();
        // Lines from the last whole set before the window on, so the values held from before it are known
        var pending = new List<string>();
        bool inWindow = false;
        long? prev = null;
        // A value that did not change has no line: it holds from the last line until the next one (or for a minute at most)
        void Hold(long untilExcl)
        {
            if (prev is not { } p) return;
            long end = Math.Min(untilExcl, p + HeldFor);
            for (double tt = p + slice; tt < end; tt += slice)
                if (tt >= from && tt <= to) foreach (var (id, v) in state) add(id, (long)tt, v, v, v);
        }
        void Apply(string line)
        {
            try
            {
                using var doc = JsonDocument.Parse(line);
                long at = doc.RootElement.GetProperty("t").GetInt64();
                Hold(at);
                prev = at;
                if (doc.RootElement.TryGetProperty("k", out _)) state.Clear();
                foreach (var p in doc.RootElement.GetProperty("v").EnumerateObject())
                    if (want.Contains(p.Name) && p.Value.TryGetDouble(out var v)) state[p.Name] = v;
                if (at < from || at > to) return;
                foreach (var (id, v) in state) add(id, at, v, v, v);
            }
            catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException) { /* a line cut off by a power loss */ }
        }
        foreach (var line in Lines(file))
        {
            if (inWindow) { if (Time(line) is { } lt && lt > to) break; Apply(line); continue; }
            var at = Time(line);
            if (at == null) continue;
            if (at < from)
            {
                if (line.Contains("\"k\":1", StringComparison.Ordinal)) pending.Clear();
                pending.Add(line);
                continue;
            }
            inWindow = true;
            foreach (var p in pending) Apply(p);
            pending.Clear();
            if (at > to) break;
            Apply(line);
        }
        // No line at or after the window start (it is just after the last one): what was held before it still counts
        if (!inWindow) foreach (var p in pending) Apply(p);
        Hold(to + 1);
    }

    private static void ReadThin(string file, IEnumerable<string> ids, Action<string, long, double, double, double> add)
    {
        var want = ids.ToHashSet();
        foreach (var line in Lines(file))
            try
            {
                using var doc = JsonDocument.Parse(line);
                long at = doc.RootElement.GetProperty("t").GetInt64();
                foreach (var p in doc.RootElement.GetProperty("v").EnumerateObject())
                    if (want.Contains(p.Name) && p.Value.GetArrayLength() == 3)
                        add(p.Name, at, p.Value[0].GetDouble(), p.Value[1].GetDouble(), p.Value[2].GetDouble());
            }
            catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException) { }
    }

    /// <summary>The time at the start of a line, read without parsing it: {"t":1760000000000,...</summary>
    private static long? Time(string line)
    {
        if (!line.StartsWith("{\"t\":", StringComparison.Ordinal)) return null;
        int end = line.IndexOf(',', 5);
        return end > 5 && long.TryParse(line.AsSpan(5, end - 5), NumberStyles.None, CultureInfo.InvariantCulture, out var t) ? t : null;
    }

    private static IEnumerable<string> Lines(string file)
    {
        if (!File.Exists(file)) yield break;
        FileStream fs;
        try { fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete); }
        catch (IOException) { yield break; }
        using (fs)
        using (var rd = new StreamReader(fs))
            while (rd.ReadLine() is { } line)
                if (line.Length > 0) yield return line;
    }

    // ---------- what is on disk ----------

    /// <summary>The day files of one tier in every machine's folder under data folder\values, oldest first.</summary>
    public static List<(DateOnly Day, string Path)> DayFiles(string valuesRoot, bool full)
    {
        if (!Directory.Exists(valuesRoot)) return new();
        return Directory.GetDirectories(valuesRoot)
            .Select(d => full ? Path.Combine(d, FullFolder) : d)
            .Where(Directory.Exists)
            .SelectMany(ProductionLog.DayFiles)
            .OrderBy(d => d.Day).ToList();
    }

    /// <summary>The first day this machine has values for (null: none yet).</summary>
    public DateOnly? OldestDay() =>
        _dir == null ? null : ProductionLog.DayFiles(_dir).Select(d => (DateOnly?)d.Day).FirstOrDefault();
}
