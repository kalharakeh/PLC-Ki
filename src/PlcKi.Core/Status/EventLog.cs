using System.Text.Json;
using System.Text.Json.Serialization;
using PlcKi.Core.Reports;

namespace PlcKi.Core.Status;

/// <summary>
/// Keeps every machine event on disk, one JSON-lines file per day, so a shift's events survive a restart and the
/// 1,000 that MachineHistory holds in memory. Step changes and outputs switched on or off are many (about 20 per
/// cycle), so they go into their own folder with a shorter limit (Setup › Settings › Storage):
///   data folder\events\&lt;machine&gt;\2026-11-10.jsonl        alarms, halts, blocked, CPU mode, link, values written...
///   data folder\events\&lt;machine&gt;\steps\2026-11-10.jsonl  steps and outputs
/// Events are filed by when they happened (a late one goes into its own day) and sorted when read. Read-only
/// towards the PLC. Thread-safe.
/// </summary>
public sealed class EventLog
{
    /// <summary>The busy kinds, kept for a shorter time.</summary>
    public static readonly IReadOnlySet<string> Busy = new HashSet<string> { "step", "output" };
    public const string StepsFolder = "steps";

    static readonly JsonSerializerOptions Json = new() { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    private readonly object _lock = new();
    private readonly string? _dir;
    private readonly List<MachineEvent>? _mem;    // no folder: memory only (tests)

    /// <param name="dir">This machine's folder (data folder\events\&lt;machine&gt;); null keeps everything in memory.</param>
    public EventLog(string? dir)
    {
        _dir = dir;
        if (dir == null) _mem = new();
    }

    public string? Folder => _dir;

    /// <summary>The folder for one machine's events.</summary>
    public static string FolderFor(string dataDir, string machineSlug) => Path.Combine(dataDir, "events", machineSlug);

    public void Write(IReadOnlyList<MachineEvent> events)
    {
        if (events.Count == 0) return;
        lock (_lock)
        {
            if (_mem != null) { _mem.AddRange(events); return; }
            try
            {
                foreach (var g in events.GroupBy(e => (Day: e.Time.ToLocalTime().Date, Busy: Busy.Contains(e.Kind))))
                {
                    var f = FileFor(g.Key.Day, g.Key.Busy);
                    Directory.CreateDirectory(Path.GetDirectoryName(f)!);
                    File.AppendAllLines(f, g.Select(e => JsonSerializer.Serialize(Line.From(e), Json)));
                }
            }
            catch (IOException) { /* a full disk or a locked file must not stop the live pages */ }
            catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>The events from <paramref name="from"/> to <paramref name="to"/>, oldest first. kinds: only these (null: all).</summary>
    public List<MachineEvent> Read(DateTimeOffset from, DateTimeOffset to, IReadOnlySet<string>? kinds = null)
    {
        bool Want(MachineEvent e) => e.Time >= from && e.Time <= to && (kinds == null || kinds.Contains(e.Kind));
        lock (_lock)
        {
            if (_mem != null) return _mem.Where(Want).OrderBy(e => e.Time).ToList();
            bool rare = kinds == null || kinds.Any(k => !Busy.Contains(k));
            bool busy = kinds == null || kinds.Any(Busy.Contains);
            var list = new List<MachineEvent>();
            for (var day = from.ToLocalTime().Date; day <= to.ToLocalTime().Date; day = day.AddDays(1))
            {
                if (rare) ReadFile(FileFor(day, false), list, Want);
                if (busy) ReadFile(FileFor(day, true), list, Want);
            }
            // Stable: events of the same moment keep the order they were written in
            return list.OrderBy(e => e.Time).ToList();
        }
    }

    private static void ReadFile(string f, List<MachineEvent> into, Func<MachineEvent, bool> want)
    {
        if (!File.Exists(f)) return;
        try
        {
            foreach (var line in File.ReadLines(f))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try { if (JsonSerializer.Deserialize<Line>(line, Json)?.ToEvent() is { } e && want(e)) into.Add(e); }
                catch (JsonException) { /* a line cut off by a power loss */ }
            }
        }
        catch (IOException) { }
    }

    private string FileFor(DateTime day, bool busy) =>
        Path.Combine(busy ? Path.Combine(_dir!, StepsFolder) : _dir!, day.ToString("yyyy-MM-dd") + ".jsonl");

    /// <summary>The day files of one kind (busy: the steps folder) in every machine's folder under data folder\events, oldest first.</summary>
    public static List<(DateOnly Day, string Path)> DayFiles(string eventsRoot, bool busy)
    {
        if (!Directory.Exists(eventsRoot)) return new();
        return Directory.GetDirectories(eventsRoot)
            .Select(d => busy ? Path.Combine(d, StepsFolder) : d)
            .Where(Directory.Exists)
            .SelectMany(ProductionLog.DayFiles)
            .OrderBy(d => d.Day).ToList();
    }

    /// <summary>One line in a day file: short names, empty fields left out.</summary>
    private sealed record Line(
        [property: JsonPropertyName("t")] DateTimeOffset T,
        [property: JsonPropertyName("k")] string K,
        [property: JsonPropertyName("text")] string Text,
        [property: JsonPropertyName("alarm")] string? Alarm = null,
        [property: JsonPropertyName("seq")] string? Seq = null,
        [property: JsonPropertyName("step")] int? Step = null,
        [property: JsonPropertyName("dev")] string? Device = null,
        [property: JsonPropertyName("tag")] string? Tag = null)
    {
        public static Line From(MachineEvent e) => new(e.Time, e.Kind, e.Text, e.Alarm, e.Seq, e.Step, e.Device, e.Tag);
        public MachineEvent ToEvent() => new(T, K, Text) { Alarm = Alarm, Seq = Seq, Step = Step, Device = Device, Tag = Tag };
    }
}
