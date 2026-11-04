using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace PlcKi.Core.Import;

/// <summary>An HMI tag: its name on the panel and the PLC tag behind it (null for internal HMI tags).</summary>
public sealed record HmiTag(string Name, string? PlcTag, string? DataType, string? Comment, string? Connection, string Table);

/// <summary>
/// Something on an HMI screen that shows or changes a PLC value: an I/O field, a switch, a button that sets a bit.
/// </summary>
public sealed class HmiField
{
    public string Screen { get; init; } = "";
    /// <summary>IOField, SymbolicIOField, Button, Switch, Slider, ...</summary>
    public string Kind { get; init; } = "";
    public string? Name { get; init; }
    /// <summary>The text an operator sees next to (or on) it: the nearest text to the left or above, or the button text.</summary>
    public string? Label { get; set; }
    /// <summary>The HMI tag it shows or writes.</summary>
    public string Tag { get; init; } = "";
    /// <summary>The PLC tag behind <see cref="Tag"/>, as a PLC symbol ("HMI".Tolerance), when the HMI tag has one.</summary>
    public string? PlcSymbol { get; set; }
    /// <summary>The operator can change the value here (input field, switch, button function).</summary>
    public bool Writes { get; init; }
    /// <summary>For a button: what it does with the tag (SetBit, ResetBit, SetBitWhileKeyPressed, SetTag ...).</summary>
    public string? Action { get; init; }
    public string? Unit { get; init; }
    public string? Format { get; init; }
    /// <summary>For a symbolic I/O field: the text list that turns the value into words.</summary>
    public string? TextList { get; init; }
    /// <summary>The user group (authorization) needed to operate it, when set.</summary>
    public string? Authorization { get; init; }
    public int Left { get; init; }
    public int Top { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }
}

public sealed class HmiScreen
{
    public string Name { get; init; } = "";
    public int? Number { get; init; }
    /// <summary>"Screen", "Template" or "Popup".</summary>
    public string Kind { get; init; } = "Screen";
    /// <summary>Folder path in the HMI's screen list, e.g. "Parameters/Leak test".</summary>
    public string? Folder { get; init; }
    /// <summary>Headline texts on the screen (the biggest texts near the top), for a readable screen description.</summary>
    public List<string> Texts { get; } = new();
    public List<HmiField> Fields { get; } = new();
}

public sealed record HmiTextList(string Name, List<(string Range, string Text)> Entries);

/// <summary>An HMI alarm (only WinCC Unified exposes them through Openness).</summary>
public sealed record HmiAlarm(string Name, string? Id, string Text, string? Tag, string? PlcSymbol, int? Bit, string? AlarmClass, string? InfoText, string Kind);

/// <summary>One HMI panel of the project, read from the folder TiaImporter "export" writes under HMI\.</summary>
public sealed class HmiDevice
{
    public string Name { get; init; } = "";
    public string? Device { get; init; }
    public string? Type { get; init; }
    /// <summary>"classic" (WinCC Comfort/Advanced) or "unified".</summary>
    public string Kind { get; init; } = "classic";
    public List<HmiTag> Tags { get; } = new();
    public List<HmiScreen> Screens { get; } = new();
    public List<HmiTextList> TextLists { get; } = new();
    public List<HmiAlarm> Alarms { get; } = new();
    public List<string> Problems { get; } = new();

    /// <summary>Every field on a screen (not templates) where the operator changes a value that reaches the PLC.</summary>
    public IEnumerable<HmiField> OperatorSettings() => Screens.Where(s => s.Kind != "Template")
        .SelectMany(s => s.Fields).Where(f => f.Writes && f.PlcSymbol != null && f.Action == null);

    /// <summary>Buttons and switches on screens (and templates) that set bits or values in the PLC.</summary>
    public IEnumerable<HmiField> OperatorCommands() => Screens.SelectMany(s => s.Fields).Where(f => f.Action != null && f.PlcSymbol != null);
}

/// <summary>
/// Reads the HMI part of a TiaImporter export: SimaticML of WinCC Comfort/Advanced tag tables, screens and text lists,
/// or unified.json for WinCC Unified. Lenient: what it does not understand is skipped.
/// </summary>
public static class HmiProject
{
    public static List<HmiDevice> Load(string exportRoot)
    {
        var list = new List<HmiDevice>();
        var root = Path.Combine(exportRoot, "HMI");
        if (!Directory.Exists(root)) return list;
        foreach (var dir in Directory.GetDirectories(root).OrderBy(d => d, StringComparer.Ordinal))
        {
            try { list.Add(LoadOne(dir)); }
            catch (Exception ex) { list.Add(new HmiDevice { Name = Path.GetFileName(dir), Problems = { ex.Message } }); }
        }
        return list;
    }

    private static HmiDevice LoadOne(string dir)
    {
        string? name = null, device = null, type = null, kind = null;
        var info = Path.Combine(dir, "hmi.json");
        if (File.Exists(info))
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(info));
            name = Str(doc.RootElement, "name");
            device = Str(doc.RootElement, "device");
            type = Str(doc.RootElement, "typeIdentifier")?.Replace("OrderNumber:", "");
            kind = Str(doc.RootElement, "kind");
        }
        var hmi = new HmiDevice { Name = name ?? Path.GetFileName(dir), Device = device, Type = type, Kind = kind ?? "classic" };
        if (hmi.Kind == "unified") ReadUnified(hmi, Path.Combine(dir, "unified.json"));
        else
        {
            foreach (var f in Files(dir, "Tags")) Try(hmi, f, () => ReadTagTable(hmi, f));
            foreach (var f in Files(dir, "TextLists")) Try(hmi, f, () => ReadTextList(hmi, f));
            foreach (var (sub, k) in new[] { ("Screens", "Screen"), ("Popups", "Popup"), ("Templates", "Template") })
                foreach (var f in Files(dir, sub))
                {
                    var folder = Path.GetDirectoryName(Path.GetRelativePath(Path.Combine(dir, sub), f))?.Replace('\\', '/');
                    Try(hmi, f, () => ReadScreen(hmi, f, k, string.IsNullOrEmpty(folder) ? null : folder));
                }
        }
        Link(hmi);
        return hmi;
    }

    private static IEnumerable<string> Files(string dir, string sub) =>
        Directory.Exists(Path.Combine(dir, sub)) ? Directory.GetFiles(Path.Combine(dir, sub), "*.xml", SearchOption.AllDirectories).OrderBy(f => f, StringComparer.Ordinal) : Array.Empty<string>();

    private static void Try(HmiDevice hmi, string file, Action read)
    {
        try { read(); }
        catch (Exception ex) { hmi.Problems.Add($"{Path.GetFileName(file)}: {ex.Message}"); }
    }

    /// <summary>Fills in the PLC symbol of every field and alarm from the HMI tags.</summary>
    private static void Link(HmiDevice hmi)
    {
        var tags = hmi.Tags.GroupBy(t => t.Name, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        foreach (var f in hmi.Screens.SelectMany(s => s.Fields))
            if (tags.TryGetValue(BaseTag(f.Tag), out var t) && t.PlcTag != null)
                f.PlcSymbol = PlcSymbol(t.PlcTag + f.Tag[BaseTag(f.Tag).Length..]);
    }

    /// <summary>"Motor[3]" -> "Motor"; "Recipe.Speed" stays (HMI UDT members are linked as a whole tag name).</summary>
    private static string BaseTag(string tag)
    {
        var i = tag.IndexOf('[');
        return i > 0 ? tag[..i] : tag;
    }

    /// <summary>The PLC symbol for a controller tag as the HMI writes it: HMI.Tolerance -> "HMI".Tolerance.</summary>
    public static string PlcSymbol(string controllerTag) => ProgramExport.NormalizeSymbol(controllerTag);

    // ------------------------------------------------------------------ classic XML

    private static XElement? Obj(string file, string type) =>
        XDocument.Load(file).Root?.Elements().FirstOrDefault(e => e.Name.LocalName == type);

    private static string? Attr(XElement e, string name) => e.Element("AttributeList")?.Element(name)?.Value is { Length: > 0 } v ? v : null;

    private static string? Link(XElement e, string name) => e.Element("LinkList")?.Element(name)?.Element("Name")?.Value;

    /// <summary>The first non-empty text of a multilingual text (English first), without the rich-text markup.</summary>
    private static string? Text(XElement e, string composition)
    {
        var mt = e.Element("ObjectList")?.Elements("MultilingualText").FirstOrDefault(m => (string?)m.Attribute("CompositionName") == composition);
        if (mt == null) return null;
        var items = mt.Descendants("MultilingualTextItem")
            .Select(i => (Culture: i.Element("AttributeList")?.Element("Culture")?.Value ?? "", Text: Clean(i.Element("AttributeList")?.Element("Text"))))
            .Where(i => !string.IsNullOrWhiteSpace(i.Text)).ToList();
        return (items.FirstOrDefault(i => i.Culture.StartsWith("en", StringComparison.OrdinalIgnoreCase)).Text ?? items.FirstOrDefault().Text);
    }

    private static string? Clean(XElement? text)
    {
        if (text == null) return null;
        // Rich text is either nested elements (<body><p>..</p></body>) or the same markup as escaped text
        var paragraphs = text.Descendants().Where(d => d.Name.LocalName == "p").Select(p => p.Value).ToList();
        var s = paragraphs.Count > 0 ? string.Join(" ", paragraphs) : text.Value;
        s = Regex.Replace(s, "<[^>]+>", " ");
        s = Regex.Replace(s, @"\s+", " ").Trim();
        return s.Length == 0 ? null : s;
    }

    private static void ReadTagTable(HmiDevice hmi, string file)
    {
        var table = Obj(file, "Hmi.Tag.TagTable");
        if (table == null) return;
        var tableName = Attr(table, "Name") ?? Path.GetFileNameWithoutExtension(file);
        foreach (var t in table.Element("ObjectList")?.Elements().Where(e => e.Name.LocalName == "Hmi.Tag.Tag") ?? Enumerable.Empty<XElement>())
        {
            var name = Attr(t, "Name");
            if (name == null) continue;
            hmi.Tags.Add(new HmiTag(name, Link(t, "ControllerTag"), Link(t, "DataType") ?? Link(t, "HmiDataType"),
                Text(t, "Comment"), Link(t, "Connection"), tableName));
        }
    }

    private static void ReadTextList(HmiDevice hmi, string file)
    {
        var list = Obj(file, "Hmi.TextGraphicList.TextList");
        if (list == null) return;
        var entries = new List<(string, string)>();
        foreach (var e in list.Element("ObjectList")?.Elements().Where(x => x.Name.LocalName == "Hmi.TextGraphicList.TextListEntry") ?? Enumerable.Empty<XElement>())
        {
            var text = Text(e, "Text");
            if (text == null) continue;
            string? from = Attr(e, "From"), to = Attr(e, "To");
            var range = Attr(e, "DefaultEntry") == "true" ? "other" : from == null ? "?" : to == null || to == from ? from : $"{from}..{to}";
            entries.Add((range, text));
        }
        hmi.TextLists.Add(new HmiTextList(Attr(list, "Name") ?? Path.GetFileNameWithoutExtension(file), entries));
    }

    private static readonly HashSet<string> WriteFunctions = new(StringComparer.OrdinalIgnoreCase)
    {
        "SetBit", "ResetBit", "InvertBit", "SetBitWhileKeyPressed", "SetTag", "IncreaseValue", "DecreaseValue",
        "IncreaseTag", "DecreaseTag", "SetBitInTag", "ResetBitInTag", "InvertBitInTag", "SetBitInTagWhileKeyPressed",
    };

    private static void ReadScreen(HmiDevice hmi, string file, string kind, string? folder)
    {
        var type = kind switch { "Popup" => "Hmi.Screen.ScreenPopup", "Template" => "Hmi.Screen.ScreenTemplate", _ => "Hmi.Screen.Screen" };
        var el = XDocument.Load(file).Root?.Elements().FirstOrDefault(e => e.Name.LocalName == type || e.Name.LocalName.StartsWith("Hmi.Screen.Screen", StringComparison.Ordinal));
        if (el == null) return;
        var screen = new HmiScreen
        {
            Name = Attr(el, "Name") ?? Path.GetFileNameWithoutExtension(file),
            Number = int.TryParse(Attr(el, "Number"), out var n) ? n : null,
            Kind = kind,
            Folder = folder,
        };
        var texts = new List<(string Text, int Left, int Top, int Width, int Height)>();
        foreach (var item in el.Descendants().Where(e => (string?)e.Attribute("CompositionName") == "ScreenItems"))
        {
            var itemKind = item.Name.LocalName.Replace("Hmi.Screen.", "");
            int.TryParse(Attr(item, "Left"), out var left);
            int.TryParse(Attr(item, "Top"), out var top);
            int.TryParse(Attr(item, "Width"), out var width);
            int.TryParse(Attr(item, "Height"), out var height);
            if (itemKind == "TextField")
            {
                if (Text(item, "Text") is { } t) texts.Add((t, left, top, width, height));
                continue;
            }
            var objName = Attr(item, "ObjectName") ?? Attr(item, "Name");
            var auth = Link(item, "Authorization");
            // Tag shown or written by the item: its process value (I/O fields, switches, sliders, bars)
            foreach (var prop in item.Element("ObjectList")?.Elements().Where(e => e.Name.LocalName == "Hmi.Screen.Property") ?? Enumerable.Empty<XElement>())
            {
                var propName = Attr(prop, "Name");
                var tag = prop.Descendants().Where(d => d.Name.LocalName == "Hmi.Dynamic.TagConnectionDynamic")
                    .Select(d => Link(d, "Tag")).FirstOrDefault(x => x != null);
                if (tag == null || propName is not ("ProcessValue" or "Tag" or "ProcessValueTag")) continue;
                var mode = Attr(item, "Mode");
                var writes = itemKind switch
                {
                    "IOField" or "SymbolicIOField" or "DateTimeField" => mode is "Input" or "InputOutput",
                    "Switch" or "Slider" => Attr(item, "Enabled") != "false",
                    _ => false,
                };
                screen.Fields.Add(new HmiField
                {
                    Screen = screen.Name, Kind = itemKind, Name = objName, Tag = tag, Writes = writes,
                    Unit = Attr(item, "Unit"), Format = Attr(item, "FormatPattern"),
                    TextList = Link(item, "TextList"), Authorization = auth,
                    Left = left, Top = top, Width = width, Height = height,
                });
            }
            // Events (button press/release/click, switch change): system functions that change PLC tags
            foreach (var entry in item.Descendants().Where(d => d.Name.LocalName == "Hmi.Event.FunctionListEntry"))
            {
                var fn = Attr(entry, "Name");
                if (fn == null || !WriteFunctions.Contains(fn)) continue;
                var tag = entry.Descendants().Where(d => d.Name.LocalName == "Hmi.Event.FunctionListEntryParameter")
                    .Select(p => p.Element("LinkList")?.Element("Value")?.Element("Name")?.Value).FirstOrDefault(x => x != null);
                if (tag == null) continue;
                var ev = entry.Ancestors().FirstOrDefault(a => a.Name.LocalName == "Hmi.Event.Event");
                var evName = ev != null ? Attr(ev, "Name") : null;
                var label = Text(item, "TextOff") ?? Text(item, "Text") ?? Text(item, "TextOn");
                screen.Fields.Add(new HmiField
                {
                    Screen = screen.Name, Kind = itemKind, Name = objName, Tag = tag, Writes = true,
                    Action = evName != null ? $"{fn} on {evName}" : fn, Label = label, Authorization = auth,
                    Left = left, Top = top, Width = width, Height = height,
                });
            }
        }
        foreach (var f in screen.Fields.Where(f => f.Label == null)) f.Label = NearestLabel(f, texts);
        screen.Texts.AddRange(texts.Where(t => t.Text.Length <= 60).OrderBy(t => t.Top).ThenBy(t => t.Left)
            .Where(t => !screen.Fields.Any(f => f.Label == t.Text)).Select(t => t.Text).Distinct().Take(8));
        hmi.Screens.Add(screen);
    }

    /// <summary>The text that names a field: on the same row to its left (closest first), else just above it.</summary>
    private static string? NearestLabel(HmiField f, List<(string Text, int Left, int Top, int Width, int Height)> texts)
    {
        var midY = f.Top + f.Height / 2;
        var left = texts.Where(t => t.Left + t.Width <= f.Left + 10 && f.Left - (t.Left + t.Width) < 450
                                    && midY >= t.Top - 4 && midY <= t.Top + Math.Max(t.Height, 12) + 4)
            .OrderBy(t => f.Left - (t.Left + t.Width)).Select(t => t.Text).FirstOrDefault();
        if (left != null) return left;
        return texts.Where(t => t.Top + t.Height <= f.Top + 4 && f.Top - (t.Top + t.Height) < 40
                                && t.Left < f.Left + Math.Max(f.Width, 40) && t.Left + t.Width > f.Left - 20)
            .OrderBy(t => f.Top - (t.Top + t.Height)).Select(t => t.Text).FirstOrDefault();
    }

    // ------------------------------------------------------------------ WinCC Unified (object dump)

    private static void ReadUnified(HmiDevice hmi, string file)
    {
        if (!File.Exists(file)) return;
        using var doc = JsonDocument.Parse(File.ReadAllText(file));
        var r = doc.RootElement;
        foreach (var t in Arr(r, "tags"))
            if (Str(t, "Name") is { } name)
                hmi.Tags.Add(new HmiTag(name, Str(t, "PlcTag") ?? Str(t, "ControllerTag"), Str(t, "DataType"), Str(t, "Comment"), Str(t, "Connection"), Str(t, "table") ?? ""));
        var plc = hmi.Tags.Where(t => t.PlcTag != null).GroupBy(t => t.Name).ToDictionary(g => g.Key, g => g.First().PlcTag!);
        foreach (var (key, kind) in new[] { ("discreteAlarms", "discrete"), ("analogAlarms", "analog") })
            foreach (var a in Arr(r, key))
            {
                var text = Str(a, "EventText") ?? Str(a, "AlarmText") ?? Str(a, "Name");
                if (text == null) continue;
                var tag = Str(a, "RaisedStateTag") ?? Str(a, "TriggerTag") ?? Str(a, "Tag") ?? Str(a, "RaisedStateTagName");
                int? bit = Num(a, "TriggerBitAddress") ?? Num(a, "RaisedStateBitAddress") ?? Num(a, "TriggerBit");
                var symbol = tag != null && plc.TryGetValue(tag, out var p) ? PlcSymbol(p) : null;
                hmi.Alarms.Add(new HmiAlarm(Str(a, "Name") ?? text, Num(a, "Id")?.ToString() ?? Str(a, "Id"), text, tag, symbol, bit,
                    Str(a, "AlarmClass"), Str(a, "InfoText"), kind));
            }
        foreach (var l in Arr(r, "textLists"))
            hmi.TextLists.Add(new HmiTextList(Str(l, "Name") ?? "", Arr(l, "entries")
                .Select(e => (Str(e, "Value") ?? Num(e, "Value")?.ToString() ?? Str(e, "From") ?? "?", Str(e, "Text") ?? ""))
                .Where(e => e.Item2.Length > 0).ToList()));
        foreach (var s in Arr(r, "screens"))
        {
            var screen = new HmiScreen { Name = Str(s, "Name") ?? "", Number = Num(s, "Number") };
            var texts = new List<(string, int, int, int, int)>();
            foreach (var i in Arr(s, "items"))
            {
                int x = Num(i, "Left") ?? 0, y = Num(i, "Top") ?? 0, w = Num(i, "Width") ?? 0, h = Num(i, "Height") ?? 0;
                var type = Str(i, "$type") ?? "";
                if (type.Contains("Text", StringComparison.Ordinal) && !type.Contains("IO", StringComparison.Ordinal) && Str(i, "Text") is { } t)
                {
                    texts.Add((Clean(new XElement("t", t)) ?? t, x, y, w, h));
                    continue;
                }
                var tag = Arr(i, "Dynamizations").Select(d => Str(d, "Tag")).FirstOrDefault(v => v != null);
                if (tag == null) continue;
                var mode = Str(i, "InputBehavior") ?? Str(i, "Mode") ?? "";
                screen.Fields.Add(new HmiField
                {
                    Screen = screen.Name, Kind = type.Replace("Hmi", ""), Name = Str(i, "Name"), Tag = tag,
                    Writes = mode.Contains("Input", StringComparison.OrdinalIgnoreCase) || type.Contains("Toggle", StringComparison.Ordinal) || type.Contains("Slider", StringComparison.Ordinal),
                    Unit = Str(i, "Unit"), Left = x, Top = y, Width = w, Height = h,
                });
            }
            foreach (var f in screen.Fields) f.Label = NearestLabel(f, texts);
            hmi.Screens.Add(screen);
        }
    }

    private static IEnumerable<JsonElement> Arr(JsonElement e, string name) =>
        e.TryGetProperty(name, out var a) && a.ValueKind == JsonValueKind.Array ? a.EnumerateArray() : Enumerable.Empty<JsonElement>();

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String && p.GetString() is { Length: > 0 } s ? s : null;

    private static int? Num(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.Number && p.TryGetInt32(out var i) ? i : null;
}
