using System.Text.Json;
using System.Text.Json.Nodes;

namespace PlcKi.Plant;

/// <summary>
/// Each login's own choices (review 52): light or dark, alarm sound, spacing, chosen trend values, start page, ...
/// kept in prefs.json in the data folder, so they follow the person to another PC or a phone. Logged out (public view),
/// the page keeps them in the browser as before. Only known keys are kept, each small, so the file cannot grow without end.
/// </summary>
public sealed class PreferenceStore
{
    /// <summary>The choices a page may keep here. Anything else sent is dropped.</summary>
    public static readonly IReadOnlySet<string> Keys = new HashSet<string>(StringComparer.Ordinal)
    {
        "theme",         // "light" | "dark" | "auto"
        "screen",        // { sound, notify, awake, density }
        "trendTags",     // tag ids picked for Values over time
        "timeline",      // "shift" | "24h" | "7d"
        "netFilter",     // Program: "all" | "holding"
        "getStarted",    // the setup guide was seen
        "handoverSeen",  // the last shift handover note that was acknowledged
        "startPage",     // "#/..." or "" for the role's start page
        "alarmSound",    // per severity and station (review 56)
        "format",        // time, date and number format (review 57)
        "language",      // "en" | "de" (review 49); none = the plant's
    };

    public const int MaxValueBytes = 8 * 1024;

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private readonly string _file;
    private readonly object _lock = new();
    private JsonObject _all;

    public PreferenceStore(string file)
    {
        _file = file;
        try { _all = File.Exists(file) ? JsonNode.Parse(File.ReadAllText(file)) as JsonObject ?? new() : new(); }
        catch (Exception) { _all = new(); }      // a broken file: everyone starts from the defaults again
    }

    private static string Key(string user) => user.Trim().ToLowerInvariant();

    /// <summary>This login's choices (a copy), empty when none are kept yet.</summary>
    public JsonObject Get(string user)
    {
        lock (_lock) return _all[Key(user)] is JsonObject o ? (JsonObject)o.DeepClone() : new JsonObject();
    }

    /// <summary>
    /// Changes some choices and keeps the others. A null value removes that choice. Unknown keys and values over
    /// <see cref="MaxValueBytes"/> are refused with a PlantException, so a page bug cannot fill the disk.
    /// </summary>
    public JsonObject Set(string user, JsonObject changes)
    {
        foreach (var (k, v) in changes)
        {
            if (!Keys.Contains(k)) throw new PlantException($"'{k}' is not a setting that is kept per login.");
            if (v != null && v.ToJsonString().Length > MaxValueBytes) throw new PlantException($"The value for '{k}' is too large.");
        }
        lock (_lock)
        {
            var mine = _all[Key(user)] as JsonObject ?? new JsonObject();
            var next = (JsonObject)mine.DeepClone();
            foreach (var (k, v) in changes)
                if (v == null) next.Remove(k); else next[k] = v.DeepClone();
            var all = (JsonObject)_all.DeepClone();
            all[Key(user)] = next;
            Save(all);
            return (JsonObject)next.DeepClone();
        }
    }

    /// <summary>A deleted login takes its choices with it, so a new login with the same name starts fresh.</summary>
    public void Remove(string user)
    {
        lock (_lock)
        {
            if (!_all.ContainsKey(Key(user))) return;
            var all = (JsonObject)_all.DeepClone();
            all.Remove(Key(user));
            Save(all);
        }
    }

    private void Save(JsonObject all)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_file))!);
        AtomicFile.Save(_file, all.ToJsonString(Json));
        _all = all;
    }
}
