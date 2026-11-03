using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace PlcKi.Core.Import;

/// <summary>One variable in a block interface or data block, with its nested members (structs, multi-instances).</summary>
public sealed class ExportMember
{
    public string Name { get; init; } = "";
    public string Section { get; init; } = "";
    /// <summary>TIA data type as written in the export: Bool, Real, "FB_Valve", Array[1..4] of Int, ...</summary>
    public string Datatype { get; init; } = "";
    public string? Comment { get; set; }
    public string? StartValue { get; init; }
    public List<ExportMember> Members { get; } = new();

    /// <summary>The block or data type name if the type is a user type ("FB_Valve" -> FB_Valve), else null.</summary>
    public string? UserType => Datatype.Length > 2 && Datatype[0] == '"' && Datatype[^1] == '"' ? Datatype[1..^1] : null;
}

/// <summary>A call found in a block's code: which block, and which instance (for FBs).</summary>
public sealed record ExportCall(string BlockType, string Block, string? Instance);

/// <summary>One block (OB, FB, FC, global DB, instance DB) or PLC data type from the export.</summary>
public sealed class ExportBlock
{
    public string Name { get; init; } = "";
    /// <summary>OB, FB, FC, GlobalDB, InstanceDB, ArrayDB, UDT.</summary>
    public string Kind { get; init; } = "";
    public int? Number { get; init; }
    public string? Language { get; init; }
    public string? Title { get; init; }
    public string? Comment { get; init; }
    /// <summary>For an instance DB: the FB it belongs to.</summary>
    public string? InstanceOf { get; init; }
    public List<ExportMember> Interface { get; } = new();
    /// <summary>Languages of the networks / compile units (LAD, FBD, SCL, STL, GRAPH...).</summary>
    public List<string> NetworkLanguages { get; } = new();
    public List<ExportCall> Calls { get; } = new();
    /// <summary>Readable SCL source, if the exporter wrote one.</summary>
    public string? SclSource { get; set; }
    /// <summary>LAD, FBD and GRAPH logic decoded into SCL-like text (null for SCL blocks and blocks without networks).</summary>
    public string? DecodedLogic { get; set; }
    /// <summary>Path of the XML file inside the export, for later decoding of LAD/GRAPH networks.</summary>
    public string File { get; init; } = "";
}

/// <summary>A tag PLC-Ki could read: its full symbol, TIA type, model type and the comment that explains it.</summary>
public sealed record ExportTag(string Symbol, string Datatype, string? ModelType, string? Comment, string Source);

/// <summary>A PLC tag-table entry (inputs, outputs, memory).</summary>
public sealed record ExportPlcTag(string Name, string Datatype, string? Address, string? Comment, string Table);

/// <summary>Everything the exporter wrote for one PLC.</summary>
public sealed class ExportPlc
{
    public string Name { get; init; } = "";
    public string? Device { get; init; }
    public string? Cpu { get; init; }
    public List<string> IpAddresses { get; init; } = new();
    public List<ExportBlock> Blocks { get; } = new();
    public List<ExportBlock> Types { get; } = new();
    public List<ExportPlcTag> PlcTags { get; } = new();
    public List<string> Problems { get; } = new();

    public ExportBlock? Block(string name) => Blocks.FirstOrDefault(b => b.Name == name) ?? Types.FirstOrDefault(b => b.Name == name);
}

/// <summary>
/// Reads the folder (or zip) written by tools/TiaImporter "export": manifest.json plus SimaticML XML per block,
/// tag table and data type. Pure file reading, no TIA needed, so it runs on any PC.
/// </summary>
public sealed class ProgramExport
{
    public string Project { get; private set; } = "";
    public string? Archive { get; private set; }
    public List<ExportPlc> Plcs { get; } = new();
    public List<string> Problems { get; } = new();

    private static readonly XNamespace Ns = "http://www.siemens.com/automation/Openness/SW/Interface/v5";

    /// <summary>Loads an export folder, a zip of it, or a folder that contains the export folder.</summary>
    public static ProgramExport Load(string path)
    {
        if (File.Exists(path) && path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            var dir = Path.Combine(Path.GetTempPath(), "plcki-export-" + Guid.NewGuid().ToString("N")[..8]);
            ZipFile.ExtractToDirectory(path, dir);
            try { return Load(dir); }
            finally { try { Directory.Delete(dir, true); } catch { /* temp folder, ignore */ } }
        }
        if (!Directory.Exists(path)) throw new FileNotFoundException($"Export not found: {path}");
        var manifest = Directory.GetFiles(path, "manifest.json", SearchOption.AllDirectories).OrderBy(p => p.Length).FirstOrDefault()
                       ?? throw new FileNotFoundException($"No manifest.json in {path}. Is this a TiaImporter export?");
        return LoadFolder(Path.GetDirectoryName(manifest)!);
    }

    private static ProgramExport LoadFolder(string root)
    {
        var export = new ProgramExport();
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "manifest.json")));
        var m = doc.RootElement;
        export.Project = Str(m, "project") ?? Path.GetFileName(root);
        export.Archive = Str(m, "archive");
        if (m.TryGetProperty("problems", out var probs) && probs.ValueKind == JsonValueKind.Array)
            export.Problems.AddRange(probs.EnumerateArray().Select(p => p.ToString()));

        foreach (var p in m.GetProperty("plcs").EnumerateArray())
        {
            var plc = new ExportPlc
            {
                Name = Str(p, "name") ?? "PLC",
                Device = Str(p, "device"),
                Cpu = Str(p, "typeIdentifier")?.Replace("OrderNumber:", ""),
                IpAddresses = p.TryGetProperty("ipAddresses", out var ips)
                    ? ips.EnumerateArray().Select(x => x.ToString()).Where(x => x.Count(c => c == '.') == 3).ToList()
                    : new(),
            };
            var dir = Path.Combine(root, Str(p, "folder") ?? plc.Name);
            ReadBlocks(plc, Path.Combine(dir, "Blocks"), plc.Blocks);
            ReadBlocks(plc, Path.Combine(dir, "Types"), plc.Types);
            ReadTagTables(plc, Path.Combine(dir, "Tags"));
            var scl = Path.Combine(dir, "SclSource");
            if (Directory.Exists(scl))
                foreach (var f in Directory.GetFiles(scl, "*.scl", SearchOption.AllDirectories))
                    if (plc.Blocks.FirstOrDefault(b => b.Name == Path.GetFileNameWithoutExtension(f) || FileSafe(b.Name) == Path.GetFileNameWithoutExtension(f)) is { } b)
                        b.SclSource = File.ReadAllText(f).TrimStart('﻿');
            FillCommentsFromTypes(plc);
            export.Plcs.Add(plc);
        }
        return export;
    }

    /// <summary>The file name TiaImporter gives a block on Windows: "0.2. Inputs/Outputs Images" -> "0.2. Inputs_Outputs Images".</summary>
    internal static string FileSafe(string name)
    {
        foreach (var c in "\\/:*?\"<>|") name = name.Replace(c, '_');
        return name;
    }

    private static void ReadBlocks(ExportPlc plc, string dir, List<ExportBlock> into)
    {
        if (!Directory.Exists(dir)) return;
        foreach (var file in Directory.GetFiles(dir, "*.xml", SearchOption.AllDirectories).OrderBy(f => f, StringComparer.Ordinal))
        {
            try
            {
                var block = ReadBlock(file, Path.GetRelativePath(dir, file));
                if (block != null) into.Add(block);
            }
            catch (Exception ex)
            {
                plc.Problems.Add($"{Path.GetFileName(file)}: {ex.Message}");
            }
        }
    }

    private static ExportBlock? ReadBlock(string file, string relPath)
    {
        var doc = XDocument.Load(file);
        var el = doc.Root?.Elements().FirstOrDefault(e => e.Name.LocalName.StartsWith("SW.Blocks.") || e.Name.LocalName.StartsWith("SW.Types."));
        if (el == null) return null;
        var kind = el.Name.LocalName switch
        {
            "SW.Blocks.OB" => "OB",
            "SW.Blocks.FB" => "FB",
            "SW.Blocks.FC" => "FC",
            "SW.Blocks.GlobalDB" => "GlobalDB",
            "SW.Blocks.InstanceDB" => "InstanceDB",
            "SW.Blocks.ArrayDB" => "ArrayDB",
            "SW.Types.PlcStruct" => "UDT",
            var other => other.Replace("SW.Blocks.", "").Replace("SW.Types.", ""),
        };
        var attrs = el.Element("AttributeList");
        var block = new ExportBlock
        {
            Name = attrs?.Element("Name")?.Value ?? Path.GetFileNameWithoutExtension(file),
            Kind = kind,
            Number = int.TryParse(attrs?.Element("Number")?.Value, out var n) ? n : null,
            Language = attrs?.Element("ProgrammingLanguage")?.Value,
            InstanceOf = attrs?.Element("InstanceOfName")?.Value,
            Title = Text(el, "Title"),
            Comment = Text(el, "Comment"),
            File = relPath,
        };
        var sections = attrs?.Element("Interface")?.Element(Ns + "Sections");
        if (sections != null)
            foreach (var sec in sections.Elements(Ns + "Section"))
                foreach (var mem in sec.Elements(Ns + "Member"))
                    block.Interface.Add(ReadMember(mem, sec.Attribute("Name")?.Value ?? ""));

        var logic = new StringBuilder();
        var networkNo = 0;
        foreach (var cu in el.Descendants().Where(e => e.Name.LocalName == "SW.Blocks.CompileUnit"))
        {
            networkNo++;
            var lang = cu.Element("AttributeList")?.Element("ProgrammingLanguage")?.Value;
            if (lang != null) block.NetworkLanguages.Add(lang);
            if (block.Language == "SCL") continue;   // the SCL source covers pure SCL blocks
            try { DecodeNetwork(cu, lang, networkNo, logic); }
            catch (Exception ex) { logic.AppendLine($"// Network {networkNo}: could not decode ({ex.Message})"); }
        }
        if (logic.Length > 0) block.DecodedLogic = logic.ToString().TrimEnd();
        foreach (var call in el.Descendants().Where(e => e.Name.LocalName == "CallInfo"))
        {
            var inst = call.Elements().FirstOrDefault(e => e.Name.LocalName == "Instance");
            var comps = inst?.Descendants().Where(e => e.Name.LocalName == "Component").Select(c => c.Attribute("Name")?.Value).ToList();
            var instName = comps is { Count: > 0 }
                ? (inst!.Attribute("Scope")?.Value == "GlobalVariable" ? $"\"{comps[0]}\"" + string.Concat(comps.Skip(1).Select(c => "." + Quote(c!))) : "#" + string.Join(".", comps.Select(c => Quote(c!))))
                : null;
            var type = call.Attribute("BlockType")?.Value ?? "";
            var name = call.Attribute("Name")?.Value;
            // SCL calls of an FC ("VAL_To_String"(...)) carry the FC's name as the "instance" and no Name attribute
            if (string.IsNullOrEmpty(name) && type == "FC" && comps is { Count: > 0 }) { name = string.Join(".", comps); instName = null; }
            if (string.IsNullOrEmpty(name)) continue;
            block.Calls.Add(new ExportCall(type, name, instName));
        }
        return block;
    }

    /// <summary>
    /// Appends one LAD/FBD network, an SCL network of a LAD/FBD block, or the GRAPH sequence as text.
    /// Networks of pure SCL blocks are skipped (the SCL source covers them); STL is not decoded yet.
    /// </summary>
    private static void DecodeNetwork(XElement cu, string? lang, int no, StringBuilder logic)
    {
        var source = cu.Element("AttributeList")?.Element("NetworkSource");
        if (source == null) return;
        var graph = source.Elements().FirstOrDefault(e => e.Name.LocalName == "Graph");
        var flg = source.Elements().FirstOrDefault(e => e.Name.LocalName == "FlgNet");
        var st = source.Elements().FirstOrDefault(e => e.Name.LocalName == "StructuredText");
        if (graph == null && flg == null && st == null) return;

        var title = Text(cu, "Title");
        var comment = Text(cu, "Comment");
        var head = $"// Network {no}" + (lang != null ? $" ({lang})" : "") + (title != null ? ": " + title : "") + (comment != null ? " - " + comment : "");
        logic.AppendLine(head);
        if (graph != null)
            logic.AppendLine(NetworkDecoder.DecodeGraph(graph));
        else if (st != null)
            logic.AppendLine(NetworkDecoder.DecodeScl(st));
        else
            foreach (var s in NetworkDecoder.Decode(flg!)) logic.AppendLine(s);
        logic.AppendLine();
    }

    private static ExportMember ReadMember(XElement mem, string section)
    {
        var m = new ExportMember
        {
            Name = mem.Attribute("Name")?.Value ?? "",
            Section = section,
            Datatype = mem.Attribute("Datatype")?.Value ?? "",
            Comment = MemberComment(mem),
            StartValue = mem.Element(Ns + "StartValue")?.Value,
        };
        // Struct members are direct children; multi-instances and UDTs carry nested Sections
        foreach (var child in mem.Elements(Ns + "Member")) m.Members.Add(ReadMember(child, section));
        foreach (var sec in mem.Elements(Ns + "Sections").Elements(Ns + "Section"))
            foreach (var child in sec.Elements(Ns + "Member"))
                m.Members.Add(ReadMember(child, sec.Attribute("Name")?.Value ?? ""));
        return m;
    }

    private static string? MemberComment(XElement mem)
    {
        var texts = mem.Element(Ns + "Comment")?.Elements(Ns + "MultiLanguageText").ToList();
        if (texts == null || texts.Count == 0) return null;
        var t = texts.FirstOrDefault(x => (x.Attribute("Lang")?.Value ?? "").StartsWith("en", StringComparison.OrdinalIgnoreCase)) ?? texts[0];
        return string.IsNullOrWhiteSpace(t.Value) ? null : t.Value.Trim();
    }

    /// <summary>Block title or comment (MultilingualText with CompositionName Title/Comment directly under the block).</summary>
    private static string? Text(XElement block, string composition)
    {
        var mt = block.Element("ObjectList")?.Elements("MultilingualText").FirstOrDefault(e => e.Attribute("CompositionName")?.Value == composition);
        var items = mt?.Descendants("MultilingualTextItem").Select(i => i.Element("AttributeList")).Where(a => a != null).ToList();
        if (items == null || items.Count == 0) return null;
        var pick = items.FirstOrDefault(a => (a!.Element("Culture")?.Value ?? "").StartsWith("en", StringComparison.OrdinalIgnoreCase)) ?? items[0];
        var text = pick!.Element("Text")?.Value?.Trim().Trim('\'');
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    private static void ReadTagTables(ExportPlc plc, string dir)
    {
        if (!Directory.Exists(dir)) return;
        foreach (var file in Directory.GetFiles(dir, "*.xml", SearchOption.AllDirectories))
        {
            try
            {
                var doc = XDocument.Load(file);
                var table = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "SW.Tags.PlcTagTable")?.Element("AttributeList")?.Element("Name")?.Value
                            ?? Path.GetFileNameWithoutExtension(file);
                foreach (var tag in doc.Descendants().Where(e => e.Name.LocalName == "SW.Tags.PlcTag"))
                {
                    var a = tag.Element("AttributeList");
                    plc.PlcTags.Add(new ExportPlcTag(
                        a?.Element("Name")?.Value ?? "",
                        a?.Element("DataTypeName")?.Value ?? "",
                        a?.Element("LogicalAddress")?.Value,
                        Text(tag, "Comment"),
                        table));
                }
            }
            catch (Exception ex)
            {
                plc.Problems.Add($"{Path.GetFileName(file)}: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Instance DBs repeat the FB interface without comments. Copy the comments from the FB (and UDT) definitions,
    /// so "Station".M201.AtSpeed gets the comment written in FB_VFD.
    /// </summary>
    private static void FillCommentsFromTypes(ExportPlc plc)
    {
        void Fill(List<ExportMember> members, List<ExportMember>? definition)
        {
            if (definition == null) return;
            foreach (var m in members)
            {
                var def = definition.FirstOrDefault(d => d.Name == m.Name);
                if (def == null) continue;
                m.Comment ??= def.Comment;
                var typeDef = m.UserType is { } ut ? plc.Block(ut)?.Interface : def.Members;
                Fill(m.Members, typeDef is { Count: > 0 } ? typeDef : def.Members);
            }
        }
        foreach (var b in plc.Blocks)
        {
            if (b.Kind == "InstanceDB" && b.InstanceOf != null) Fill(b.Interface, plc.Block(b.InstanceOf)?.Interface);
            else
                foreach (var m in b.Interface)
                    if (m.UserType is { } ut && plc.Block(ut) is { } t) Fill(m.Members, t.Interface);
        }
    }

    // ---- tags ----

    private static readonly Regex ArrayRx = new(@"^Array\[(-?\d+)\.\.(-?\d+)\]\s+of\s+(.+)$", RegexOptions.IgnoreCase);

    /// <summary>Maps a TIA elementary type to a type the machine model reads, or null if PLC-Ki cannot use it.</summary>
    public static string? ModelType(string datatype) => datatype.Trim() switch
    {
        "Bool" => "Bool",
        "SInt" or "Int" or "USInt" or "UInt" or "Byte" or "Word" or "Char" => "Int",
        "DInt" or "UDInt" or "DWord" => "DInt",
        "Real" => "Real",
        "LReal" => "LReal",
        "Time" => "Time",
        _ => null,
    };

    /// <summary>
    /// Every value PLC-Ki could read from this PLC, with its full symbol: members of global and instance DBs
    /// (through structs, UDTs and multi-instances; arrays up to 32 elements) and the PLC tag tables.
    /// Temp and Constant sections are left out (not readable).
    /// </summary>
    public static List<ExportTag> Tags(ExportPlc plc)
    {
        var tags = new List<ExportTag>();
        foreach (var b in plc.Blocks.Where(b => b.Kind is "GlobalDB" or "InstanceDB"))
            foreach (var m in b.Interface.Where(m => m.Section is not ("Temp" or "Constant")))
                Walk(plc, m, $"\"{b.Name}\".{Quote(m.Name)}", b.Name, tags, 0);
        foreach (var t in plc.PlcTags)
            tags.Add(new ExportTag($"\"{t.Name}\"", t.Datatype, ModelType(t.Datatype), t.Comment, $"tag table {t.Table}" + (t.Address != null ? $" {t.Address}" : "")));
        return tags;
    }

    private static void Walk(ExportPlc plc, ExportMember m, string symbol, string source, List<ExportTag> tags, int depth)
    {
        if (depth > 8) return;
        // GRAPH runtime structures: a step is "active" (.X) and "active for" (.T); everything else
        // (transition internals, flags, offsets) is noise that pushed real tags out of the list
        if (m.Datatype.StartsWith("G7_Step", StringComparison.Ordinal))
        {
            tags.Add(new ExportTag($"{symbol}.X", "Bool", "Bool", "step active", source));
            tags.Add(new ExportTag($"{symbol}.T", "Time", "Time", "time the step has been active", source));
            return;
        }
        if (m.Datatype.StartsWith("G7_", StringComparison.Ordinal)) return;
        var arr = ArrayRx.Match(m.Datatype);
        if (arr.Success)
        {
            int lo = int.Parse(arr.Groups[1].Value), hi = int.Parse(arr.Groups[2].Value);
            var elem = arr.Groups[3].Value.Trim();
            if (ModelType(elem) is { } mt && hi - lo < 32)
                for (int i = lo; i <= hi; i++)
                    tags.Add(new ExportTag($"{symbol}[{i}]", elem, mt, m.Comment, source));
            else tags.Add(new ExportTag(symbol, m.Datatype, null, m.Comment, source));
            return;
        }
        if (m.Members.Count > 0)
        {
            foreach (var c in m.Members.Where(c => c.Section is not ("Temp" or "Constant")))
                Walk(plc, c, $"{symbol}.{Quote(c.Name)}", source, tags, depth + 1);
            return;
        }
        // A UDT member without nested members in this file: expand from the type definition
        if (m.UserType is { } ut && plc.Types.FirstOrDefault(t => t.Name == ut) is { } udt)
        {
            foreach (var c in udt.Interface)
                Walk(plc, c, $"{symbol}.{Quote(c.Name)}", source, tags, depth + 1);
            return;
        }
        tags.Add(new ExportTag(symbol, m.Datatype, ModelType(m.Datatype), m.Comment, source));
    }

    private static readonly Regex PlainName = new(@"^[A-Za-z_][A-Za-z0-9_]*$");
    internal static string Quote(string name) => PlainName.IsMatch(name) ? name : $"\"{name}\"";

    /// <summary>Normalises a symbol for comparison: no spaces, same quoting ("DB".a == "DB"."a").</summary>
    public static string NormalizeSymbol(string symbol)
    {
        var parts = new List<string>();
        var sb = new StringBuilder();
        bool inQuote = false;
        foreach (var ch in symbol.Trim())
        {
            if (ch == '"') { inQuote = !inQuote; continue; }
            if (ch == '.' && !inQuote) { parts.Add(sb.ToString().Trim()); sb.Clear(); continue; }
            sb.Append(ch);
        }
        parts.Add(sb.ToString().Trim());
        return string.Join(".", parts.Select((p, i) => i == 0 ? $"\"{p}\"" : p));
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;
}
