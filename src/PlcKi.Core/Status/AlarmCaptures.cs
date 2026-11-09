using System.Globalization;
using System.Text.Json;
using PlcKi.Core.Model;

namespace PlcKi.Core.Status;

/// <summary>A value change in a capture. T is seconds from the moment the alarm came (negative = before it).</summary>
public sealed record CapturePoint(double T, object? Value, string Text);

/// <summary>One tag's values over the capture: the value at the start, then each change.</summary>
/// <param name="Related">The tag is in the alarm's own checks (or is the alarm bit itself).</param>
public sealed record CaptureTrack(string Tag, string Label, string Symbol, string Type, bool Related, object? Start, string StartText, List<CapturePoint> Changes);

/// <summary>A Bool that was on (or off) for less than one normal read: the once-a-second reads would likely miss it.</summary>
public sealed record CapturePulse(string Tag, string Label, double T, double Ms, bool On, string Text);

/// <summary>A stretch of the capture without data (PLC link down). Seconds from the alarm.</summary>
public sealed record CaptureGap(double From, double? To);

/// <summary>
/// The seconds around one alarm, cut from the fast recording: every change of every model tag from Before seconds
/// before the alarm came to After seconds after it.
/// </summary>
/// <param name="Mode">"subscription" (every change the PLC sent) or "reads" (only the normal reads).</param>
/// <param name="OnMs">How long the alarm stayed on, when it went again within the capture.</param>
/// <param name="Blip">The alarm went again faster than one normal read, so the alarm list may never have shown it.</param>
public sealed record AlarmCapture(
    string Id, string AlarmId, string Severity, string AlarmText, DateTimeOffset Time, double Before, double After,
    string Mode, int CycleMs, int ReadMs, double? OnMs, bool Blip,
    List<string> Summary, List<CapturePulse> Pulses, List<CaptureTrack> Tracks, List<CaptureGap> Gaps, int TracksLeftOut);

/// <summary>The short form for lists.</summary>
public sealed record CaptureInfo(string Id, string AlarmId, string Severity, string AlarmText, DateTimeOffset Time,
    string Mode, int CycleMs, bool Blip, double? OnMs, int Pulses, int Tracks, string? Headline);

/// <summary>
/// Watches the fast recording for alarm bits that come on and keeps the seconds around each one ("what happened just
/// before the alarm"). Works on what the recorder already has: with the PLC subscription that is every change sampled
/// every 100 ms, so it also catches alarms and pulses too short for the normal reads. Keeps the newest captures in
/// memory and, when given a folder, on disk. Thread-safe. Read-only towards the PLC.
/// </summary>
public sealed class AlarmCaptureBook
{
    private readonly MachineModel _model;
    private readonly FastRecorder _rec;
    private readonly Dictionary<string, List<AlarmDef>> _alarmsByTag;
    private readonly Dictionary<string, TagDef> _tags;
    private readonly string? _folder;
    private readonly object _lock = new();
    private readonly List<(AlarmDef Alarm, DateTimeOffset Time)> _pending = new();
    private readonly LinkedList<AlarmCapture> _done = new();
    private long _seen;

    public TimeSpan Before { get; }
    public TimeSpan After { get; }
    public int Max { get; private set; }
    public string? Folder => _folder;
    /// <summary>At most this many tracks per capture (the alarm bit and its related tags always first).</summary>
    public int MaxTracks { get; init; } = 40;

    public AlarmCaptureBook(MachineModel model, FastRecorder rec, TimeSpan? before = null, TimeSpan? after = null, int max = 100, string? folder = null)
    {
        _model = model;
        _rec = rec;
        Before = before ?? TimeSpan.FromSeconds(10);
        After = after ?? TimeSpan.FromSeconds(2);
        Max = max;
        _folder = folder;
        _tags = model.Tags.ToDictionary(t => t.Id, StringComparer.Ordinal);
        _alarmsByTag = model.Alarms.Where(a => !string.IsNullOrEmpty(a.Tag)).GroupBy(a => a.Tag, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);
        _seen = rec.LastSeq;
        Load();
    }

    /// <summary>
    /// Looks at the changes since the last call: an alarm bit that went on starts a capture; captures whose After time
    /// has passed are cut and kept. Returns the captures finished now. Call it after every read.
    /// </summary>
    public List<AlarmCapture> Check(DateTimeOffset now, int readMs)
    {
        var finished = new List<AlarmCapture>();
        lock (_lock)
        {
            foreach (var p in _rec.After(_seen))
            {
                _seen = p.Seq;
                if (!p.HadPrev || !_alarmsByTag.TryGetValue(p.TagId, out var alarms)) continue;
                if (IsOn(p.Value) && !IsOn(p.Prev))
                    foreach (var a in alarms) _pending.Add((a, p.Time));
            }
            foreach (var x in _pending.Where(x => now >= x.Time + After).ToList())
            {
                _pending.Remove(x);
                var c = Build(x.Alarm, x.Time, readMs);
                _done.AddLast(c);
                while (_done.Count > Max) _done.RemoveFirst();
                finished.Add(c);
            }
        }
        foreach (var c in finished) Save(c);
        return finished;
    }

    public List<CaptureInfo> List()
    {
        lock (_lock) return _done.Reverse().Select(Info).ToList();
    }

    public AlarmCapture? Get(string id)
    {
        lock (_lock) return _done.FirstOrDefault(c => c.Id == id);
    }

    /// <summary>The newest capture of an alarm (optionally the one closest to a time, e.g. when the alarm list saw it come).</summary>
    public AlarmCapture? Latest(string alarmId, DateTimeOffset? near = null)
    {
        lock (_lock)
        {
            var all = _done.Where(c => c.AlarmId.Equals(alarmId, StringComparison.OrdinalIgnoreCase)).ToList();
            if (all.Count == 0) return null;
            return near is { } t ? all.OrderBy(c => Math.Abs((c.Time - t).TotalSeconds)).First() : all[^1];
        }
    }

    /// <summary>Alarms seen coming whose capture is still being recorded (the After seconds have not passed yet).</summary>
    public List<string> Recording()
    {
        lock (_lock) return _pending.Select(p => p.Alarm.Id).Distinct().ToList();
    }

    public static CaptureInfo Info(AlarmCapture c) => new(c.Id, c.AlarmId, c.Severity, c.AlarmText, c.Time, c.Mode, c.CycleMs,
        c.Blip, c.OnMs, c.Pulses.Count, c.Tracks.Count, c.Summary.Count > 1 ? c.Summary[1] : c.Summary.FirstOrDefault());

    // ---------- cutting a capture ----------

    private AlarmCapture Build(AlarmDef alarm, DateTimeOffset t0, int readMs)
    {
        var from = t0 - Before;
        var to = t0 + After;
        var start = _rec.ValuesBefore(from);
        var points = _rec.Window(from, to);
        double Rel(DateTimeOffset t) => Math.Round((t - t0).TotalSeconds, 3);

        var related = new HashSet<string>(StringComparer.Ordinal) { alarm.Tag };
        foreach (var c in alarm.Checks)
        {
            try { foreach (var id in Expressions.ExprParser.Parse(c.Expr).Identifiers()) related.Add(id); }
            catch (Expressions.ExprException) { /* a bad check is reported by the model check */ }
        }

        var byTag = points.GroupBy(p => p.TagId, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);
        var tracks = new List<CaptureTrack>();
        foreach (var (id, list) in byTag)
        {
            var def = _tags.GetValueOrDefault(id) ?? new TagDef { Id = id, Symbol = id, Type = "Real" };
            var s = start.GetValueOrDefault(id);
            var changes = new List<CapturePoint>();
            object? last = s;
            foreach (var p in list)
            {
                // The first value after (re)connecting is where the tag was, not a change, unless it differs from before
                if (!p.HadPrev && (last == null || Equals(p.Value, last)))
                {
                    if (last == null && changes.Count == 0) s = p.Value;
                    last = p.Value;
                    continue;
                }
                changes.Add(new CapturePoint(Rel(p.Time), p.Value, ValueFormat.Format(def, p.Value)));
                last = p.Value;
            }
            if (changes.Count == 0) continue;
            tracks.Add(new CaptureTrack(id, Label(def), def.Symbol, def.Type, related.Contains(id), s, s == null ? "?" : ValueFormat.Format(def, s), changes));
        }
        // Related tags that did not change are still worth showing: "the leak rate stayed at 0.0"
        foreach (var id in related.Where(r => _tags.ContainsKey(r) && !tracks.Any(k => k.Tag == r)))
        {
            var def = _tags[id];
            var s = start.GetValueOrDefault(id) ?? byTag.GetValueOrDefault(id)?.FirstOrDefault(p => p.Time <= t0)?.Value;
            if (s == null) continue;
            tracks.Add(new CaptureTrack(id, Label(def), def.Symbol, def.Type, true, s, ValueFormat.Format(def, s), new()));
        }

        // The alarm bit first, then its own checks, then whatever changed last before the alarm
        double LastBefore(CaptureTrack k) => k.Changes.Where(c => c.T <= 0).Select(c => c.T).DefaultIfEmpty(double.MinValue).Max();
        tracks = tracks.OrderBy(k => k.Tag == alarm.Tag ? 0 : k.Related ? 1 : 2)
            .ThenByDescending(LastBefore)
            .ThenBy(k => k.Label, StringComparer.OrdinalIgnoreCase).ToList();
        var leftOut = Math.Max(0, tracks.Count - MaxTracks);
        tracks = tracks.Take(MaxTracks).ToList();

        // How long the alarm stayed on
        double? onMs = null;
        var off = points.FirstOrDefault(p => p.TagId == alarm.Tag && p.Time > t0 && !IsOn(p.Value));
        if (off != null) onMs = Math.Round((off.Time - t0).TotalMilliseconds);
        var blip = onMs != null && onMs < readMs;

        var pulses = Pulses(tracks, readMs, alarm.Tag);
        var gaps = _rec.Gaps(from, to).Select(g => new CaptureGap(Math.Max(Rel(g.From), -Before.TotalSeconds), g.To is { } e ? Math.Min(Rel(e), After.TotalSeconds) : null)).ToList();
        var mode = _rec.Mode == "off" ? "reads" : _rec.Mode;
        var cycle = _rec.Mode == "subscription" ? _rec.CycleMs : readMs;

        var summary = new List<string>
        {
            mode == "subscription"
                ? $"Every change the PLC sent, sampled every {cycle} ms, from {Before.TotalSeconds:0} s before the alarm to {After.TotalSeconds:0} s after."
                : $"Only from the normal reads (every {Secs(readMs)}), so changes shorter than that can be missing." +
                  (_rec.Note is { Length: > 0 } n ? " Fast capture is off: " + n : ""),
        };
        if (blip) summary.Add($"The alarm was on for only {Ms(onMs!.Value)}, shorter than one normal read: the alarm list may never have shown it.");
        else if (onMs != null) summary.Add($"The alarm went again after {Ms(onMs.Value)}.");

        // What changed last before the alarm (the likely cause), and what changed in the same sample (often its effects)
        var lastChanges = tracks.Where(k => k.Tag != alarm.Tag)
            .SelectMany(k => k.Changes.Where(c => c.T < 0 && c.T >= -Before.TotalSeconds).Select(c => (k, c)))
            .OrderByDescending(x => x.c.T).Take(3).ToList();
        if (lastChanges.Count > 0)
            summary.Add("Right before it: " + string.Join("; ", lastChanges.Select(x => $"{x.k.Label} became {x.c.Text} {Ago(x.c.T)}")) + ".");
        else
            summary.Add($"No other model tag changed in the {Before.TotalSeconds:0} s before the alarm.");
        var together = tracks.Where(k => k.Tag != alarm.Tag && k.Changes.Any(c => c.T == 0)).ToList();
        if (together.Count > 0)
            summary.Add("Together with the alarm: " + string.Join(", ", together.Take(4).Select(k => $"{k.Label} became {k.Changes.First(c => c.T == 0).Text}")) +
                (together.Count > 4 ? $" and {together.Count - 4} more" : "") + ".");
        var beforePulses = pulses.Where(p => p.T <= 0).ToList();
        if (beforePulses.Count > 0)
            summary.Add($"{beforePulses.Count} short pulse{(beforePulses.Count > 1 ? "s" : "")} before it that the normal reads would likely miss: " +
                string.Join("; ", beforePulses.Take(3).Select(p => p.Text)) + (beforePulses.Count > 3 ? "; ..." : "") + ".");
        if (gaps.Count > 0) summary.Add("The PLC link was down for part of this time, so some changes are missing.");
        if (leftOut > 0) summary.Add($"{leftOut} more tag{(leftOut > 1 ? "s" : "")} changed too; only the {MaxTracks} most relevant are kept.");

        var captureId = $"{t0.ToUniversalTime():yyyyMMdd-HHmmss-fff}-{Safe(alarm.Id)}";
        return new AlarmCapture(captureId, alarm.Id, alarm.Severity, alarm.Text, t0, Before.TotalSeconds, After.TotalSeconds, mode, cycle, readMs,
            onMs, blip, summary, pulses, tracks, gaps, leftOut);
    }

    private static List<CapturePulse> Pulses(List<CaptureTrack> tracks, int readMs, string alarmTag)
    {
        var list = new List<CapturePulse>();
        foreach (var k in tracks.Where(k => k.Type == "Bool" && k.Tag != alarmTag))
        {
            for (int i = 0; i + 1 < k.Changes.Count; i++)
            {
                var a = k.Changes[i];
                var b = k.Changes[i + 1];
                var ms = Math.Round((b.T - a.T) * 1000);
                if (ms >= readMs) continue;
                var on = IsOn(a.Value);
                list.Add(new CapturePulse(k.Tag, k.Label, a.T, ms, on,
                    $"{k.Label} was {(on ? "on" : "off")} for {Ms(ms)} {Ago(a.T)}"));
            }
        }
        return list.OrderBy(p => p.T).ToList();
    }

    private static bool IsOn(object? v) => v switch
    {
        bool b => b,
        double d => d != 0,
        null => false,
        IConvertible c => Convert.ToDouble(c, CultureInfo.InvariantCulture) != 0,
        _ => false,
    };

    private static string Label(TagDef t) => string.IsNullOrWhiteSpace(t.Label) ? t.Id : $"{t.Label} ({t.Id})";
    private static string Ms(double ms) => ms < 1000 ? $"{ms:0} ms" : $"{ms / 1000:0.0#} s";
    private static string Secs(int ms) => ms % 1000 == 0 ? $"{ms / 1000} s" : $"{ms} ms";
    private static string Ago(double t) => t switch
    {
        0 => "at the same moment",
        < 0 => $"{Ms(-t * 1000)} before the alarm",
        _ => $"{Ms(t * 1000)} after the alarm",
    };
    private static string Safe(string s) => new(s.Select(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_' or '.' ? ch : '_').ToArray());

    // ---------- on disk ----------

    private static readonly JsonSerializerOptions Json = new(MachineModel.JsonOptions) { WriteIndented = false };

    private void Save(AlarmCapture c)
    {
        if (_folder == null) return;
        try
        {
            Directory.CreateDirectory(_folder);
            File.WriteAllText(Path.Combine(_folder, c.Id + ".json"), JsonSerializer.Serialize(c, Json));
            foreach (var old in Directory.GetFiles(_folder, "*.json").OrderByDescending(f => Path.GetFileName(f), StringComparer.Ordinal).Skip(Max))
                File.Delete(old);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* kept in memory anyway */ }
    }

    /// <summary>Sets the limit and removes the oldest recordings over it, in memory and on disk. Returns how many were (or would be) removed.</summary>
    public int Prune(int max, bool dryRun = false)
    {
        max = Math.Max(1, max);
        lock (_lock)
        {
            var onDisk = _folder != null && Directory.Exists(_folder) ? Directory.GetFiles(_folder, "*.json").Length : 0;
            var n = Math.Max(Math.Max(0, _done.Count - max), Math.Max(0, onDisk - max));
            if (dryRun) return n;
            Max = max;
            while (_done.Count > Max) _done.RemoveFirst();
            if (_folder != null && Directory.Exists(_folder))
                foreach (var old in Directory.GetFiles(_folder, "*.json").OrderByDescending(f => Path.GetFileName(f), StringComparer.Ordinal).Skip(Max))
                    try { File.Delete(old); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            return n;
        }
    }

    /// <summary>Forgets every finished recording (the files are removed by the caller). Recordings still being cut stay.</summary>
    public void Clear()
    {
        lock (_lock) _done.Clear();
    }

    private void Load()
    {
        if (_folder == null || !Directory.Exists(_folder)) return;
        foreach (var f in Directory.GetFiles(_folder, "*.json").OrderBy(f => Path.GetFileName(f), StringComparer.Ordinal).TakeLast(Max))
        {
            try
            {
                if (JsonSerializer.Deserialize<AlarmCapture>(File.ReadAllText(f), Json) is { } c) _done.AddLast(c);
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { /* skip a broken file */ }
        }
    }
}
