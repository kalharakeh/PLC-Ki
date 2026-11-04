using System.Text.RegularExpressions;
using PlcKi.Core.Hardware;
using PlcKi.Core.Import;
using PlcKi.Core.Knowledge;

namespace PlcKi.Core.Diagnostics;

/// <summary>
/// One entry of the CPU's own alarm list, as the PLC reports it. The CPU puts its system diagnostics there (PROFINET
/// device failure, module and channel faults, drive faults reported over PROFINET), next to the program's alarms.
/// Domain 1 is system diagnostics in S7CommPlus.
/// </summary>
public sealed record CpuAlarm(
    ulong Id, string Text, string? Info, IReadOnlyList<string> Extra, string? ClassName,
    int Domain, int Priority, DateTimeOffset? Since, bool? Acknowledged);

/// <summary>A value PLC-Ki reads for a drive: from a telegram word tag, a status bit tag, or a drive block.</summary>
public sealed record DriveSignal(string Role, string Symbol, string ModelType, string From);

/// <summary>A drive PLC-Ki found in the hardware (SINAMICS) and/or the program (SINA_SPEED, SINA_POS ... blocks).</summary>
public sealed class DriveDef
{
    public string Name { get; init; } = "";
    public string? Station { get; init; }
    public int? Slot { get; init; }
    public string? Title { get; init; }
    public string? OrderNumber { get; init; }
    public string? Ip { get; init; }
    public HwTelegram? Telegram { get; init; }
    /// <summary>Instance of the drive block in the program ("SpeedAxis_DB" or "Station_DB".Drive1), and the block (SINA_SPEED).</summary>
    public string? Block { get; set; }
    public string? BlockType { get; set; }
    public List<DriveSignal> Signals { get; } = new();
    /// <summary>Why some values cannot be read (no tag at the telegram address ...), for the page.</summary>
    public List<string> Notes { get; } = new();
}

/// <summary>A device problem the CPU reports, tied to the station and module where possible, with what it means.</summary>
public sealed record DeviceProblem(
    ulong Id, string Kind, string Severity, string Text, string? Info, string? Station, string? Module, int? Slot,
    List<string> Codes, string? ClassName, DateTimeOffset? Since, DiagnosticEntry? Knowledge);

/// <summary>A drive's live state in plain words.</summary>
public sealed record DriveState(
    string Name, string? Station, int? Slot, string? Title, string? OrderNumber, string? Ip, string? Telegram, string? Block,
    string State, string Text, List<string> Problems, string? Fault, string? Warning, double? SpeedPercent, double? Speed,
    int? StatusWord, List<object> Values, List<string> Notes, List<DeviceProblem> Reported, DiagnosticEntry? Knowledge);

/// <summary>
/// Drive and PROFINET diagnostics without any PLC-side work: the CPU's own system diagnostics (read as its active
/// alarms) tied to the stations and modules of the hardware configuration, and the drives' status words, fault
/// codes and drive blocks read like any other tag. All read-only.
/// </summary>
public static class DeviceDiagnostics
{
    // Drive blocks of the SINAMICS DriveLib and the outputs worth reading: (member, role, model type)
    private static readonly Dictionary<string, (string Member, string Role, string Type)[]> DriveBlocks = new(StringComparer.OrdinalIgnoreCase)
    {
        ["SINA_SPEED"] = new[] { ("AxisEnabled", "enabled", "Bool"), ("Lockout", "lockout", "Bool"), ("ActVelocity", "speed", "Real"), ("Error", "error", "Bool"), ("Status", "status", "Int"), ("DiagId", "diagId", "Int") },
        ["SINA_POS"] = new[] { ("AxisEnabled", "enabled", "Bool"), ("Lockout", "lockout", "Bool"), ("AxisWarn", "warnBit", "Bool"), ("AxisError", "error", "Bool"), ("ActVelocity", "speed", "DInt"), ("ActWarn", "warnCode", "Int"), ("ActFault", "faultCode", "Int"), ("Error", "blockError", "Bool"), ("Status", "status", "Int"), ("DiagID", "diagId", "Int") },
        ["SINA_INFEED"] = new[] { ("Ready", "enabled", "Bool"), ("Error", "error", "Bool"), ("Status", "status", "Int"), ("DiagId", "diagId", "Int") },
    };

    private static readonly Regex CodeRx = new(@"\b([FA])[\s-]?(\d{4,5})\b", RegexOptions.Compiled);
    private static readonly Regex FaultWords = new(@"fail|error|fault|missing|removed|not reachable|not available|wire break|short circuit|overflow|underflow|overload|overtemp|supply voltage|passivat|lost|defect|ausfall|fehler|störung|gestört|drahtbruch|kurzschluss",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex MaintWords = new(@"maintenance|wartung", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex DeviceWords = new(@"PROFINET|PROFIBUS|IO[- ]?device|IO[- ]?system|station|module|channel|submodule|rack|slot|port|drive|SINAMICS|Baugruppe|Kanal|Gerät",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // ------------------------------------------------------------------ drives

    /// <summary>Is this station or module a SINAMICS drive?</summary>
    public static bool IsDrive(string? orderNumber, string? title) =>
        (orderNumber?.StartsWith("6SL3", StringComparison.OrdinalIgnoreCase) ?? false)
        || (orderNumber?.StartsWith("6SE", StringComparison.OrdinalIgnoreCase) ?? false)
        || (title?.Contains("SINAMICS", StringComparison.OrdinalIgnoreCase) ?? false);

    /// <summary>
    /// The drives of the machine: SINAMICS devices in the hardware configuration with their telegram, and the
    /// drive blocks in the program. Telegram words are read through the PLC tags at their addresses (PLC-Ki reads
    /// by symbol, so a word without a tag cannot be read); drive blocks through their instance data.
    /// </summary>
    public static List<DriveDef> FindDrives(HardwareConfig? hw, ExportPlc? plc)
    {
        var drives = new List<DriveDef>();
        var tags = new Dictionary<string, ExportPlcTag>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in plc?.PlcTags ?? new List<ExportPlcTag>())
            if (HardwareConfig.NormalizeAddress(t.Address) is { } a) tags.TryAdd(a, t);

        foreach (var st in hw?.Stations ?? new List<HwStation>())
        {
            var stationDrive = IsDrive(st.OrderNumber, st.TypeName);
            var mods = st.Modules.Where(m => m.Telegrams.Count > 0 || (stationDrive && m.Kind != "cpu") || IsDrive(m.OrderNumber, m.Title)).ToList();
            if (!stationDrive && mods.Count == 0) continue;
            var telegrams = mods.SelectMany(m => m.Telegrams.Select(t => (Module: m, Telegram: t))).ToList();
            if (telegrams.Count == 0)
            {
                // A drive without a telegram in the export: still listed, so its PROFINET diagnostics have a home
                var head = mods.FirstOrDefault();
                var d = new DriveDef { Name = st.Name, Station = st.Name, Slot = head?.Slot, Title = head?.Title ?? st.TypeName, OrderNumber = st.OrderNumber, Ip = st.Ips.FirstOrDefault() };
                d.Notes.Add("No drive telegram in the hardware export, so its status word cannot be found.");
                drives.Add(d);
                continue;
            }
            foreach (var (m, tg) in telegrams)
            {
                var d = new DriveDef
                {
                    Name = telegrams.Count > 1 ? $"{st.Name} / {m.Name}" : st.Name, Station = st.Name, Slot = m.Slot,
                    Title = (IsDrive(m.OrderNumber, m.Title) ? m.Title : null) ?? st.TypeName ?? m.Title, OrderNumber = m.OrderNumber ?? st.OrderNumber,
                    Ip = m.Ips.FirstOrDefault() ?? st.Ips.FirstOrDefault(), Telegram = tg,
                };
                AddTelegramSignals(d, tg, tags);
                drives.Add(d);
            }
        }

        // Drive blocks in the program, tied to a drive by name where the names fit, else listed on their own
        foreach (var (symbol, type, iface) in DriveBlockInstances(plc))
        {
            var target = BestDrive(drives, symbol);
            if (target == null)
            {
                target = new DriveDef { Name = symbol.Replace("\"", ""), Title = type };
                drives.Add(target);
            }
            target.Block = symbol;
            target.BlockType = type;
            foreach (var (member, role, mt) in DriveBlocks[type])
                if (iface == null || iface.Contains(member))
                    target.Signals.Add(new DriveSignal(role, $"{symbol}.{member}", mt, type));
        }
        return drives;
    }

    private static void AddTelegramSignals(DriveDef d, HwTelegram tg, Dictionary<string, ExportPlcTag> tags)
    {
        if (tg.InStart is not { } start) { d.Notes.Add("The telegram has no input address in the export."); return; }
        var layout = tg.Number is { } n ? Telegrams.Layout(n) : null;
        var words = layout?.In ?? new[] { "ZSW1", "NIST_A" };    // every PROFIdrive speed telegram starts like this
        if (layout == null) d.Notes.Add($"Telegram {tg.Number?.ToString() ?? "type"} is not known to PLC-Ki: only its first two words (status word, speed) are read.");
        int found = 0;
        for (int i = 0; i < words.Length && i < Math.Max(tg.InWords, 2); i++)
        {
            var w = words[i];
            if (w.EndsWith('+') || w == "reserved") continue;
            var addr = $"%IW{start + 2 * i}";
            if (!tags.TryGetValue(addr, out var tag)) continue;
            var mt = ProgramExport.ModelType(tag.Datatype);
            if (mt is null or "Bool") continue;
            d.Signals.Add(new DriveSignal(w, $"\"{tag.Name}\"", mt, $"{addr} ({tag.Name})"));
            found++;
        }
        // Single bits of status word 1 that have their own tag (ZSW1 is big-endian: bits 0-7 are in the second byte)
        foreach (var bit in Telegrams.Zsw1)
        {
            var addr = bit.Bit < 8 ? $"%I{start + 1}.{bit.Bit}" : $"%I{start}.{bit.Bit - 8}";
            if (tags.TryGetValue(addr, out var tag) && ProgramExport.ModelType(tag.Datatype) == "Bool")
            {
                d.Signals.Add(new DriveSignal($"zsw1.{bit.Bit}", $"\"{tag.Name}\"", "Bool", $"{addr} ({tag.Name})"));
                found++;
            }
        }
        if (found == 0)
            d.Notes.Add($"No PLC tag sits on the telegram's input words (%IW{start}...). PLC-Ki reads by tag name, so the drive's status word " +
                        "can only be read when a tag points at it (or through a SINA_SPEED/SINA_POS block).");
    }

    /// <summary>Instances of the DriveLib blocks: global instance DBs and multi-instances inside other DBs.</summary>
    private static IEnumerable<(string Symbol, string Type, HashSet<string>? Interface)> DriveBlockInstances(ExportPlc? plc)
    {
        if (plc == null) yield break;
        // The FB's own interface, when the export has it (library blocks often are not exported)
        HashSet<string>? Iface(string type)
        {
            var fb = plc.Blocks.FirstOrDefault(b => b.Kind == "FB" && b.Name.Equals(type, StringComparison.OrdinalIgnoreCase));
            return fb == null || fb.Interface.Count == 0 ? null
                : fb.Interface.Where(m => m.Section is "Output" or "InOut" or "Static").Select(m => m.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        }
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var b in plc.Blocks)
        {
            if (b.Kind == "InstanceDB" && b.InstanceOf is { } fb && DriveBlocks.ContainsKey(fb))
            {
                var key = DriveBlocks.Keys.First(k => k.Equals(fb, StringComparison.OrdinalIgnoreCase));
                if (seen.Add(b.Name)) yield return ($"\"{b.Name}\"", key, Iface(key));
                continue;
            }
            if (b.Kind is not ("InstanceDB" or "GlobalDB")) continue;
            foreach (var (path, type) in Multi(b.Interface, "", 0))
            {
                var sym = $"\"{b.Name}\".{path}";
                if (seen.Add(sym)) yield return (sym, type, Iface(type));
            }
        }
    }

    private static IEnumerable<(string Path, string Type)> Multi(List<ExportMember> members, string prefix, int depth)
    {
        if (depth > 4) yield break;
        foreach (var m in members)
        {
            if (m.Section is "Temp" or "Constant") continue;
            var p = prefix.Length == 0 ? m.Name : prefix + "." + m.Name;
            if (m.UserType is { } ut && DriveBlocks.Keys.FirstOrDefault(k => k.Equals(ut, StringComparison.OrdinalIgnoreCase)) is { } key)
            { yield return (p, key); continue; }
            foreach (var x in Multi(m.Members, p, depth + 1)) yield return x;
        }
    }

    /// <summary>The hardware drive whose name fits a block instance name best ("Conveyor_Drive" ~ "G120_Conveyor"), or null.</summary>
    private static DriveDef? BestDrive(List<DriveDef> drives, string symbol)
    {
        var candidates = drives.Where(d => d.Block == null && d.Station != null).ToList();
        if (candidates.Count == 0) return null;
        var words = Words(symbol);
        var scored = candidates.Select(d => (d, Score: Words(d.Name).Intersect(words).Count())).OrderByDescending(x => x.Score).ToList();
        if (scored[0].Score > 0 && (scored.Count == 1 || scored[1].Score < scored[0].Score)) return scored[0].d;
        // One drive and one block: they belong together
        return candidates.Count == 1 && drives.Count(x => x.Station != null) == 1 ? candidates[0] : null;
    }

    private static readonly HashSet<string> Generic = new(StringComparer.OrdinalIgnoreCase)
    { "db", "idb", "inst", "instance", "drive", "drives", "axis", "sina", "speed", "pos", "fb", "g120", "s120", "v90", "sinamics", "data" };

    private static HashSet<string> Words(string s) =>
        Regex.Split(s, @"[^A-Za-z0-9]+|(?<=[a-z])(?=[A-Z])").Where(w => w.Length >= 2 && !Generic.Contains(w)).Select(w => w.ToLowerInvariant()).ToHashSet();

    /// <summary>The live state of one drive from the values read for it, plus what the CPU reports about its station.</summary>
    public static DriveState State(DriveDef d, IReadOnlyDictionary<string, (object? Value, string? Error)> values, IEnumerable<DeviceProblem> reported)
    {
        double? Num(string role)
        {
            var s = d.Signals.FirstOrDefault(x => x.Role == role);
            if (s == null || !values.TryGetValue(s.Symbol, out var v) || v.Error != null || v.Value == null) return null;
            return v.Value switch { bool b => b ? 1 : 0, IConvertible c => Convert.ToDouble(c, System.Globalization.CultureInfo.InvariantCulture), _ => null };
        }
        bool? Bit(string role) => Num(role) is { } x ? x != 0 : null;

        var mine = reported.ToList();
        var shown = new List<object>();
        foreach (var s in d.Signals)
        {
            values.TryGetValue(s.Symbol, out var v);
            shown.Add(new { role = s.Role, label = Label(s.Role), symbol = s.Symbol, from = s.From, value = v.Value, error = v.Error });
        }

        string state = "Unknown", text;
        var problems = new List<string>();
        int? zsw = null;
        if (Num("ZSW1") is { } z)
        {
            zsw = (int)z & 0xFFFF;
            (state, text, problems) = Telegrams.DecodeZsw1(zsw.Value);
        }
        else if (d.Signals.Any(s => s.Role.StartsWith("zsw1.")))
        {
            // Only single bits have tags: rebuild what they tell
            int word = 0, known = 0;
            foreach (var s in d.Signals.Where(s => s.Role.StartsWith("zsw1.")))
                if (Bit(s.Role) is { } on) { known |= 1 << int.Parse(s.Role[5..]); if (on) word |= 1 << int.Parse(s.Role[5..]); }
            if ((known & (1 << 3)) != 0 && (word & (1 << 3)) != 0) { state = "Fault"; text = "The drive reports a fault (status word bit 3)."; }
            else if ((known & (1 << 2)) != 0 && (word & (1 << 2)) != 0) { state = "Running"; text = "The drive is running (operation enabled)."; }
            else text = "Some bits of the drive's status word are read, not enough to say its state.";
        }
        else text = d.Signals.Count == 0 ? "PLC-Ki has nothing to read for this drive." : "";

        // Drive block outputs refine or replace the status word
        if (Bit("error") == true || Bit("blockError") == true)
        {
            var diag = Num("diagId") is { } di && di != 0 ? $" (DiagId 16#{(int)di & 0xFFFF:X4})" : "";
            if (state != "Fault") { state = "Fault"; text = $"The drive block {d.BlockType} reports an error{diag}."; }
            else if (diag.Length > 0) problems.Add($"Drive block {d.BlockType} reports an error{diag}.");
        }
        else if (state == "Unknown" && Bit("enabled") is { } en)
        {
            state = en ? "Running" : Bit("lockout") == true ? "Inhibited" : "Ready";
            text = en ? "The drive is enabled (drive block)." : Bit("lockout") == true ? "Switching on is inhibited (drive block Lockout): OFF2/OFF3 or STO is active, or it needs ON off and on again." : "The drive is not enabled (drive block).";
        }
        if (Bit("lockout") == true && state != "Inhibited" && state != "Fault") problems.Add("Switching on is inhibited (Lockout).");
        if (Bit("warnBit") == true) problems.Add("The drive reports a warning.");

        var fault = Telegrams.Code(Num("FAULT_CODE") ?? Num("faultCode"), false);
        var warning = Telegrams.Code(Num("WARN_CODE") ?? Num("warnCode"), true);
        if (fault == null)
            fault = mine.SelectMany(p => p.Codes).FirstOrDefault(c => c.StartsWith('F'));
        if (warning == null)
            warning = mine.SelectMany(p => p.Codes).FirstOrDefault(c => c.StartsWith('A'));
        if (mine.Any(p => p.Severity == "fault"))
        {
            if (state is "Unknown" or "Running" or "Ready")
            {
                state = "Fault";
                text = "The PLC reports a problem with this drive: " + mine.First(p => p.Severity == "fault").Text;
            }
        }
        if (state == "Unknown" && string.IsNullOrEmpty(text)) text = "No value read yet.";
        if (fault != null && state != "Fault") { state = "Fault"; text = "The drive reports a fault."; }

        double? pct = Telegrams.SpeedPercent(Num("NIST_A") ?? Num("NIST_A_GLATT"));
        var speed = Num("speed");
        var kb = fault ?? warning;
        var knowledge = kb != null ? Knowledge(kb) : null;
        return new DriveState(d.Name, d.Station, d.Slot, d.Title, d.OrderNumber, d.Ip,
            d.Telegram == null ? null : d.Telegram.Number is { } n ? $"Telegram {n}" : d.Telegram.Name,
            d.Block, state, text, problems, fault, warning, pct, speed, zsw, shown, d.Notes, mine, knowledge);
    }

    /// <summary>The knowledge base entry for an exact code (F07900), or null.</summary>
    public static DiagnosticEntry? Knowledge(string code) =>
        SiemensDiagnostics.Default.Search(code, null, 1).FirstOrDefault(m => m.Score >= 100)?.Entry;

    private static string Label(string role) => role switch
    {
        "enabled" => "Enabled", "lockout" => "Switching on inhibited", "speed" => "Actual speed", "error" => "Error",
        "blockError" => "Block error", "status" => "Block status", "diagId" => "DiagId", "warnBit" => "Warning",
        "warnCode" => "Warning number", "faultCode" => "Fault number",
        _ when role.StartsWith("zsw1.") => "Status bit " + role[5..] + ": " + Telegrams.Zsw1[int.Parse(role[5..])].Name,
        _ => Telegrams.WordText(role),
    };

    // ------------------------------------------------------------------ CPU system diagnostics

    /// <summary>
    /// The CPU's alarms that are about devices (system diagnostics), tied to a station and module of the hardware,
    /// with severity, codes and the matching Siemens knowledge entry. Program alarms are left out: the machine
    /// model has those already.
    /// </summary>
    public static List<DeviceProblem> Problems(IEnumerable<CpuAlarm> alarms, HardwareConfig? hw)
    {
        var names = Names(hw);
        var list = new List<DeviceProblem>();
        foreach (var a in alarms)
        {
            var all = string.Join(" | ", new[] { a.Text, a.Info }.Concat(a.Extra).Where(x => !string.IsNullOrWhiteSpace(x)));
            var system = IsSystem(a, all);
            if (!system) continue;
            var (station, module, slot) = Match(names, all);
            var codes = CodeRx.Matches(all).Select(m => m.Groups[1].Value.ToUpperInvariant() + m.Groups[2].Value.PadLeft(5, '0')).Distinct().ToList();
            var severity = FaultWords.IsMatch(all) || codes.Any(c => c.StartsWith('F')) ? "fault"
                : MaintWords.IsMatch(all) ? "maintenance" : codes.Count > 0 ? "warning" : "info";
            var kind = codes.Count > 0 || (station != null && hw?.Stations.FirstOrDefault(s => s.Name == station) is { } st && IsDrive(st.OrderNumber, st.TypeName)) ? "drive"
                : Regex.IsMatch(all, @"channel|wire break|short circuit|overflow|underflow|Kanal|Drahtbruch", RegexOptions.IgnoreCase) ? "channel"
                : Regex.IsMatch(all, @"PROFINET|IO[- ]?device|station|failure|Ausfall|not reachable", RegexOptions.IgnoreCase) ? "profinet" : "module";
            // A code that the knowledge base knows wins; otherwise only a clear text match (a weak one would mislead)
            var kb = codes.Select(Knowledge).FirstOrDefault(e => e != null)
                     ?? SiemensDiagnostics.Default.Search(a.Text, null, 1).FirstOrDefault(m => m.Score >= 10)?.Entry;
            list.Add(new DeviceProblem(a.Id, kind, severity, a.Text, a.Info, station, module, slot, codes, a.ClassName, a.Since, kb));
        }
        return list.OrderBy(p => p.Severity switch { "fault" => 0, "warning" => 1, "maintenance" => 2, _ => 3 }).ThenBy(p => p.Station).ToList();
    }

    /// <summary>System diagnostics, not a program alarm: domain 1, a system class name, or device words without a program source.</summary>
    public static bool IsSystem(CpuAlarm a, string all)
    {
        if (a.Domain == 1) return true;
        if (a.ClassName is { } c && Regex.IsMatch(c, @"system|diagnos", RegexOptions.IgnoreCase)) return true;
        if (a.Domain >= 256) return false;     // user alarm classes
        return DeviceWords.IsMatch(all) && (FaultWords.IsMatch(all) || MaintWords.IsMatch(all) || CodeRx.IsMatch(all));
    }

    private sealed record NameHit(string Text, string Station, string? Module, int? Slot, int Weight);

    private static List<NameHit> Names(HardwareConfig? hw)
    {
        var list = new List<NameHit>();
        foreach (var st in hw?.Stations ?? new List<HwStation>())
        {
            list.Add(new NameHit(st.Name, st.Name, null, null, 2));
            if (!string.IsNullOrWhiteSpace(st.PnName)) list.Add(new NameHit(st.PnName, st.Name, null, null, 2));
            foreach (var ip in st.Ips) list.Add(new NameHit(ip, st.Name, null, null, 1));
            foreach (var m in st.Modules)
                if (m.Name.Length >= 4 && !m.Name.Equals(st.Name, StringComparison.OrdinalIgnoreCase))
                    list.Add(new NameHit(m.Name, st.Name, m.Name, m.Slot, 3));
        }
        // Longest first: "ET200SP_Leak" before "ET200SP"
        return list.OrderByDescending(n => n.Text.Length).ThenByDescending(n => n.Weight).ToList();
    }

    private static (string? Station, string? Module, int? Slot) Match(List<NameHit> names, string text)
    {
        string? station = null, module = null;
        int? slot = null;
        foreach (var n in names)
        {
            if (!Regex.IsMatch(text, $@"(?<![A-Za-z0-9_]){Regex.Escape(n.Text)}(?![A-Za-z0-9_])", RegexOptions.IgnoreCase)) continue;
            if (n.Module != null)
            {
                if (module == null && (station == null || station == n.Station)) { module = n.Module; slot = n.Slot; station ??= n.Station; }
            }
            else station ??= n.Station;
            if (station != null && module != null) break;
        }
        // "slot 3" in the text, when the module name was not there
        if (station != null && slot == null && Regex.Match(text, @"(?:slot|Steckplatz)\s*(\d{1,3})", RegexOptions.IgnoreCase) is { Success: true } sm)
            slot = int.Parse(sm.Groups[1].Value);
        return (station, module, slot);
    }
}
