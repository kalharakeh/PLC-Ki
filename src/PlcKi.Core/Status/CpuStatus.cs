namespace PlcKi.Core.Status;

/// <summary>
/// The CPU's operating mode as last read, for every page: State = Run, Stop, Startup, Halt, Defective, Unknown.
/// Error is set when the mode could not be read. Only a mode that was really read and is not RUN counts as stopped:
/// a failed read ("Unknown", an error) never does, so a flaky read cannot claim the program stopped.
/// </summary>
/// <param name="Since">When PLC-Ki saw the CPU go into this mode (server time).</param>
/// <param name="AtStart">The CPU was already in this mode at the first read, so it may have been in it for longer.</param>
public sealed record CpuStatus(string State, string? Switch, DateTimeOffset? Since, string? Error = null, bool AtStart = false)
{
    private static readonly HashSet<string> NotRunning = new(StringComparer.OrdinalIgnoreCase)
    {
        "Stop", "Startup", "Halt", "Hold", "Defective", "Defect",
    };

    /// <summary>True only for a real non-RUN mode (STOP, startup, hold, defect).</summary>
    public bool Stopped => Error == null && NotRunning.Contains(State.Trim());

    /// <summary>True when the mode was read and is RUN.</summary>
    public bool Running => Error == null && string.Equals(State.Trim(), "Run", StringComparison.OrdinalIgnoreCase);

    /// <summary>"STOP", "STARTUP", "HOLD", "DEFECT", "RUN"; null when not known.</summary>
    public string? Word => Error != null ? null : State.Trim().ToLowerInvariant() switch
    {
        "stop" => "STOP",
        "startup" => "STARTUP",
        "halt" or "hold" => "HOLD",
        "defective" or "defect" => "DEFECT",
        "run" => "RUN",
        _ => null,
    };
}

/// <summary>
/// Remembers since when the CPU is in its mode. A failed read keeps what was known about the mode before, so a single
/// bad read does not reset the "since" time; it is passed on with its error, which is never shown as STOP.
/// </summary>
public sealed class CpuModeTracker
{
    private readonly object _lock = new();
    private string? _state;
    private DateTimeOffset? _since;
    private bool _atStart;
    private bool _seen;
    private CpuStatus? _current;

    public CpuStatus? Current { get { lock (_lock) return _current; } }

    /// <summary>Takes a new reading. state null: the source cannot tell the mode (test files without one).</summary>
    public CpuStatus? Update(string? state, string? modeSwitch, string? error, DateTimeOffset time)
    {
        lock (_lock)
        {
            if (state == null && error == null) return _current = null;
            var known = error == null && !string.IsNullOrWhiteSpace(state)
                && !string.Equals(state.Trim(), "Unknown", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(state.Trim(), "NotSupported", StringComparison.OrdinalIgnoreCase);
            if (!known)
                return _current = new CpuStatus(string.IsNullOrWhiteSpace(state) ? "Unknown" : state.Trim(), modeSwitch, null,
                    error ?? "The CPU did not say its mode.");
            var s = state!.Trim();
            if (!string.Equals(s, _state, StringComparison.OrdinalIgnoreCase))
            {
                _atStart = !_seen;
                _since = time;
                _state = s;
            }
            _seen = true;
            return _current = new CpuStatus(s, modeSwitch, _since, null, _atStart);
        }
    }

    /// <summary>The link is gone: the mode read before is no longer true now.</summary>
    public void Clear() { lock (_lock) _current = null; }
}
