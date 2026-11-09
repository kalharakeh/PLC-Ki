using System.Collections.Concurrent;
using System.Globalization;
using System.Text.RegularExpressions;

namespace PlcKi.Core.Storage;

/// <summary>What one pruning or clearing step removed (or would remove): for the log and the confirmation sheet.</summary>
public sealed record Removed(string Kind, int Count, long Bytes, string Text);

/// <summary>
/// A JSON-lines log that never becomes one endless file. The live file keeps its name (value-changes.jsonl), so
/// anything that reads it keeps working. When it reaches <see cref="RotateBytes"/>, or a new month starts, it is
/// renamed to value-changes-yyyy-MM-dd-HHmmss.jsonl: the time of its last line. Readers walk the live file and then
/// the older files. Pruning removes whole older files only, never the live one and never part of a file.
/// One instance per file (<see cref="For"/>), so every writer shares one lock. Thread-safe.
/// </summary>
public sealed class JsonLinesLog
{
    private static readonly ConcurrentDictionary<string, JsonLinesLog> Shared = new(StringComparer.OrdinalIgnoreCase);
    public static JsonLinesLog For(string liveFile) => Shared.GetOrAdd(Path.GetFullPath(liveFile), p => new JsonLinesLog(p));

    public const long DefaultRotateBytes = 5L * 1024 * 1024;
    private readonly object _lock = new();
    private readonly string _dir, _stem, _ext;
    private readonly Regex _rotated;

    private JsonLinesLog(string live)
    {
        Live = live;
        _dir = Path.GetDirectoryName(live)!;
        _stem = Path.GetFileNameWithoutExtension(live);
        _ext = Path.GetExtension(live);
        _rotated = new Regex("^" + Regex.Escape(_stem) + @"-(\d{4}-\d{2}-\d{2}-\d{6})(?:-(\d+))?" + Regex.Escape(_ext) + "$", RegexOptions.IgnoreCase);
    }

    /// <summary>The file new lines go to.</summary>
    public string Live { get; }
    /// <summary>The live file is put aside when it reaches this size (set from the MB limit, so it is a fraction of it).</summary>
    public long RotateBytes { get; set; } = DefaultRotateBytes;

    public void Append(string line, DateTimeOffset now)
    {
        lock (_lock)
        {
            RotateIfDue(now);
            Directory.CreateDirectory(_dir);
            File.AppendAllText(Live, line + Environment.NewLine);
        }
    }

    private void RotateIfDue(DateTimeOffset now)
    {
        if (!File.Exists(Live)) return;
        var fi = new FileInfo(Live);
        var last = fi.LastWriteTime;
        var local = now.LocalDateTime;
        if (fi.Length >= RotateBytes || last.Year != local.Year || last.Month != local.Month) Rotate(fi);
    }

    private void Rotate(FileInfo live)
    {
        var stamp = live.LastWriteTime.ToString("yyyy-MM-dd-HHmmss", CultureInfo.InvariantCulture);
        var target = Path.Combine(_dir, $"{_stem}-{stamp}{_ext}");
        for (int n = 2; File.Exists(target); n++) target = Path.Combine(_dir, $"{_stem}-{stamp}-{n}{_ext}");
        try { File.Move(Live, target); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* stays live; tried again next time */ }
    }

    /// <summary>The older (rotated) files, oldest first, with the time of their last line.</summary>
    public List<(DateTime End, string Path, long Bytes)> Older()
    {
        var list = new List<(DateTime End, int N, string Path, long Bytes)>();
        if (!Directory.Exists(_dir)) return new();
        foreach (var f in Directory.GetFiles(_dir, _stem + "-*" + _ext))
        {
            var m = _rotated.Match(Path.GetFileName(f));
            if (!m.Success || !DateTime.TryParseExact(m.Groups[1].Value, "yyyy-MM-dd-HHmmss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var end)) continue;
            long size = 0;
            try { size = new FileInfo(f).Length; } catch (IOException) { }
            list.Add((end, m.Groups[2].Success ? int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture) : 1, f, size));
        }
        return list.OrderBy(x => x.End).ThenBy(x => x.N).Select(x => (x.End, x.Path, x.Bytes)).ToList();
    }

    /// <summary>Every file of this log, oldest first (the live file last).</summary>
    public List<string> Files()
    {
        lock (_lock)
        {
            var all = Older().Select(x => x.Path).ToList();
            if (File.Exists(Live)) all.Add(Live);
            return all;
        }
    }

    /// <summary>The lines, newest first, across the live file and the older ones. Reads one file at a time.</summary>
    public IEnumerable<string> NewestFirst()
    {
        var files = Files();
        for (int i = files.Count - 1; i >= 0; i--)
        {
            var lines = ReadLines(files[i]);
            for (int k = lines.Length - 1; k >= 0; k--)
                if (!string.IsNullOrWhiteSpace(lines[k])) yield return lines[k];
        }
    }

    /// <summary>The lines, oldest first, across every file.</summary>
    public IEnumerable<string> OldestFirst()
    {
        foreach (var f in Files())
            foreach (var line in ReadLines(f))
                if (!string.IsNullOrWhiteSpace(line)) yield return line;
    }

    private string[] ReadLines(string file)
    {
        lock (_lock)
        {
            try { return File.Exists(file) ? File.ReadAllLines(file) : Array.Empty<string>(); }
            catch (IOException) { return Array.Empty<string>(); }
        }
    }

    /// <summary>
    /// Removes older files whose last line is more than <paramref name="days"/> old, then the oldest ones while all
    /// files together are over <paramref name="maxBytes"/>. The live file is never removed (it is put aside first
    /// when it alone is too big). With dryRun nothing changes: it says what would go.
    /// </summary>
    public Removed Prune(int days, long maxBytes, DateTimeOffset now, bool dryRun = false)
    {
        lock (_lock)
        {
            RotateBytes = Math.Clamp(maxBytes / 5, 64 * 1024, DefaultRotateBytes);
            if (!dryRun && File.Exists(Live) && new FileInfo(Live).Length > RotateBytes) Rotate(new FileInfo(Live));
            var older = Older();
            var cutoff = now.LocalDateTime.AddDays(-days);
            var drop = older.Where(o => o.End < cutoff).ToList();
            long total = older.Sum(o => o.Bytes) + (File.Exists(Live) ? new FileInfo(Live).Length : 0) - drop.Sum(o => o.Bytes);
            foreach (var o in older.Where(o => !drop.Contains(o)))
            {
                if (total <= maxBytes) break;
                drop.Add(o);
                total -= o.Bytes;
            }
            if (!dryRun)
                foreach (var o in drop)
                    try { File.Delete(o.Path); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            var text = drop.Count == 0 ? "" : $"{drop.Count} older file{(drop.Count == 1 ? "" : "s")} up to {drop.Max(o => o.End):yyyy-MM-dd}";
            return new Removed("changes", drop.Count, drop.Sum(o => o.Bytes), text);
        }
    }

    /// <summary>Removes every file of this log (the live one too). Only on an administrator's confirmed request.</summary>
    public Removed Clear()
    {
        lock (_lock)
        {
            var files = Older().Select(o => o.Path).ToList();
            if (File.Exists(Live)) files.Add(Live);
            long bytes = 0;
            int n = 0;
            foreach (var f in files)
            {
                try { var len = new FileInfo(f).Length; File.Delete(f); bytes += len; n++; }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
            return new Removed("changes", n, bytes, $"{n} file{(n == 1 ? "" : "s")}");
        }
    }
}
