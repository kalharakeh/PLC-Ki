using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using PlcKi.Core.Knowledge;
using PlcKi.Core.Memory;
using PlcKi.Core.Model;
using PlcKi.Core.Status;

namespace PlcKi.Core.Ask;

/// <summary>A tool the assistant can call: name, what it does, and its input as JSON schema properties.</summary>
public sealed record ToolSpec(string Name, string Description, Dictionary<string, object> Properties, string[] Required);

/// <summary>What the tools see: the latest status, the latest values and the recent history.</summary>
public sealed record LiveView(MachineStatus? Status, Snapshot? Snapshot, MachineHistory History, DateTimeOffset Now);

/// <summary>
/// The tools the assistant uses to look at the machine. All of them only read: the machine model and the
/// values PLC-Ki has already read. Each returns JSON text. No API access here, so they are tested directly.
/// </summary>
public sealed class MachineTools
{
    private readonly MachineModel _model;
    private readonly Dictionary<string, TagDef> _tags;
    private readonly Func<LiveView> _live;
    private readonly AlarmMemory? _memory;
    private readonly DocumentLibrary? _docs;
    private readonly Func<object>? _devices;

    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        // Keeps quotes and < > readable ("Station".M201, Level > 20) instead of \u0022 escapes; this is not HTML
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <param name="devices">Drive and PROFINET diagnostics (the server's view), or null where there is none.</param>
    public MachineTools(MachineModel model, Func<LiveView> live, AlarmMemory? memory = null, Func<object>? devices = null, DocumentLibrary? docs = null)
    {
        _memory = memory;
        _devices = devices;
        _model = model;
        _docs = docs;
        _tags = model.Tags.ToDictionary(t => t.Id, StringComparer.Ordinal);
        _live = live;
    }

    private static Dictionary<string, object> Props(params (string Name, object Schema)[] p) =>
        p.ToDictionary(x => x.Name, x => x.Schema);

    public static readonly IReadOnlyList<ToolSpec> Specs = new List<ToolSpec>
    {
        new("get_live_status",
            "The full live picture of the machine right now: PLC connection, plain-English summary, every sequence " +
            "(current step, time in step, step limit, halted and why, each condition to move on with met/not met and the " +
            "live values), every device (state, requested by the program, output on, which permits/interlocks block it), " +
            "active alarms with causes and fixes, and data exchanged with other systems. Call this before answering " +
            "anything about the current state; values change every second.",
            Props(), Array.Empty<string>()),

        new("read_tags",
            "Current values of PLC tags, with their TIA symbol, meaning and unit. Give tag ids, or a search text that is " +
            "matched against ids, symbols and descriptions (e.g. \"temperature\", \"XV101\", \"DB_Station\").",
            Props(
                ("ids", new { type = "array", items = new { type = "string" }, description = "Tag ids from the model, e.g. [\"TT301\", \"Level\"]" }),
                ("search", new { type = "string", description = "Text to find in tag ids, symbols or descriptions" })),
            Array.Empty<string>()),

        new("get_tag_history",
            "How tag values changed over the last minutes (PLC-Ki keeps a short history in memory since it started). " +
            "For each tag: the change points (time and value from then on) and, for numbers, min/max/average. " +
            "Use it for questions like \"did the temperature drop before the alarm?\" or \"how long did filling take?\".",
            Props(
                ("ids", new { type = "array", items = new { type = "string" }, description = "Tag ids, up to 10" }),
                ("minutes", new { type = "number", description = "How far back, default 10" })),
            new[] { "ids" }),

        new("get_recent_events",
            "What happened recently, oldest first: step changes (with time spent in the step before), alarms coming and " +
            "going, halts, devices blocked by an interlock, outputs switched on/off, PLC connection changes. " +
            "Only covers the time since PLC-Ki started.",
            Props(("minutes", new { type = "number", description = "How far back, default 30" })),
            Array.Empty<string>()),

        new("get_sequence",
            "How a sequence is programmed: every step with what it does, its time limit and the conditions to move to the " +
            "next step, plus the reasons it can halt, together with where it is now. Use it to explain the process or " +
            "what comes next.",
            Props(("id", new { type = "string", description = "Sequence id; leave out for the first (or only) sequence" })),
            Array.Empty<string>()),

        new("get_equipment",
            "One device (valve, motor, heater, pump...): how the program controls it (when it is requested, the output, " +
            "every permit/interlock as written in the program, the states it can be in) and its live state.",
            Props(("id", new { type = "string", description = "Device id or name, e.g. \"M201\" or \"agitator\"" })),
            new[] { "id" }),

        new("get_alarm",
            "One alarm: its text, severity, where it is raised in the program, the condition that raises it, likely " +
            "causes and how to fix it, and whether it is active now.",
            Props(("id", new { type = "string", description = "Alarm id (e.g. \"A07\") or words from its text" })),
            new[] { "id" }),

        new("get_alarm_history",
            "The machine memory: every time an alarm was on (kept on disk for months, across restarts), with when it came " +
            "and went, how long it lasted, the step and failed checks at the time, and the cause and fix the technicians " +
            "noted. For one alarm: counts (7 and 30 days), average duration, downtime, the noted causes ranked, the step it " +
            "usually comes in, and the latest occurrences. Without an id: the alarms that happened most in the window. " +
            "Use it for any active alarm (\"did this happen before, what fixed it?\") and for \"what goes wrong most?\".",
            Props(
                ("id", new { type = "string", description = "Alarm id (e.g. \"A07\"); leave out for the most frequent alarms" }),
                ("days", new { type = "number", description = "How far back, default 30" })),
            Array.Empty<string>()),

        new("search_program",
            "Search everything in the machine model (tags, steps, conditions, devices, interlocks, alarms, data exchange) " +
            "for a word or tag. Use it to find where something is used, e.g. which steps or interlocks depend on a sensor.",
            Props(("query", new { type = "string", description = "Word, tag id or symbol to look for" })),
            new[] { "query" }),

        new("get_device_diagnostics",
            "Live hardware diagnostics read from the PLC itself (not the machine's own alarms): the CPU's system " +
            "diagnostics (PROFINET device or station failure, module and channel faults such as wire break or short " +
            "circuit, maintenance demands, drive faults reported over PROFINET) tied to the station, module and slot " +
            "where possible, and every drive with its state (running, ready, fault, switching on inhibited), fault and " +
            "warning numbers (F07900, A07910), status word bits and speed. Use it for questions about a device that is " +
            "missing or red, a drive that does not run, or what the CPU's ERROR LED is about.",
            Props(), Array.Empty<string>()),

        new("lookup_siemens_diagnostic",
            "General Siemens knowledge (not from this machine's program) about S7-1500/S7-1200 system and diagnostic " +
            "messages: CPU diagnostic buffer events and STOP reasons, error OBs (OB80, OB82, OB83, OB86, OB121, OB122), " +
            "CPU LEDs, PROFINET device failures, module/channel diagnostics (wire break, short circuit, overflow, missing " +
            "supply), fail-safe (F-I/O passivation, PROFIsafe), SINAMICS drive faults (F07900, F30001...) and communication " +
            "errors. Returns the meaning, likely causes, what to check and how to fix it. Use it when a message, code or " +
            "symptom is not one of the machine's own alarms.",
            Props(
                ("query", new { type = "string", description = "Message text, fault code, OB number or symptom, e.g. \"IO device failure\", \"F07900\", \"wire break\", \"ERROR LED flashing\"" }),
                ("category", new { type = "string", description = "Optional filter: CPU, PROFINET, Module, Safety, Drive or Communication" })),
            new[] { "query" }),

        new("lookup_device_fault",
            "Meaning, cause and fix of a fault or error code shown by a device next to the PLC: drives (SINAMICS F07801), " +
            "robots (FANUC SRVO-062, KUKA, ABB), IO-Link sensors (0x8C10), valve terminals, leak detectors, barcode readers... " +
            "Looks in the manuals and fault lists added to PLC-Ki for this machine (with document and page) and in the " +
            "built-in general knowledge. Give the code as shown; add the device if known.",
            Props(
                ("code", new { type = "string", description = "The code or message as shown on the device, e.g. \"F07801\", \"SRVO-062 BZAL\", \"Error 41\"" }),
                ("device", new { type = "string", description = "Optional: device or maker, e.g. \"G120\", \"Fanuc\", \"Inficon\"" })),
            new[] { "code" }),

        new("search_documents",
            "Full-text search in the documents added to PLC-Ki: device manuals, electrical drawings (EPLAN PDF), fault lists, " +
            "operating instructions. Returns the best pages with a text snippet, the document name and page number. Use it " +
            "for how a device works or is set up, wiring and terminal questions (\"%I12.3\", \"X2:14\", \"-B23\"), maintenance " +
            "steps, and when lookup_device_fault finds nothing.",
            Props(
                ("query", new { type = "string", description = "Words, codes, tags or terminal names to find" }),
                ("kind", new { type = "string", description = "Optional: manual, drawing, faults or other" })),
            new[] { "query" }),
    };

    /// <summary>Runs a tool. Never throws: problems come back as {"error": ...} so the assistant can react.</summary>
    public string Run(string name, JsonElement input)
    {
        try
        {
            object result = name switch
            {
                "get_live_status" => LiveStatus(),
                "read_tags" => ReadTags(Strings(input, "ids"), Str(input, "search")),
                "get_tag_history" => TagHistory(Strings(input, "ids"), Num(input, "minutes") ?? 10),
                "get_recent_events" => RecentEvents(Num(input, "minutes") ?? 30),
                "get_sequence" => Sequence(Str(input, "id")),
                "get_equipment" => Equipment(Str(input, "id") ?? ""),
                "get_alarm" => Alarm(Str(input, "id") ?? ""),
                "get_alarm_history" => AlarmHistory(Str(input, "id"), Num(input, "days") ?? 30),
                "search_program" => Search(Str(input, "query") ?? ""),
                "get_device_diagnostics" => _devices != null ? _devices() : new { note = "No device diagnostics here: PLC-Ki reads them only from a live PLC connection." },
                "lookup_siemens_diagnostic" => SiemensLookup(Str(input, "query") ?? "", Str(input, "category")),
                "lookup_device_fault" => DeviceFault(Str(input, "code") ?? "", Str(input, "device")),
                "search_documents" => SearchDocuments(Str(input, "query") ?? "", Str(input, "kind")),
                _ => new { error = $"Unknown tool {name}." },
            };
            return JsonSerializer.Serialize(result, Json);
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { error = ex.Message }, Json);
        }
    }

    /// <summary>
    /// The fixed part of what the assistant knows about this machine: names and ids of everything in the model,
    /// so it can pick the right tool call. Same text on every request, so it is cached.
    /// </summary>
    public string ModelIndex(int maxItems = 300)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Machine: {_model.Name}");
        if (!string.IsNullOrWhiteSpace(_model.Description)) sb.AppendLine(_model.Description);
        if (!string.IsNullOrWhiteSpace(_model.Source)) sb.AppendLine($"Model built from: {_model.Source}");
        sb.AppendLine();
        foreach (var s in _model.Sequences)
        {
            sb.AppendLine($"Sequence {s.Id}: {s.Name}" + (s.Block != null ? $" (block {s.Block})" : ""));
            foreach (var st in s.Steps) sb.AppendLine($"  step {st.No}: {st.Name}");
        }
        sb.AppendLine();
        sb.AppendLine("Devices:");
        foreach (var e in _model.Equipment.Take(maxItems))
            sb.AppendLine($"  {e.Id}: {e.Name}" + (e.Kind != null ? $" [{e.Kind}]" : "") + (e.Location != null ? $" - {e.Location}" : ""));
        sb.AppendLine();
        sb.AppendLine("Alarms:");
        foreach (var a in _model.Alarms.Take(maxItems))
            sb.AppendLine($"  {a.Id} ({a.Severity}): {a.Text}");
        if (_model.Alarms.Count > maxItems) sb.AppendLine($"  ... {_model.Alarms.Count - maxItems} more, use search_program.");
        if (_model.Exchange.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Data exchange partners: " + string.Join(", ", _model.Exchange.Select(x => x.Partner).Distinct()));
        }
        sb.AppendLine();
        sb.AppendLine($"{_model.Tags.Count} tags. Tag ids: " + string.Join(", ", _model.Tags.Take(maxItems * 2).Select(t => t.Id)) +
                      (_model.Tags.Count > maxItems * 2 ? ", ... (use read_tags with a search)" : ""));
        return sb.ToString();
    }

    // ---- tools ----

    private static object SiemensLookup(string query, string? category)
    {
        if (string.IsNullOrWhiteSpace(query)) return new { error = "Give a message text, code or symptom." };
        var kb = SiemensDiagnostics.Default;
        var found = kb.Search(query, category);
        return new
        {
            source = $"{kb.File.Name} (general Siemens knowledge, version {kb.File.Version}), not this machine's program. " +
                     "Confirm against the CPU diagnostic buffer or the device's own diagnostics.",
            matches = found.Select(m => m.Entry),
            note = found.Count == 0
                ? "Nothing in the Siemens knowledge base matches. Ask for the exact diagnostic buffer text or code, " +
                  "or check the TIA Portal help for that message."
                : null,
        };
    }

    private object DeviceFault(string code, string? device)
    {
        if (string.IsNullOrWhiteSpace(code)) return new { error = "Give the fault code or message as shown on the device." };
        var hits = FaultLookup.Find(_docs, code, device);
        return new
        {
            matches = hits.Select(FaultLookup.ToJson),
            note = hits.Count == 0
                ? (_docs == null || _docs.List().Count == 0
                    ? "No match. No manuals are added to PLC-Ki yet: say the device's manual (fault list) should be added on the Documents page, and what to look for in it."
                    : "No match in the manuals or the built-in knowledge. Try search_documents with the code or the device name, or ask for the exact text on the device.")
                : "Say where the answer comes from: the document and page, or 'general knowledge'. Confirm on the device's display.",
        };
    }

    private object SearchDocuments(string query, string? kind)
    {
        if (string.IsNullOrWhiteSpace(query)) return new { error = "Give words or codes to find." };
        if (_docs == null || _docs.List().Count == 0)
            return new { matches = Array.Empty<object>(), note = "No documents are added to PLC-Ki yet (Documents page)." };
        var hits = _docs.Search(query, 6, DocumentLibrary.Kinds.Contains(kind) ? kind : null);
        return new
        {
            matches = hits.Select(h => new { document = h.Document.File, device = h.Document.Device, kind = h.Document.Kind, page = h.Page, text = h.Snippet }),
            documents = hits.Count == 0 ? _docs.List().Select(d => d.File + (d.Device != null ? $" ({d.Device})" : "") + (d.State != "ready" ? $" [{d.State}]" : "")).Take(30) : null,
            note = hits.Count == 0 ? "Nothing found. The list shows the documents there are." : "Name the document and page in the answer.",
        };
    }

    private object LiveStatus()
    {
        var v = _live();
        if (v.Status == null) return new { error = "No status yet: PLC-Ki is still starting." };
        return v.Status;
    }

    private object ReadTags(List<string> ids, string? search)
    {
        var v = _live();
        var picked = new List<TagDef>();
        var unknown = new List<string>();
        foreach (var id in ids)
        {
            var t = FindTag(id);
            if (t != null) picked.Add(t); else unknown.Add(id);
        }
        if (!string.IsNullOrWhiteSpace(search))
            picked.AddRange(_model.Tags.Where(t => Matches(search, t.Id, t.Symbol, t.Label)));
        picked = picked.Distinct().ToList();
        if (picked.Count == 0 && ids.Count == 0 && string.IsNullOrWhiteSpace(search))
            return new { error = "Give ids or a search text." };

        const int max = 60;
        return new
        {
            time = v.Snapshot?.Time,
            connection = v.Status?.Connection.State,
            tags = picked.Take(max).Select(t => TagView(t, v.Snapshot)),
            more = picked.Count > max ? $"{picked.Count - max} more matched; narrow the search." : null,
            unknown = unknown.Count > 0 ? unknown : null,
        };
    }

    private object TagHistory(List<string> ids, double minutes)
    {
        var v = _live();
        var window = TimeSpan.FromMinutes(Math.Clamp(minutes, 0.1, v.History.KeepValues.TotalMinutes));
        var result = new List<object>();
        var unknown = new List<string>();
        foreach (var id in ids.Take(10))
        {
            var t = FindTag(id);
            if (t == null) { unknown.Add(id); continue; }
            var stats = v.History.Stats(t, window, v.Now);
            result.Add(new
            {
                id = t.Id,
                label = t.Label,
                changes = v.History.Series(t, window, v.Now).Select(p => new { time = Clock(p.Time), value = p.Text }),
                min = stats is { } s1 ? ValueFormat.Format(t, s1.Min) : null,
                max = stats is { } s2 ? ValueFormat.Format(t, s2.Max) : null,
                average = stats is { } s3 && t.Type is not "Time" ? ValueFormat.Format(t, s3.Avg) : null,
            });
        }
        return new
        {
            now = Clock(v.Now),
            historyStarts = v.History.OldestValueTime is { } o ? Clock(o) : "no values yet",
            note = "Each change point is the value from that time on.",
            tags = result,
            unknown = unknown.Count > 0 ? unknown : null,
        };
    }

    private object RecentEvents(double minutes)
    {
        var v = _live();
        var events = v.History.Events(TimeSpan.FromMinutes(Math.Max(minutes, 0.1)), v.Now);
        const int max = 150;
        return new
        {
            now = Clock(v.Now),
            events = events.TakeLast(max).Select(e => new { time = Clock(e.Time), kind = e.Kind, text = e.Text }),
            olderLeftOut = events.Count > max ? events.Count - max : (int?)null,
        };
    }

    private object Sequence(string? id)
    {
        var v = _live();
        var s = string.IsNullOrWhiteSpace(id) ? _model.Sequences.FirstOrDefault()
            : _model.Sequences.FirstOrDefault(x => Same(x.Id, id) || Same(x.Name, id))
              ?? _model.Sequences.FirstOrDefault(x => Matches(id, x.Id, x.Name));
        if (s == null) return new { error = $"No sequence '{id}'. Sequences: " + string.Join(", ", _model.Sequences.Select(x => x.Id)) };
        return new
        {
            definition = new
            {
                s.Id, s.Name, s.Block,
                stepTag = TagRef(s.StepTag),
                haltedTag = s.HaltedTag != null ? TagRef(s.HaltedTag) : null,
                haltReasons = s.HaltReasons.Select(h => new { h.Text, programCondition = h.Expr }),
                steps = s.Steps.Select(st => new
                {
                    st.No, st.Name, st.Does, st.Limit,
                    next = st.Next.Select(n => new
                    {
                        to = n.To,
                        conditions = n.Conditions.Select(c => new { c.Text, programCondition = c.Expr }),
                    }),
                }),
            },
            now = v.Status?.Sequences.FirstOrDefault(x => x.Id == s.Id),
        };
    }

    private object Equipment(string id)
    {
        var v = _live();
        var e = _model.Equipment.FirstOrDefault(x => Same(x.Id, id) || Same(x.Name, id))
                ?? _model.Equipment.FirstOrDefault(x => Matches(id, x.Id, x.Name, x.Kind));
        if (e == null) return new { error = $"No device '{id}'. Devices: " + string.Join(", ", _model.Equipment.Select(x => $"{x.Id} {x.Name}")) };
        return new
        {
            definition = new
            {
                e.Id, e.Name, e.Kind, e.Location,
                requestedWhen = e.Request,
                outputOnWhen = e.Command,
                permits = e.Permits.Select(p => new { p.Text, programCondition = p.Expr }),
                states = e.States.Select(st => new { when = st.When, st.Text, st.Level }),
                tagsInvolved = TagsIn(new[] { e.Request, e.Command }.Concat(e.Permits.Select(p => p.Expr)).Concat(e.States.Select(st => st.When)))
                    .Concat(e.Values).Distinct().Select(TagRef),
            },
            now = v.Status?.Equipment.FirstOrDefault(x => x.Id == e.Id),
        };
    }

    private object Alarm(string id)
    {
        var v = _live();
        var a = _model.Alarms.FirstOrDefault(x => Same(x.Id, id))
                ?? _model.Alarms.FirstOrDefault(x => Matches(id, x.Id, x.Text, x.Tag));
        if (a == null) return new { error = $"No alarm '{id}'. Use search_program to look for words from the alarm text." };
        var raw = v.Snapshot?.Values.GetValueOrDefault(a.Tag);
        return new
        {
            a.Id, a.Severity, a.Text, a.Location, a.Condition, a.Causes, a.Fix,
            alarmTag = TagRef(a.Tag),
            activeNow = raw is bool b ? b : (bool?)null,
            readError = v.Snapshot?.Errors.GetValueOrDefault(a.Tag),
            machineMemory = _memory?.Summary(a.Id, v.Now).Insight,
        };
    }

    private object AlarmHistory(string? id, double days)
    {
        if (_memory == null) return new { error = "The machine memory is not available here." };
        var now = _live().Now;
        days = Math.Clamp(days, 1, 3650);
        var from = now.AddDays(-days);
        static object Occ(AlarmOccurrence o) => new
        {
            o.AlarmId,
            came = o.Came.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"),
            went = o.Went?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"),
            stillOn = o.Went == null ? true : (bool?)null,
            duration = o.Duration is { } d ? AlarmMemory.Span(d) : null,
            note = o.AtStart ? "already on when PLC-Ki started watching" : o.EndUnknown ? "went while PLC-Ki was not watching" : null,
            repeats = o.Repeats > 0 ? o.Repeats : (int?)null,
            steps = o.Steps.Count > 0 ? o.Steps : null,
            failedChecks = o.Checks.Count > 0 ? o.Checks : null,
            devices = o.Devices.Count > 0 ? o.Devices : null,
            alsoActive = o.AlsoActive.Count > 0 ? o.AlsoActive : null,
            technicianNote = o.Note is { } n ? new { n.Cause, n.Fix, n.Text, by = n.By, at = n.At.ToLocalTime().ToString("yyyy-MM-dd HH:mm") } : null,
        };

        if (string.IsNullOrWhiteSpace(id))
        {
            var top = _memory.Top(from, now, 15);
            return new
            {
                window = $"last {days:0.#} days",
                recorded = _memory.Count,
                mostFrequent = top.Select(r => new { r.AlarmId, r.Text, r.Severity, r.Count, downtime = AlarmMemory.Span(r.Downtime), last = r.Last.ToLocalTime().ToString("yyyy-MM-dd HH:mm"), r.Noted, r.TopCause }),
                note = top.Count == 0 ? "No alarm was recorded in this window." : null,
            };
        }
        var a = _model.Alarms.FirstOrDefault(x => Same(x.Id, id)) ?? _model.Alarms.FirstOrDefault(x => Matches(id, x.Id, x.Text, x.Tag));
        var alarmId = a?.Id ?? id.Trim();
        var h = _memory.Summary(alarmId, now);
        return new
        {
            alarm = alarmId,
            text = a?.Text,
            insight = h.Insight,
            total = h.Total,
            last7Days = h.Last7Days,
            last30Days = h.Last30Days,
            firstSeen = h.FirstSeen?.ToLocalTime().ToString("yyyy-MM-dd HH:mm"),
            averageDuration = h.AverageDuration is { } ad ? AlarmMemory.Span(ad) : null,
            downtime30Days = AlarmMemory.Span(h.Downtime30Days),
            notedCauses = h.Causes.Select(c => new { c.Cause, c.Count, c.LastFix, last = c.Last.ToLocalTime().ToString("yyyy-MM-dd") }),
            usualStep = h.UsualStep,
            occurrences = _memory.Occurrences(from, now, alarmId, 10).Select(Occ),
        };
    }

    private object Search(string query)
    {
        if (string.IsNullOrWhiteSpace(query)) return new { error = "Empty query." };
        var hits = new List<object>();
        foreach (var t in _model.Tags.Where(t => Matches(query, t.Id, t.Symbol, t.Label)))
            hits.Add(new { kind = "tag", id = t.Id, text = $"{t.Symbol}: {t.Label}" });
        foreach (var s in _model.Sequences)
        {
            foreach (var h in s.HaltReasons.Where(h => Matches(query, h.Text, h.Expr)))
                hits.Add(new { kind = "halt reason", id = s.Id, text = $"{h.Text} [{h.Expr}]" });
            foreach (var st in s.Steps)
            {
                if (Matches(query, st.Name, st.Does, st.Limit))
                    hits.Add(new { kind = "step", id = $"{s.Id} step {st.No}", text = $"{st.Name}: {st.Does}" });
                foreach (var n in st.Next)
                foreach (var c in n.Conditions.Where(c => Matches(query, c.Text, c.Expr)))
                    hits.Add(new { kind = "step condition", id = $"{s.Id} step {st.No} -> {n.To}", text = $"{c.Text} [{c.Expr}]" });
            }
        }
        foreach (var e in _model.Equipment)
        {
            if (Matches(query, e.Id, e.Name, e.Kind, e.Location, e.Request, e.Command))
                hits.Add(new { kind = "device", id = e.Id, text = e.Name + (e.Location != null ? $" ({e.Location})" : "") });
            foreach (var p in e.Permits.Where(p => Matches(query, p.Text, p.Expr)))
                hits.Add(new { kind = "interlock", id = e.Id, text = $"{p.Text} [{p.Expr}]" });
        }
        foreach (var a in _model.Alarms.Where(a => Matches(query, new[] { a.Id, a.Text, a.Tag, a.Location, a.Condition }.Concat(a.Causes).Concat(a.Fix).ToArray())))
            hits.Add(new { kind = "alarm", id = a.Id, text = a.Text });
        foreach (var x in _model.Exchange.Where(x => Matches(query, x.Tag, x.Partner, x.Text)))
            hits.Add(new { kind = "exchange", id = x.Tag, text = $"{x.Direction} {x.Partner}: {x.Text}" });

        const int max = 60;
        return new
        {
            query,
            hits = hits.Take(max),
            more = hits.Count > max ? $"{hits.Count - max} more; use a more specific query." : null,
            note = hits.Count == 0 ? "Nothing in the machine model matches. The PLC program may still use it; check the TIA project." : null,
        };
    }

    // ---- helpers ----

    private object TagView(TagDef t, Snapshot? snap)
    {
        var raw = snap?.Values.GetValueOrDefault(t.Id);
        return new
        {
            t.Id, t.Symbol, t.Label, t.Type, t.Unit,
            value = snap == null ? null : ValueFormat.Format(t, raw),
            error = snap?.Errors.GetValueOrDefault(t.Id),
        };
    }

    private object TagRef(string id) =>
        _tags.TryGetValue(id, out var t) ? new { t.Id, t.Symbol, t.Label } : new { Id = id, Symbol = (string)"?", Label = (string?)null };

    private IEnumerable<string> TagsIn(IEnumerable<string?> exprs)
    {
        foreach (var e in exprs)
        {
            if (string.IsNullOrWhiteSpace(e)) continue;
            IEnumerable<string> ids;
            try { ids = Expressions.ExprParser.Parse(e).Identifiers(); }
            catch (Expressions.ExprException) { continue; }
            foreach (var id in ids) if (_tags.ContainsKey(id)) yield return id;
        }
    }

    private TagDef? FindTag(string idOrSymbol) =>
        _tags.GetValueOrDefault(idOrSymbol)
        ?? _model.Tags.FirstOrDefault(t => Same(t.Id, idOrSymbol) || Same(t.Symbol, idOrSymbol) || Same(t.Symbol.Replace("\"", ""), idOrSymbol.Replace("\"", "")));

    private static bool Same(string? a, string? b) => a != null && b != null && string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);

    /// <summary>True if every word of the query appears in one of the fields (case-insensitive).</summary>
    private static bool Matches(string query, params string?[] fields)
    {
        var words = query.Split(new[] { ' ', ',', ';' }, StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0) return false;
        var hay = string.Join(" | ", fields.Where(f => f != null));
        return words.All(w => hay.Contains(w.Trim('"'), StringComparison.OrdinalIgnoreCase));
    }

    private static string Clock(DateTimeOffset t) => t.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture);

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;

    private static double? Num(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.Number ? p.GetDouble() : null;

    private static List<string> Strings(JsonElement e, string name)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(name, out var p)) return new();
        if (p.ValueKind == JsonValueKind.String) return new() { p.GetString()! };
        if (p.ValueKind != JsonValueKind.Array) return new();
        return p.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).ToList();
    }
}
