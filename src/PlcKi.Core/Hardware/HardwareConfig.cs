using System.Text.Json;
using System.Text.RegularExpressions;
using PlcKi.Core.Import;

namespace PlcKi.Core.Hardware;

/// <summary>One I/O channel of a module: its address, and the PLC tag at that address if the program has one.</summary>
public sealed record HwChannel(int Number, string Io, string Type, string Address, string? Tag, string? Datatype, string? Comment, string? Table);

/// <summary>An address area of a module, as TIA shows it: "I 10...13".</summary>
public sealed record HwAddress(string Io, int Start, int Bytes)
{
    public string Text => Bytes <= 1 ? $"{Prefix} {Start}" : $"{Prefix} {Start}...{Start + Bytes - 1}";
    private string Prefix => Io == "Input" ? "I" : Io == "Output" ? "Q" : Io;
}

/// <summary>
/// A drive telegram (PROFIdrive): its number when the name says it ("Standard telegram 1, PZD-2/2" -> 1) and where
/// its words sit in the PLC's process image. InStart/OutStart are byte addresses, InWords/OutWords the word counts.
/// </summary>
public sealed record HwTelegram(string Name, int? Number, int? InStart, int InWords, int? OutStart, int OutWords);

public sealed class HwModule
{
    public int? Slot { get; init; }
    public string Name { get; init; } = "";
    public string? Title { get; init; }
    public string? OrderNumber { get; init; }
    public string? Firmware { get; init; }
    public string Family { get; init; } = "other";
    public string Kind { get; init; } = "other";
    public double WidthMm { get; init; }
    public bool Safety { get; init; }
    public bool Display { get; init; }
    public string? Comment { get; init; }
    public List<HwAddress> Addresses { get; } = new();
    public List<HwChannel> Channels { get; } = new();
    public List<string> Ips { get; } = new();
    public List<string> Ports { get; } = new();
    /// <summary>Drive telegrams in this module (a SINAMICS drive's telegram submodule), usually zero or one.</summary>
    public List<HwTelegram> Telegrams { get; } = new();
}

public sealed class HwStation
{
    public string Name { get; init; } = "";
    /// <summary>plc, io (ET 200 station) or other (drive, robot, camera ... from a GSD file).</summary>
    public string Kind { get; set; } = "other";
    public string Family { get; set; } = "other";
    public string? TypeName { get; init; }
    public string? OrderNumber { get; set; }
    public string? Comment { get; init; }
    public List<string> Ips { get; } = new();
    public string? PnName { get; set; }
    public string? IoSystem { get; set; }
    public string? ControlsIoSystem { get; set; }
    public List<HwModule> Modules { get; } = new();
}

/// <summary>
/// The hardware configuration of a project, read from hardware.json (written by TiaImporter export). The channels
/// are matched to the PLC tags by address (%I10.3, %IW256), so each input and output shows its tag name and comment.
/// </summary>
public sealed class HardwareConfig
{
    public string? Project { get; init; }
    public string? ExportedAt { get; init; }
    public List<HwStation> Stations { get; } = new();

    public static HardwareConfig Load(string json, IEnumerable<ExportPlcTag>? tags = null)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var hw = new HardwareConfig { Project = Str(root, "project"), ExportedAt = Str(root, "exportedAt") };
        var byAddress = TagsByAddress(tags);
        if (root.TryGetProperty("devices", out var devs) && devs.ValueKind == JsonValueKind.Array)
            foreach (var d in devs.EnumerateArray())
                hw.Stations.Add(Station(d, byAddress));
        // PLCs first, then I/O stations, then the rest; each group by name
        var order = new Dictionary<string, int> { ["plc"] = 0, ["io"] = 1, ["other"] = 2 };
        var sorted = hw.Stations.OrderBy(s => order.GetValueOrDefault(s.Kind, 3)).ThenBy(s => s.Name, StringComparer.OrdinalIgnoreCase).ToList();
        hw.Stations.Clear();
        hw.Stations.AddRange(sorted);
        return hw;
    }

    /// <summary>Only the CPU, from the program export's manifest, when the export has no hardware.json yet.</summary>
    public static HardwareConfig FromProgram(ProgramExport export)
    {
        var hw = new HardwareConfig { Project = export.Project };
        foreach (var plc in export.Plcs)
        {
            var spec = ModuleCatalog.Lookup(plc.Cpu);
            var st = new HwStation { Name = plc.Device ?? plc.Name, Kind = "plc", Family = spec.Family, OrderNumber = ModuleCatalog.Normalize(plc.Cpu) };
            st.Ips.AddRange(plc.IpAddresses);
            var cpu = new HwModule
            {
                Slot = spec.Family == "s7-1500" ? 1 : 1, Name = plc.Name, Title = spec.Title, OrderNumber = ModuleCatalog.Normalize(plc.Cpu),
                Family = spec.Family, Kind = "cpu", WidthMm = spec.WidthMm, Safety = spec.Safety, Display = spec.Display,
            };
            cpu.Ips.AddRange(plc.IpAddresses);
            st.Modules.Add(cpu);
            hw.Stations.Add(st);
        }
        return hw;
    }

    private static Dictionary<string, ExportPlcTag> TagsByAddress(IEnumerable<ExportPlcTag>? tags)
    {
        var map = new Dictionary<string, ExportPlcTag>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in tags ?? Enumerable.Empty<ExportPlcTag>())
            if (NormalizeAddress(t.Address) is { } a) map.TryAdd(a, t);
        return map;
    }

    /// <summary>"%I 10.3" / "%IX10.3" / "I10.3" -> "%I10.3"; "%iw256" -> "%IW256".</summary>
    public static string? NormalizeAddress(string? address)
    {
        if (string.IsNullOrWhiteSpace(address)) return null;
        var s = address.Replace(" ", "").ToUpperInvariant();
        if (!s.StartsWith('%')) s = "%" + s;
        s = Regex.Replace(s, @"^%([IQ])X(\d+\.\d)$", "%$1$2");
        s = Regex.Replace(s, @"^%([IQ])(W|D|B)?(\d+)(:P)?$", "%$1$2$3");
        return s;
    }

    private static HwStation Station(JsonElement d, Dictionary<string, ExportPlcTag> tags)
    {
        var st = new HwStation
        {
            Name = Str(d, "name") ?? "Device",
            TypeName = Str(d, "typeName"),
            Comment = Str(d, "comment"),
        };
        var items = Items(d).ToList();
        // The rack (S7-1500 rail, S7-1200 rack, ET 200 station) holds the modules; a device without one has its modules at the top
        var racks = items.Where(IsRack).ToList();
        var holders = racks.Count > 0 ? racks : new List<JsonElement> { d };
        foreach (var holder in holders)
            foreach (var it in Items(holder))
            {
                if (IsRack(it)) continue;
                var m = Module(it, tags);
                if (m != null) st.Modules.Add(m);
            }
        // Interfaces and ports anywhere in the device: addresses, PROFINET name, IO system
        void Net(JsonElement it)
        {
            if (it.TryGetProperty("nodes", out var nodes) && nodes.ValueKind == JsonValueKind.Array)
                foreach (var n in nodes.EnumerateArray())
                {
                    if (Str(n, "address") is { } ip && ip.Count(c => c == '.') == 3 && !st.Ips.Contains(ip)) st.Ips.Add(ip);
                    st.PnName ??= Str(n, "pnName");
                }
            st.IoSystem ??= Str(it, "ioSystem");
            st.ControlsIoSystem ??= Str(it, "controlsIoSystem");
            foreach (var sub in Items(it)) Net(sub);
        }
        foreach (var it in items) Net(it);

        st.Modules.Sort((a, b) => (a.Slot ?? 999).CompareTo(b.Slot ?? 999));
        var cpu = st.Modules.FirstOrDefault(m => m.Kind == "cpu");
        var head = cpu ?? st.Modules.FirstOrDefault(m => m.Kind == "im");
        st.Kind = cpu != null ? "plc" : head != null ? "io" : "other";
        st.Family = head?.Family ?? st.Modules.Select(m => m.Family).FirstOrDefault(f => f != "other") ?? "other";
        st.OrderNumber = head?.OrderNumber ?? st.Modules.Select(m => m.OrderNumber).FirstOrDefault(o => o != null);
        return st;
    }

    private static IEnumerable<JsonElement> Items(JsonElement e) =>
        e.TryGetProperty("items", out var a) && a.ValueKind == JsonValueKind.Array ? a.EnumerateArray() : Enumerable.Empty<JsonElement>();

    private static bool IsRack(JsonElement it)
    {
        var ti = Str(it, "typeIdentifier") ?? "";
        if (ti.StartsWith("System:Rack", StringComparison.OrdinalIgnoreCase)) return true;
        var spec = ModuleCatalog.Lookup(Str(it, "orderNumber") ?? OrderFromType(ti));
        if (spec.Kind == "bus" && spec.WidthMm == 0) return Items(it).Any();   // S7-1500 mounting rail with modules on it
        return Str(it, "orderNumber") == null && !ti.StartsWith("OrderNumber:", StringComparison.OrdinalIgnoreCase) && !ti.StartsWith("GSD:", StringComparison.OrdinalIgnoreCase)
               && Items(it).Any(c => (Str(c, "typeIdentifier") ?? "").StartsWith("OrderNumber:", StringComparison.OrdinalIgnoreCase) || Str(c, "orderNumber") != null);
    }

    private static string? OrderFromType(string ti) =>
        ti.StartsWith("OrderNumber:", StringComparison.OrdinalIgnoreCase) ? ti : null;

    private static HwModule? Module(JsonElement it, Dictionary<string, ExportPlcTag> tags)
    {
        var ti = Str(it, "typeIdentifier") ?? "";
        var order = ModuleCatalog.Normalize(Str(it, "orderNumber") ?? OrderFromType(ti));
        var typeName = Str(it, "typeName");
        // Items that are not modules: ports, interfaces, built-in sub-items without an order number of their own
        if (order == null && !ti.StartsWith("GSD:", StringComparison.OrdinalIgnoreCase) && typeName == null) return null;
        var spec = ModuleCatalog.Lookup(order, typeName);
        if (spec.Kind == "bus" && spec.WidthMm == 0) return null;
        var m = new HwModule
        {
            Slot = Int(it, "position"),
            Name = Str(it, "name") ?? "",
            Title = spec.Title ?? typeName,
            OrderNumber = order,
            Firmware = Str(it, "firmware") ?? ModuleCatalog.FirmwareOf(ti),
            Family = spec.Family,
            Kind = spec.Kind,
            WidthMm = spec.WidthMm,
            Safety = spec.Safety,
            Display = spec.Display,
            Comment = Str(it, "comment"),
        };

        // Addresses and channels of the module and its sub-items (an S7-1200 CPU has its on-board I/O as a sub-item)
        var areas = new List<HwAddress>();
        var channels = new List<(string Io, string Type, int Number)>();
        void Collect(JsonElement e, int depth)
        {
            if (e.TryGetProperty("addresses", out var addrs) && addrs.ValueKind == JsonValueKind.Array)
                foreach (var a in addrs.EnumerateArray())
                {
                    var io = Str(a, "io") ?? "";
                    if (io is not ("Input" or "Output")) continue;
                    if (Int(a, "start") is not { } start || start < 0) continue;
                    areas.Add(new HwAddress(io, start, Int(a, "length") ?? 0));
                }
            if (e.TryGetProperty("channels", out var chs) && chs.ValueKind == JsonValueKind.Array)
                foreach (var c in chs.EnumerateArray())
                    channels.Add((Str(c, "io") ?? "", Str(c, "type") ?? "", Int(c, "number") ?? channels.Count));
            if (depth < 4)
                foreach (var sub in Items(e))
                {
                    var subOrder = Str(sub, "orderNumber") ?? OrderFromType(Str(sub, "typeIdentifier") ?? "");
                    if (subOrder != null && ModuleCatalog.Normalize(subOrder) != order) continue;   // a module of its own
                    Collect(sub, depth + 1);
                }
            if (e.TryGetProperty("nodes", out var nodes) && nodes.ValueKind == JsonValueKind.Array)
                foreach (var n in nodes.EnumerateArray())
                    if (Str(n, "address") is { } ip && ip.Count(ch => ch == '.') == 3 && !m.Ips.Contains(ip)) m.Ips.Add(ip);
        }
        Collect(it, 0);
        FindTelegrams(it, m.Telegrams, 0);
        foreach (var sub in Items(it))
            if ((Str(sub, "name") ?? "") is { Length: > 0 } pn && Regex.IsMatch(pn, @"^(Port|PROFINET|PROFIBUS|MPI|X\d)", RegexOptions.IgnoreCase))
                m.Ports.Add(pn);

        // Channels: from the export, else from the catalog (DI 16 -> 16 digital inputs)
        if (channels.Count == 0)
        {
            var analog = ModuleCatalog.IsAnalog(spec.Kind);
            var (_, ni, no) = spec.Inputs + spec.Outputs > 0 ? ("", spec.Inputs, spec.Outputs) : ModuleCatalog.FromName(typeName ?? "");
            for (int i = 0; i < ni; i++) channels.Add(("Input", analog ? "Analog" : "Digital", i));
            for (int i = 0; i < no; i++) channels.Add(("Output", analog ? "Analog" : "Digital", i));
        }

        foreach (var io in new[] { "Input", "Output" })
        {
            var chs = channels.Where(c => c.Io.Equals(io, StringComparison.OrdinalIgnoreCase)).OrderBy(c => c.Number).ToList();
            var analog = chs.Any(c => c.Type.Equals("Analog", StringComparison.OrdinalIgnoreCase));
            var needed = analog ? chs.Count * 2 : (chs.Count + 7) / 8;
            HwAddress? first = null;
            foreach (var raw in areas.Where(x => x.Io == io))
            {
                m.Addresses.Add(raw with { Bytes = Math.Max(1, Bytes(raw.Bytes, first == null ? needed : 0)) });
                first ??= m.Addresses[^1];
            }
            if (first == null) continue;
            var p = io == "Input" ? "I" : "Q";
            foreach (var c in chs)
            {
                var isAnalog = c.Type.Equals("Analog", StringComparison.OrdinalIgnoreCase);
                var addr = isAnalog ? $"%{p}W{first.Start + 2 * c.Number}" : $"%{p}{first.Start + c.Number / 8}.{c.Number % 8}";
                tags.TryGetValue(addr, out var tag);
                m.Channels.Add(new HwChannel(c.Number, io, isAnalog ? "analog" : "digital", addr, tag?.Name, tag?.Datatype, tag?.Comment, tag?.Table));
            }
        }
        return m;
    }

    private static readonly Regex TelegramRx = new(@"telegram\s*(\d{1,3})", RegexOptions.IgnoreCase);
    private static readonly Regex PzdRx = new(@"PZD[-\s]*(\d{1,2})\s*/\s*(\d{1,2})", RegexOptions.IgnoreCase);

    /// <summary>Items named "... telegram ..." in a drive (the item itself or one below it), with their own addresses.</summary>
    private static void FindTelegrams(JsonElement e, List<HwTelegram> found, int depth)
    {
        var label = string.Join(" ", new[] { Str(e, "typeName"), Str(e, "name") }.Where(x => !string.IsNullOrWhiteSpace(x)));
        if (label.Contains("telegram", StringComparison.OrdinalIgnoreCase))
        {
            int? num = TelegramRx.Match(label) is { Success: true } tm ? int.Parse(tm.Groups[1].Value) : null;
            // "PZD-2/6": 2 words to the drive, 6 words back
            var pzd = PzdRx.Match(label);
            int outWords = pzd.Success ? int.Parse(pzd.Groups[1].Value) : 0, inWords = pzd.Success ? int.Parse(pzd.Groups[2].Value) : 0;
            if (num is { } n && PlcKi.Core.Diagnostics.Telegrams.Layout(n) is { } lay)
            {
                if (inWords == 0) inWords = lay.In.Length;
                if (outWords == 0) outWords = lay.OutWords;
            }
            int? inStart = null, outStart = null;
            if (e.TryGetProperty("addresses", out var addrs) && addrs.ValueKind == JsonValueKind.Array)
                foreach (var a in addrs.EnumerateArray())
                {
                    if (Int(a, "start") is not { } start || start < 0) continue;
                    var len = Int(a, "length") ?? 0;
                    if (Str(a, "io") == "Input") { inStart ??= start; if (inWords == 0) inWords = Bytes(len, 0) / 2; }
                    else if (Str(a, "io") == "Output") { outStart ??= start; if (outWords == 0) outWords = Bytes(len, 0) / 2; }
                }
            var name = Str(e, "typeName") ?? Str(e, "name") ?? "Telegram";
            found.Add(new HwTelegram(name, num, inStart, inWords, outStart, outWords));
            return;
        }
        if (depth < 4)
            foreach (var sub in Items(e)) FindTelegrams(sub, found, depth + 1);
    }

    /// <summary>
    /// Openness gives an address length in bits on some versions and in bytes on others: pick the reading that fits
    /// the channels (a DI 32 needs 4 bytes: 32 means bits, 4 means bytes).
    /// </summary>
    public static int Bytes(int length, int needed)
    {
        if (needed > 0) return length / 8 >= needed && length != needed ? length / 8 : length;
        return length > 64 && length % 8 == 0 ? length / 8 : length;
    }

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) ? v.ValueKind switch
        {
            JsonValueKind.String => v.GetString(),
            JsonValueKind.Number => v.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => null,
        } : null;

    private static int? Int(JsonElement e, string name)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(name, out var v)) return null;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i)) return i;
        if (v.ValueKind == JsonValueKind.String && int.TryParse(v.GetString(), out i)) return i;
        return null;
    }
}
