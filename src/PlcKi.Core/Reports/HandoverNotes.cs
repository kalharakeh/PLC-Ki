using System.Text.Json;
using System.Text.Json.Serialization;

namespace PlcKi.Core.Reports;

/// <summary>
/// One shift's handover note: free text for the next shift. Keeps the shift's name and times as they were, so it still
/// reads right after the shifts are changed in the report setup.
/// </summary>
public sealed record HandoverNote(
    [property: JsonPropertyName("day")] string Day,
    [property: JsonPropertyName("shiftIndex")] int ShiftIndex,
    [property: JsonPropertyName("shift")] string Shift,
    [property: JsonPropertyName("from")] DateTimeOffset From,
    [property: JsonPropertyName("to")] DateTimeOffset To,
    [property: JsonPropertyName("text")] string Text,
    [property: JsonPropertyName("by")] string By,
    [property: JsonPropertyName("at")] DateTimeOffset At,
    [property: JsonPropertyName("editedBy")] string? EditedBy = null,
    [property: JsonPropertyName("editedAt")] DateTimeOffset? EditedAt = null);

/// <summary>
/// The shift handover notes (review 3, item 47): one per shift, written by anyone logged in during that shift or the
/// next one, then kept read-only. One small JSON file per machine (data folder\handover\&lt;machine&gt;.json), outside
/// every folder the storage limits prune: notes are never removed automatically. Thread-safe.
/// </summary>
public sealed class HandoverNotes
{
    public const int MaxLength = 4000;
    /// <summary>The next shift sees the note on every page for this long after it starts.</summary>
    public static readonly TimeSpan ShowFor = TimeSpan.FromHours(2);

    static readonly JsonSerializerOptions Json = new() { WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    private readonly object _lock = new();
    private readonly string? _file;
    private List<HandoverNote> _notes = new();

    /// <param name="file">The machine's file; null keeps the notes in memory (tests).</param>
    public HandoverNotes(string? file)
    {
        _file = file;
        if (file != null && File.Exists(file))
            try { _notes = JsonSerializer.Deserialize<List<HandoverNote>>(File.ReadAllText(file), Json) ?? new(); }
            catch (JsonException) { /* a damaged file: start empty, and the next save writes a good one */ }
    }

    public static string FileFor(string dataDir, string machineSlug) => Path.Combine(dataDir, "handover", machineSlug + ".json");

    public int Count { get { lock (_lock) return _notes.Count; } }

    public HandoverNote? Get(DateOnly day, int index)
    {
        var d = day.ToString("yyyy-MM-dd");
        lock (_lock) return _notes.FirstOrDefault(n => n.Day == d && n.ShiftIndex == index);
    }

    /// <summary>
    /// Writes, changes or (empty text) removes a shift's note. The caller has checked the shift may still be written.
    /// Returns the note, or null when it was removed.
    /// </summary>
    public HandoverNote? Set(DateOnly day, int index, string shift, DateTimeOffset from, DateTimeOffset to, string? text, string user, DateTimeOffset now)
    {
        text = (text ?? "").Trim();
        if (text.Length > MaxLength) throw new ArgumentException($"The note is too long: at most {MaxLength} characters.");
        var d = day.ToString("yyyy-MM-dd");
        lock (_lock)
        {
            var old = _notes.FirstOrDefault(n => n.Day == d && n.ShiftIndex == index);
            if (old != null) _notes.Remove(old);
            HandoverNote? note = null;
            if (text.Length > 0)
            {
                note = old == null
                    ? new HandoverNote(d, index, shift, from, to, text, user, now)
                    : old with { Shift = shift, From = from, To = to, Text = text, EditedBy = user, EditedAt = now };
                _notes.Add(note);
                _notes.Sort((a, b) => a.From.CompareTo(b.From));
            }
            Save();
            return note;
        }
    }

    /// <summary>A shift's note can be written during that shift and the next one: the shift running now, or the one that ended last.</summary>
    public static bool CanWrite(ReportSettings cfg, DateOnly day, int index, DateTimeOffset now) =>
        ReportBuilder.ShiftAt(cfg, now) == (day, index) || ReportBuilder.LastShift(cfg, now) == (day, index);

    /// <summary>
    /// The note to show on every page now: the one the last shift left, while the shift after it is in its first
    /// <see cref="ShowFor"/>. Null when there is none.
    /// </summary>
    public HandoverNote? ToShow(ReportSettings cfg, DateTimeOffset now)
    {
        if (ReportBuilder.ShiftAt(cfg, now) is not { } cur || ReportBuilder.LastShift(cfg, now) is not { } last) return null;
        var (start, _) = ReportBuilder.Window(cur.Day, cfg.Shifts[cur.Index]);
        if (now - start > ShowFor) return null;
        return Get(last.Day, last.Index);
    }

    private void Save()
    {
        if (_file == null) return;
        Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
        var tmp = _file + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(_notes, Json));
        File.Move(tmp, _file, overwrite: true);
    }
}
