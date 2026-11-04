using System.Text.Json;
using System.Text.Json.Serialization;
using PlcKi.Core.Model;
using PlcKi.Core.Status;

namespace PlcKi.Core.Reports;

/// <summary>
/// One line of the production log.
///   state: the machine changed to S (running, idle, down, nolink), Why says what it was doing or what stopped it
///   count: parts counted since the line before (Good and Bad are null when no counter is set)
///   cycle: Seq finished one cycle in Sec seconds
///   alive: PLC-Ki was running (written once a minute when nothing else was), so a gap means it was not
///   start: PLC-Ki started watching
/// </summary>
public sealed record ProdRecord(
    [property: JsonPropertyName("t")] DateTimeOffset T,
    [property: JsonPropertyName("k")] string K,
    [property: JsonPropertyName("s")] string? S = null,
    [property: JsonPropertyName("why")] string? Why = null,
    [property: JsonPropertyName("alarm")] string? Alarm = null,
    [property: JsonPropertyName("good")] int? Good = null,
    [property: JsonPropertyName("bad")] int? Bad = null,
    [property: JsonPropertyName("seq")] string? Seq = null,
    [property: JsonPropertyName("sec")] double? Sec = null);

/// <summary>
/// Records what the machine did, for the shift, OEE and fault reports: its state (running, idle, down, no PLC link)
/// whenever it changes, the parts counted, and every finished cycle. Fed with each status from the polling loop.
/// Kept on disk as one small JSON-lines file per day (data folder\reports\2026-11-04.jsonl), so the reports survive
/// a restart. Only state and counts are kept here: alarm occurrences are kept by the machine memory.
/// Read-only towards the PLC. Thread-safe.
/// </summary>
public sealed class ProductionLog
{
    public static readonly TimeSpan AliveEvery = TimeSpan.FromMinutes(1);
    static readonly JsonSerializerOptions Json = new() { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };
    static readonly HashSet<string> NotFaults = new(StringComparer.OrdinalIgnoreCase) { "WARNING", "WARN", "INFO", "MESSAGE", "NOTE" };

    private readonly object _lock = new();
    private readonly MachineModel _model;
    private readonly string? _dir;
    private readonly Func<ReportSettings> _settings;
    private readonly List<ProdRecord>? _mem;     // no folder: memory only (tests)

    private string? _state, _why;
    private DateTimeOffset _lastWrite = DateTimeOffset.MinValue, _lastFlush = DateTimeOffset.MinValue;
    private bool _started;
    private double? _lastGood, _lastBad;
    private int _pendGood, _pendBad;
    private int? _lastStep;
    private DateTimeOffset? _leftHome;
    private string? _cycleSeq;

    /// <param name="dir">Folder for the day files; null keeps everything in memory.</param>
    public ProductionLog(MachineModel model, string? dir, Func<ReportSettings> settings)
    {
        _model = model;
        _dir = dir;
        _settings = settings;
        if (dir == null) _mem = new();
        else Directory.CreateDirectory(dir);
    }

    public string? Folder => _dir;

    /// <summary>The state the machine is in by this status, and why (null for running).</summary>
    public static (string State, string? Why, string? Alarm) Classify(MachineStatus st, MachineModel model)
    {
        if (st.Connection.State is not ("connected" or "snapshot") || st.Raw == null || st.Raw.Count == 0 || st.Raw.Values.All(v => v == null))
            return ("nolink", st.Connection.State switch
            {
                "notset" => "No PLC address set.",
                "connecting" => "Connecting to the PLC.",
                _ => "No connection to the PLC" + (st.Connection.Error is { Length: > 0 } e ? ": " + e : "."),
            }, null);

        var fault = st.ActiveAlarms.Where(a => !NotFaults.Contains(a.Severity))
            .OrderBy(a => a.Since ?? DateTimeOffset.MaxValue).FirstOrDefault();
        if (fault != null) return ("down", $"{fault.Id} {fault.Text}".Trim(), fault.Id);

        var halted = st.Sequences.FirstOrDefault(s => s.Halted);
        if (halted != null)
            return ("idle", $"{halted.Name} halted" + (halted.HaltReasons.Count > 0 ? ": " + string.Join(", ", halted.HaltReasons) : "") + ".", null);

        if (st.Sequences.Count > 0)
        {
            var busy = st.Sequences.FirstOrDefault(s => s.Step != null && s.Step != HomeStep(model, s.Id));
            if (busy != null) return ("running", null, null);
            var wait = st.Sequences.FirstOrDefault(s => s.Step != null);
            return ("idle", wait != null ? $"{wait.Name} waiting in step {wait.Step} \"{wait.StepName}\"." : "No sequence step read.", null);
        }
        // No sequences in the model: a device switched on means it is working
        var on = st.Equipment.FirstOrDefault(e => e.Commanded == true);
        return on != null ? ("running", null, null) : ("idle", "No device is switched on.", null);
    }

    /// <summary>The step a sequence rests in between cycles: the first step of the model (GRAPH's initial step).</summary>
    public static int? HomeStep(MachineModel model, string seqId) =>
        model.Sequences.FirstOrDefault(s => s.Id == seqId)?.Steps.FirstOrDefault()?.No;

    public void Add(MachineStatus st)
    {
        var now = st.Time;
        var cfg = _settings();
        lock (_lock)
        {
            var batch = new List<ProdRecord>();
            if (!_started)
            {
                _started = true;
                batch.Add(new ProdRecord(now, "start"));
            }

            var (state, why, alarm) = Classify(st, _model);
            bool changed = state != _state || (state != "running" && why != _why);

            if (state != "nolink" && st.Raw != null)
            {
                Count(cfg.GoodCountTag, st.Raw, ref _lastGood, ref _pendGood);
                Count(cfg.BadCountTag, st.Raw, ref _lastBad, ref _pendBad);
                if (Cycle(cfg, st, now) is { } cyc) batch.Add(cyc);
            }

            // Counts go out once a minute, at a state change and with a finished cycle, so a fast counter stays small on disk
            bool hasCounter = !string.IsNullOrEmpty(cfg.GoodCountTag) || !string.IsNullOrEmpty(cfg.BadCountTag);
            if ((_pendGood > 0 || _pendBad > 0) && (changed || batch.Any(b => b.K == "cycle") || now - _lastFlush >= AliveEvery))
            {
                batch.Insert(0, new ProdRecord(now, "count",
                    Good: string.IsNullOrEmpty(cfg.GoodCountTag) ? null : _pendGood,
                    Bad: string.IsNullOrEmpty(cfg.BadCountTag) ? null : _pendBad));
                _pendGood = _pendBad = 0;
                _lastFlush = now;
            }
            else if (!hasCounter) _pendGood = _pendBad = 0;

            if (changed)
            {
                batch.Add(new ProdRecord(now, "state", state, why, alarm));
                _state = state;
                _why = why;
            }
            if (batch.Count == 0 && now - _lastWrite >= AliveEvery) batch.Add(new ProdRecord(now, "alive"));
            if (batch.Count > 0)
            {
                Write(batch);
                _lastWrite = now;
            }
        }
    }

    /// <summary>A counter tag went up: add the difference. A counter that drops (reset to 0) starts again from its new value.</summary>
    private static void Count(string? tag, IReadOnlyDictionary<string, object?> raw, ref double? last, ref int pending)
    {
        if (string.IsNullOrEmpty(tag)) { last = null; return; }
        if (raw.GetValueOrDefault(tag) is not double v) return;
        if (last is { } l)
        {
            var d = v >= l ? v - l : v;
            // A jump of more than 10000 parts in one read is a counter set by hand, not production
            if (d > 0 && d <= 10000) pending += (int)Math.Round(d);
        }
        last = v;
    }

    /// <summary>The cycle sequence left its home step and came back: one finished cycle.</summary>
    private ProdRecord? Cycle(ReportSettings cfg, MachineStatus st, DateTimeOffset now)
    {
        var seqId = string.IsNullOrEmpty(cfg.CycleSequence) ? _model.Sequences.FirstOrDefault()?.Id : cfg.CycleSequence;
        if (seqId != _cycleSeq) { _cycleSeq = seqId; _lastStep = null; _leftHome = null; }
        var seq = seqId == null ? null : _model.Sequences.FirstOrDefault(s => s.Id == seqId);
        if (seq == null || st.Raw!.GetValueOrDefault(seq.StepTag) is not double d) return null;
        int step = (int)d;
        var home = HomeStep(_model, seq.Id);
        ProdRecord? done = null;
        if (_lastStep is { } prev && prev != step)
        {
            if (prev == home) _leftHome = now;
            else if (step == home && _leftHome is { } left)
            {
                done = new ProdRecord(now, "cycle", Seq: seq.Id, Sec: Math.Round((now - left).TotalSeconds, 1));
                _leftHome = null;
            }
        }
        _lastStep = step;
        return done;
    }

    private void Write(List<ProdRecord> batch)
    {
        if (_mem != null) { _mem.AddRange(batch); return; }
        try
        {
            foreach (var g in batch.GroupBy(r => r.T.ToLocalTime().Date))
                File.AppendAllLines(FileFor(g.Key), g.Select(r => JsonSerializer.Serialize(r, Json)));
        }
        catch (IOException) { /* a full disk or a locked file must not stop the live pages; the gap shows in the report */ }
        catch (UnauthorizedAccessException) { }
    }

    private string FileFor(DateTime day) => Path.Combine(_dir!, day.ToString("yyyy-MM-dd") + ".jsonl");

    /// <summary>The records from the day before <paramref name="from"/> (for the state at the start) up to <paramref name="to"/>, oldest first.</summary>
    public List<ProdRecord> Records(DateTimeOffset from, DateTimeOffset to)
    {
        lock (_lock)
        {
            if (_mem != null) return _mem.Where(r => r.T <= to && r.T >= from.AddDays(-1)).ToList();
            var list = new List<ProdRecord>();
            for (var day = from.ToLocalTime().Date.AddDays(-1); day <= to.ToLocalTime().Date; day = day.AddDays(1))
            {
                var f = FileFor(day);
                if (!File.Exists(f)) continue;
                foreach (var line in File.ReadLines(f))
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    try { if (JsonSerializer.Deserialize<ProdRecord>(line, Json) is { } r && r.T <= to) list.Add(r); }
                    catch (JsonException) { /* a line cut off by a power loss */ }
                }
            }
            // Counts not written yet belong to now
            if (_pendGood > 0 || _pendBad > 0)
            {
                var cfg = _settings();
                list.Add(new ProdRecord(DateTimeOffset.Now, "count",
                    Good: string.IsNullOrEmpty(cfg.GoodCountTag) ? null : _pendGood, Bad: string.IsNullOrEmpty(cfg.BadCountTag) ? null : _pendBad));
            }
            return list.OrderBy(r => r.T).ToList();
        }
    }

    /// <summary>For tests: feed records directly (memory mode only).</summary>
    public void AddRaw(IEnumerable<ProdRecord> records)
    {
        lock (_lock) _mem!.AddRange(records);
    }
}
