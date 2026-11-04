using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace PlcKi.Core.Import;

/// <summary>
/// Reads a Beckhoff TwinCAT 3 PLC project straight from its files: .TcPOU (programs, function blocks, functions
/// with methods and actions), .TcGVL (global variable lists), .TcDUT (structs, enums) and .TcTTO (tasks).
/// No TwinCAT installation is needed: the files are XML with the IEC 61131-3 text inside.
/// Structured Text is read as source; SFC, LD, FBD and CFC bodies are listed but not decoded yet.
/// Symbols are ADS paths as TwinCAT writes them: MAIN.fbPump.bRunning, GVL_IO.bStartButton.
/// </summary>
public sealed class TwinCatProject : IProgramReader
{
    public string Brand => PlcBrands.Beckhoff;

    private static readonly string[] Extensions = { ".TcPOU", ".TcGVL", ".TcDUT", ".TcTTO" };

    public bool CanRead(string folder) =>
        Directory.EnumerateFiles(folder, "*.plcproj", SearchOption.AllDirectories).Any()
        || Directory.EnumerateFiles(folder, "*.TcPOU", SearchOption.AllDirectories).Any();

    public ProgramExport Read(string folder)
    {
        var export = new ProgramExport();
        var tsproj = Directory.EnumerateFiles(folder, "*.tsproj", SearchOption.AllDirectories).OrderBy(p => p.Length).FirstOrDefault();
        var plcprojs = Directory.EnumerateFiles(folder, "*.plcproj", SearchOption.AllDirectories).OrderBy(p => p, StringComparer.Ordinal).ToList();
        export.Project = Path.GetFileNameWithoutExtension(tsproj ?? plcprojs.FirstOrDefault() ?? folder);
        var netId = tsproj != null ? NetId(tsproj) : null;
        var newest = Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)
            .Where(f => Extensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase))
            .Select(File.GetLastWriteTimeUtc).DefaultIfEmpty().Max();
        if (newest != default) export.ExportedAt = new DateTimeOffset(newest, TimeSpan.Zero);

        if (plcprojs.Count == 0)
            export.Plcs.Add(ReadPlc(folder, Path.GetFileName(folder.TrimEnd('/', '\\')), netId, null));
        else
            foreach (var proj in plcprojs)
                export.Plcs.Add(ReadPlc(Path.GetDirectoryName(proj)!, Path.GetFileNameWithoutExtension(proj), netId, proj));
        return export;
    }

    private static string? NetId(string tsproj)
    {
        try
        {
            var doc = XDocument.Load(tsproj);
            return doc.Descendants().SelectMany(e => e.Attributes())
                .Where(a => a.Name.LocalName.EndsWith("NetId", StringComparison.OrdinalIgnoreCase))
                .Select(a => a.Value).FirstOrDefault(v => Regex.IsMatch(v, @"^\d+(\.\d+){5}$"));
        }
        catch { return null; }
    }

    private static ExportPlc ReadPlc(string dir, string name, string? netId, string? plcproj)
    {
        var plc = new ExportPlc { Name = name, Brand = PlcBrands.Beckhoff, Device = "TwinCAT 3 PLC", AmsNetId = netId };
        var files = Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)
            .Where(f => Extensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase))
            .OrderBy(f => f, StringComparer.Ordinal).ToList();
        // A .plcproj lists the files that belong to the project; old copies lying next to it are left out
        if (plcproj != null && Included(plcproj) is { Count: > 0 } included)
            files = files.Where(f => included.Contains(Path.GetRelativePath(dir, f).Replace('\\', '/'))).ToList();

        foreach (var file in files)
        {
            try
            {
                var block = ReadFile(file, Path.GetRelativePath(dir, file));
                if (block == null) continue;
                (block.Kind is "UDT" or "ENUM" ? plc.Types : plc.Blocks).Add(block);
            }
            catch (Exception ex)
            {
                plc.Problems.Add($"{Path.GetFileName(file)}: {ex.Message}");
            }
        }
        Link(plc);
        return plc;
    }

    private static HashSet<string> Included(string plcproj)
    {
        try
        {
            return XDocument.Load(plcproj).Descendants().Where(e => e.Name.LocalName == "Compile")
                .Select(e => e.Attribute("Include")?.Value?.Replace('\\', '/')).Where(v => v != null)
                .ToHashSet(StringComparer.OrdinalIgnoreCase)!;
        }
        catch { return new(); }
    }

    private static readonly Regex HeaderRx = new(
        @"^\s*(?<kind>FUNCTION_BLOCK|PROGRAM|FUNCTION|INTERFACE)\b(\s+(PUBLIC|PRIVATE|PROTECTED|INTERNAL|ABSTRACT|FINAL))*\s+(?<name>[A-Za-z_]\w*)(\s+EXTENDS\s+(?<base>[\w.]+))?",
        RegexOptions.IgnoreCase);

    private static ExportBlock? ReadFile(string file, string relPath)
    {
        var root = XDocument.Load(file).Root;
        var el = root?.Elements().FirstOrDefault(e => e.Name.LocalName is "POU" or "GVL" or "DUT" or "Task" or "Itf");
        if (el == null || el.Name.LocalName == "Itf") return null;
        var name = el.Attribute("Name")?.Value ?? Path.GetFileNameWithoutExtension(file);
        var decl = el.Element("Declaration")?.Value ?? "";

        switch (el.Name.LocalName)
        {
            case "Task":
            {
                var task = new ExportBlock { Name = name, Kind = "Task", File = relPath, Comment = TaskComment(el) };
                foreach (var call in el.Descendants().Where(e => e.Name.LocalName == "PouCall"))
                    if (call.Element("Name")?.Value is { Length: > 0 } prg)
                        task.Calls.Add(new ExportCall("PRG", prg, null));
                return task;
            }
            case "GVL":
            {
                var gvl = new ExportBlock { Name = name, Kind = "GVL", File = relPath, Comment = LeadingComment(decl) };
                gvl.Interface.AddRange(IecText.Declarations(decl));
                return gvl;
            }
            case "DUT":
            {
                var clean = IecText.Clean(decl);
                var isStruct = Regex.IsMatch(clean, @"\b(STRUCT|UNION)\b", RegexOptions.IgnoreCase);
                var isEnum = !isStruct && Regex.IsMatch(clean, @":\s*\(", RegexOptions.IgnoreCase);
                var dut = new ExportBlock
                {
                    Name = name, Kind = isEnum ? "ENUM" : "UDT", File = relPath, Comment = LeadingComment(decl),
                    InstanceOf = Regex.Match(clean, @"\bEXTENDS\s+([\w.]+)", RegexOptions.IgnoreCase) is { Success: true } b ? b.Groups[1].Value : null,
                };
                if (isStruct) dut.Interface.AddRange(IecText.Declarations(decl));
                else if (isEnum)
                    dut.Interface.AddRange(IecText.EnumValues(decl).Select(v => new ExportMember { Name = v.Name, Section = "Constant", Datatype = "", StartValue = v.Value }));
                else if (Regex.Match(clean, @":\s*([^;]+);") is { Success: true } alias)   // TYPE T_Speed : REAL; END_TYPE
                    dut.Interface.Add(new ExportMember { Name = "", Section = "Alias", Datatype = alias.Groups[1].Value.Trim() });
                return dut;
            }
        }

        // POU
        var head = HeaderRx.Match(IecText.Clean(decl));
        var kindWord = head.Success ? head.Groups["kind"].Value.ToUpperInvariant() : "FUNCTION_BLOCK";
        var kind = kindWord switch { "PROGRAM" => "PRG", "FUNCTION" => "FC", _ => "FB" };
        var bodies = new List<string>();
        var (lang, body) = Body(el);
        var block = new ExportBlock
        {
            Name = name, Kind = kind, Language = lang, File = relPath, Comment = LeadingComment(decl),
            // FB inheritance: the base FB's members are part of every instance
            InstanceOf = head.Success && head.Groups["base"].Success ? head.Groups["base"].Value : null,
        };
        block.Interface.AddRange(IecText.Declarations(decl, functionLocals: kind == "FC"));
        if (lang != null) block.NetworkLanguages.Add(lang);
        if (!string.IsNullOrWhiteSpace(body)) bodies.Add(body.TrimEnd());

        // methods, actions and property accessors: their code is part of what the block does
        foreach (var part in el.Elements().Where(e => e.Name.LocalName is "Method" or "Action" or "Property"))
        {
            var partName = part.Attribute("Name")?.Value ?? "";
            foreach (var code in part.Name.LocalName == "Property"
                         ? part.Elements().Where(e => e.Name.LocalName is "Get" or "Set").Select(a => (Label: $"PROPERTY {partName}.{a.Name.LocalName}", El: a))
                         : new[] { (Label: $"{part.Name.LocalName.ToUpperInvariant()} {partName}", El: part) })
            {
                var (plang, pbody) = Body(code.El);
                if (plang != null && !block.NetworkLanguages.Contains(plang)) block.NetworkLanguages.Add(plang);
                var pdecl = code.El.Element("Declaration")?.Value?.Trim();
                var sb = new StringBuilder($"// {code.Label}");
                var locals = string.IsNullOrEmpty(pdecl) ? new() : IecText.Declarations(pdecl, functionLocals: true);
                if (locals.Count > 0)
                    sb.Append(" (" + string.Join(", ", locals.Select(l => $"{l.Section.ToLowerInvariant()} {l.Name} : {l.Datatype}")) + ")");
                sb.AppendLine();
                sb.Append(pbody?.TrimEnd() ?? $"// {plang ?? "no"} body, not decoded yet");
                bodies.Add(sb.ToString());
            }
        }
        if (bodies.Count > 0) block.SclSource = string.Join("\n\n", bodies);
        return block;
    }

    /// <summary>The implementation: ("ST", text) for Structured Text, (language, null) for graphical bodies.</summary>
    private static (string? Lang, string? Body) Body(XElement owner)
    {
        var impl = owner.Element("Implementation");
        var first = impl?.Elements().FirstOrDefault();
        if (first == null) return (null, null);
        var lang = first.Name.LocalName.ToUpperInvariant() switch
        {
            "ST" => "ST",
            "SFC" => "SFC",
            "LD" or "NWL" => "LD/FBD",
            "FBD" => "FBD",
            "CFC" => "CFC",
            "IL" => "IL",
            var other => other,
        };
        return lang == "ST" ? ("ST", first.Value.Replace("\r\n", "\n")) : (lang, null);
    }

    private static string? TaskComment(XElement task)
    {
        var cycle = task.Element("CycleTime")?.Value;
        var prio = task.Element("Priority")?.Value;
        var parts = new List<string>();
        // TwinCAT stores the cycle time in 100 ns units (10000 = 1 ms)
        if (long.TryParse(cycle, out var c)) parts.Add($"cycle {c / 10000.0:0.###} ms");
        if (prio != null) parts.Add($"priority {prio}");
        return parts.Count > 0 ? string.Join(", ", parts) : null;
    }

    /// <summary>The comment above or in the header line of a declaration (before the first VAR or STRUCT).</summary>
    private static string? LeadingComment(string decl)
    {
        var comments = new List<IecComment>();
        var clean = IecText.Clean(decl, comments);
        var first = Regex.Match(clean, @"\b(VAR\w*|STRUCT|UNION)\b|\(", RegexOptions.IgnoreCase);
        var limit = first.Success ? clean[..first.Index].Count(c => c == '\n') + 1 : int.MaxValue;
        var text = string.Join(" ", comments.Where(c => c.Line < limit && c.Text.Length > 0).Select(c => c.Text));
        return text.Length > 0 ? text : null;
    }

    // ---- links between blocks: which instance is of which FB, who calls whom, which globals are used ----

    private static readonly Regex CallRx = new(@"(?<![\w.])(?<path>[A-Za-z_]\w*(\s*\.\s*[A-Za-z_]\w*)*)\s*(\[[^\]]*\]\s*)?\(", RegexOptions.Compiled);
    private static readonly Regex QualifiedRx = new(@"(?<![\w.])(?<gvl>[A-Za-z_]\w*)\s*\.", RegexOptions.Compiled);

    private static void Link(ExportPlc plc)
    {
        var byName = plc.Blocks.Concat(plc.Types).GroupBy(b => b.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        // members typed with a project FB, struct or enum point at it (IEC names carry no quotes)
        void Mark(IEnumerable<ExportMember> members)
        {
            foreach (var m in members)
            {
                if (byName.TryGetValue(ElementType(m.Datatype), out var t) && t.Kind is "FB" or "UDT" or "ENUM")
                    m.TypeRef = t.Name;
                Mark(m.Members);
            }
        }
        foreach (var b in plc.Blocks.Concat(plc.Types)) Mark(b.Interface);

        var gvls = plc.Blocks.Where(b => b.Kind == "GVL").ToList();
        var globalVar = gvls.SelectMany(g => g.Interface.Select(m => (Gvl: g, Var: m)))
            .GroupBy(x => x.Var.Name, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        foreach (var b in plc.Blocks.Where(b => b.Kind is "PRG" or "FB" or "FC" && b.SclSource != null))
        {
            var code = IecText.Clean(b.SclSource!);
            var own = b.Interface.ToDictionary(m => m.Name, StringComparer.OrdinalIgnoreCase);
            foreach (Match m in CallRx.Matches(code))
            {
                var path = Regex.Replace(m.Groups["path"].Value, @"\s+", "").Split('.');
                ExportMember? inst = null;
                string? instName = null;
                if (own.TryGetValue(path[0], out var local) && path.Length == 1) { inst = local; instName = path[0]; }
                else if (path.Length == 2 && gvls.FirstOrDefault(g => g.Name.Equals(path[0], StringComparison.OrdinalIgnoreCase)) is { } g
                         && g.Interface.FirstOrDefault(v => v.Name.Equals(path[1], StringComparison.OrdinalIgnoreCase)) is { } gv)
                { inst = gv; instName = $"{g.Name}.{gv.Name}"; }
                else if (path.Length == 1 && !own.ContainsKey(path[0]) && globalVar.TryGetValue(path[0], out var gx)) { inst = gx.Var; instName = $"{gx.Gvl.Name}.{gx.Var.Name}"; }

                if (inst != null)
                {
                    var t = ElementType(inst.Datatype);
                    if (byName.TryGetValue(t, out var fb) && fb.Kind == "FB") b.Calls.Add(new ExportCall("FB", fb.Name, instName));
                    else if (IecText.StandardBlock(t) != null) b.Calls.Add(new ExportCall("FB", t.ToUpperInvariant(), instName));
                }
                else if (path.Length == 1 && byName.TryGetValue(path[0], out var target) && target.Kind is "FC" or "PRG")
                    b.Calls.Add(new ExportCall(target.Kind, target.Name, null));
            }
            foreach (Match m in QualifiedRx.Matches(code))
                if (gvls.FirstOrDefault(g => g.Name.Equals(m.Groups["gvl"].Value, StringComparison.OrdinalIgnoreCase)) is { } g)
                    b.GlobalNames.Add(g.Name);
            foreach (Match m in Regex.Matches(code, @"(?<![\w.])[A-Za-z_]\w*"))
                if (!own.ContainsKey(m.Value) && globalVar.TryGetValue(m.Value, out var gx))
                    b.GlobalNames.Add(gx.Gvl.Name);
        }
    }

    /// <summary>"ARRAY[1..4] OF FB_Valve" -> "FB_Valve"; "FB_Valve" -> "FB_Valve".</summary>
    private static string ElementType(string datatype)
    {
        var m = ArrayRx.Match(datatype);
        return (m.Success ? m.Groups["elem"].Value : datatype).Trim();
    }

    // ---- readable tags (ADS symbols) ----

    private static readonly Regex ArrayRx = new(@"^ARRAY\s*\[\s*(?<lo>-?\d+)\s*\.\.\s*(?<hi>-?\d+)\s*\]\s*OF\s+(?<elem>.+)$", RegexOptions.IgnoreCase);

    /// <summary>
    /// Every value PLC-Ki could read over ADS: the variables of each PROGRAM and global variable list, through
    /// function block instances, structs, standard timers and arrays (elementary arrays up to 32 elements, arrays
    /// of blocks or structs up to 16). Inputs, outputs and statics of FB instances are readable; temps, constants
    /// and VAR_IN_OUT references are not.
    /// </summary>
    public static List<ExportTag> Tags(ExportPlc plc)
    {
        var tags = new List<ExportTag>();
        foreach (var b in plc.Blocks.Where(b => b.Kind is "GVL" or "PRG"))
            foreach (var m in b.Interface.Where(Readable))
                Walk(plc, m.Datatype, m.Comment, $"{b.Name}.{m.Name}", b.Name, tags, 0, m.Address);
        return tags;
    }

    private static bool Readable(ExportMember m) => m.Section is "Input" or "Output" or "Static";

    private static void Walk(ExportPlc plc, string datatype, string? comment, string symbol, string source, List<ExportTag> tags, int depth, string? address = null)
    {
        if (depth > 8) return;
        var where = address != null ? $"{source} {address}" : source;
        var arr = ArrayRx.Match(datatype);
        if (arr.Success)
        {
            int lo = int.Parse(arr.Groups["lo"].Value), hi = int.Parse(arr.Groups["hi"].Value);
            var elem = arr.Groups["elem"].Value.Trim();
            var simple = ModelType(plc, elem);
            if (simple != null && hi - lo < 32)
                for (int i = lo; i <= hi; i++) tags.Add(new ExportTag($"{symbol}[{i}]", elem, simple, comment, where));
            else if (simple == null && hi - lo < 16 && Expandable(plc, elem))
                for (int i = lo; i <= hi; i++) Walk(plc, elem, comment, $"{symbol}[{i}]", source, tags, depth + 1);
            else tags.Add(new ExportTag(symbol, datatype, null, comment, where));
            return;
        }
        if (ModelType(plc, datatype) is { } mt) { tags.Add(new ExportTag(symbol, datatype, mt, comment, where)); return; }
        var members = Members(plc, datatype);
        if (members == null) { tags.Add(new ExportTag(symbol, datatype, null, comment, where)); return; }
        foreach (var c in members.Where(Readable))
            Walk(plc, c.Datatype, c.Comment, $"{symbol}.{c.Name}", source, tags, depth + 1);
    }

    private static bool Expandable(ExportPlc plc, string type) => Members(plc, type) != null;

    /// <summary>The readable members of a project FB or struct (base FB/struct first), or of a standard IEC block.</summary>
    private static List<ExportMember>? Members(ExportPlc plc, string type, int depth = 0)
    {
        if (IecText.StandardBlock(type) is { } std) return std.ToList();
        var t = plc.Blocks.Concat(plc.Types).FirstOrDefault(b => b.Name.Equals(type.Trim(), StringComparison.OrdinalIgnoreCase) && b.Kind is "FB" or "UDT");
        if (t == null || depth > 5) return null;
        var list = t.InstanceOf != null ? Members(plc, t.InstanceOf, depth + 1) ?? new() : new();
        list.AddRange(t.Interface);
        return list;
    }

    /// <summary>Elementary types, enums (read as their integer value) and aliases of either.</summary>
    private static string? ModelType(ExportPlc plc, string type)
    {
        if (IecText.ModelType(type) is { } mt) return mt;
        var t = plc.Types.FirstOrDefault(b => b.Name.Equals(type.Trim(), StringComparison.OrdinalIgnoreCase));
        return t?.Kind switch
        {
            "ENUM" => "Int",
            "UDT" when t.Interface is [{ Section: "Alias" } alias] => IecText.ModelType(alias.Datatype),
            _ => null,
        };
    }
}
