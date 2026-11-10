namespace PlcKi.Core.Storage;

/// <summary>
/// How much PLC-Ki keeps on disk (Setup › Settings › Storage, saved in settings.json under Plant:Retention).
/// Pruning applies them at start and once a day. Notes, documents, logins, settings and keys have no limit here:
/// they are never removed automatically.
/// </summary>
public sealed record RetentionLimits
{
    /// <summary>Production log (shift, OEE and fault reports): day files. 400 days lets a report compare with the same week last year.</summary>
    public int ProductionDays { get; init; } = 400;
    /// <summary>Machine events (alarms, halts, blocked devices, CPU mode, the PLC link, values written): day files.
    /// As long as the production log, so the shift log reaches as far back as the shift report.</summary>
    public int EventDays { get; init; } = 400;
    /// <summary>Step changes and outputs switched on or off: about 20 per cycle, so 1 to 5 MB a day. Kept for a shorter time.</summary>
    public int StepEventDays { get; init; } = 30;
    /// <summary>Values at the full rate (every read): big, about 16 MB a day for the demo and several times that on a real machine.</summary>
    public int ValueFullDays { get; init; } = 2;
    /// <summary>Values as one minute per line (lowest, highest, last): about 1 to 5 MB a day, so a week of trends stays small.</summary>
    public int ValueDays { get; init; } = 90;
    /// <summary>Value-change log: older files go after this many days (2 years: an audit trail of who changed what)...</summary>
    public int ChangeLogDays { get; init; } = 730;
    /// <summary>...or when all its files together are over this size, whichever comes first.</summary>
    public int ChangeLogMb { get; init; } = 50;
    /// <summary>Alarm recordings (the seconds around an alarm), per machine model.</summary>
    public int Recordings { get; init; } = 100;
    /// <summary>Alarm memory: occurrences. Ones with a technician's note are never removed by this limit.</summary>
    public int AlarmOccurrences { get; init; } = 20_000;

    public static readonly RetentionLimits Default = new();

    /// <summary>What is wrong with these limits, in plain words; null when they are fine.</summary>
    public string? Problem() =>
        ProductionDays is < 7 or > 3650 ? "Production log: keep 7 to 3650 days."
        : EventDays is < 7 or > 3650 ? "Machine events: keep 7 to 3650 days."
        : StepEventDays is < 1 or > 3650 ? "Steps and outputs: keep 1 to 3650 days."
        : ValueFullDays is < 1 or > 14 ? "Values at the full rate: keep 1 to 14 days."
        : ValueDays is < 7 or > 3650 ? "Values, a minute at a time: keep 7 to 3650 days."
        : ValueDays < ValueFullDays ? "Values, a minute at a time, must be kept at least as long as the full rate."
        : ChangeLogDays is < 30 or > 3650 ? "Change log: keep 30 to 3650 days."
        : ChangeLogMb is < 1 or > 2000 ? "Change log: 1 to 2000 MB."
        : Recordings is < 1 or > 2000 ? "Alarm recordings: keep 1 to 2000."
        : AlarmOccurrences is < 100 or > 200_000 ? "Alarm memory: keep 100 to 200000 occurrences."
        : null;
}
