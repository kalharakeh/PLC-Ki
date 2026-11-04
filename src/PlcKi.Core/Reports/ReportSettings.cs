using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PlcKi.Core.Reports;

/// <summary>One shift: a name and its clock times. An end before the start runs past midnight (22:00 to 06:00).</summary>
public sealed class ShiftDef
{
    public string Name { get; set; } = "";
    public string Start { get; set; } = "06:00";
    public string End { get; set; } = "14:00";
    /// <summary>Planned stops in this shift (breaks, cleaning), in minutes. They are not counted as lost time.</summary>
    public int PlannedStopMinutes { get; set; }

    public static TimeSpan? ParseTime(string? s) =>
        TimeSpan.TryParseExact((s ?? "").Trim(), new[] { @"h\:mm", @"hh\:mm" }, CultureInfo.InvariantCulture, out var t) && t < TimeSpan.FromDays(1) ? t : null;
}

/// <summary>
/// What the reports need to know about this machine, set on the Reports page by an administrator.
/// Everything is optional: without a counter PLC-Ki counts finished sequence cycles, and without an ideal
/// cycle time it uses the fastest cycles it has seen.
/// </summary>
public sealed class ReportSettings
{
    public List<ShiftDef> Shifts { get; set; } = new()
    {
        new() { Name = "Early", Start = "06:00", End = "14:00" },
        new() { Name = "Late", Start = "14:00", End = "22:00" },
        new() { Name = "Night", Start = "22:00", End = "06:00" },
    };
    /// <summary>Tag id of a counter that goes up by one per good part (a model tag). Empty: count finished cycles.</summary>
    public string? GoodCountTag { get; set; }
    /// <summary>Tag id of a counter that goes up by one per rejected part. Empty: quality is not known.</summary>
    public string? BadCountTag { get; set; }
    /// <summary>The sequence whose return to its first step is one finished cycle. Empty: the first sequence of the model.</summary>
    public string? CycleSequence { get; set; }
    /// <summary>The fastest a cycle can be, in seconds, for performance. Empty: estimated from the fastest cycles seen.</summary>
    public double? IdealCycleSeconds { get; set; }

    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>The problems that make these settings unusable (empty when fine).</summary>
    public List<string> Problems()
    {
        var list = new List<string>();
        if (Shifts.Count == 0) list.Add("Add at least one shift.");
        foreach (var s in Shifts)
        {
            var name = string.IsNullOrWhiteSpace(s.Name) ? "A shift" : $"Shift \"{s.Name}\"";
            if (string.IsNullOrWhiteSpace(s.Name)) list.Add("Every shift needs a name.");
            if (ShiftDef.ParseTime(s.Start) == null) list.Add($"{name}: the start \"{s.Start}\" is not a time like 06:00.");
            if (ShiftDef.ParseTime(s.End) == null) list.Add($"{name}: the end \"{s.End}\" is not a time like 14:00.");
            if (s.PlannedStopMinutes < 0 || s.PlannedStopMinutes > 600) list.Add($"{name}: planned stops must be 0 to 600 minutes.");
        }
        if (Shifts.Select(s => s.Name.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).Count() != Shifts.Count)
            list.Add("Two shifts have the same name.");
        if (IdealCycleSeconds is { } c && (c <= 0 || c > 86400)) list.Add("The ideal cycle time must be between 0 and 86400 seconds.");
        return list;
    }

    public static ReportSettings Load(string? file)
    {
        try
        {
            if (file != null && File.Exists(file))
                return JsonSerializer.Deserialize<ReportSettings>(File.ReadAllText(file), Json) ?? new();
        }
        catch (JsonException) { /* a broken file: start from the defaults, Save writes a good one */ }
        return new();
    }

    public void Save(string file)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        var tmp = file + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this, Json));
        File.Move(tmp, file, overwrite: true);
    }
}
