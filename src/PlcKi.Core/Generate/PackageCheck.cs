using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace PlcKi.Core.Generate;

/// <summary>A block or type a package creates, found by reading its file.</summary>
public sealed record PackageBlock(string Name, string Kind, string? Language, string File, string? InstanceOf);

/// <summary>
/// Offline checks of a generated package, before it goes near TIA: file names, one block per file, block headers,
/// SimaticML wiring (the same checks as plc-demo/xml-gen/check_demo_xml.py), tag addresses, the lessons from the
/// demo (reserved "dt", self-resetting timers, GRAPH FBs only as single instances) and no F-blocks.
/// TIA's own compile is the final check; these catch what would make the import fail outright.
/// </summary>
public static class PackageCheck
{
    private static readonly Regex SclHeader = new(@"^\s*(FUNCTION_BLOCK|FUNCTION|DATA_BLOCK|ORGANIZATION_BLOCK|TYPE)\s+""([^""]+)""", RegexOptions.Multiline | RegexOptions.IgnoreCase);
    private static readonly Regex SclEnd = new(@"^\s*END_(FUNCTION_BLOCK|FUNCTION|DATA_BLOCK|ORGANIZATION_BLOCK|TYPE)\b", RegexOptions.Multiline | RegexOptions.IgnoreCase);
    private static readonly Regex DbInstanceOf = new(@"^\s*""([^""]+)""\s*$", RegexOptions.Multiline);
    private static readonly Regex DeclaredDt = new(@"^\s*(dt|tod|ldt|dtl|date|time|string|real|int|bool|word|byte)\s*(\{[^}]*\})?\s*:(?!=)", RegexOptions.Multiline | RegexOptions.IgnoreCase);
    private static readonly Regex SelfResetTimer = new(@"#(\w+)\s*\(\s*IN\s*:=\s*NOT\s+#\1\.Q\b", RegexOptions.IgnoreCase);
    private static readonly Regex SclMemberOfType = new(@"^\s*([A-Za-z_]\w*)\s*(?:\{[^}]*\})?\s*:\s*""([^""]+)""\s*;", RegexOptions.Multiline);
    private static readonly Regex Address = new(@"^%(I|Q|M)(\d+\.[0-7]|[BWD]\d+)$", RegexOptions.IgnoreCase);
    private static readonly Regex Identifier = new(@"^[A-Za-z_][A-Za-z0-9_]*$");

    private const string IfNs = "http://www.siemens.com/automation/Openness/SW/Interface/v5";

    /// <summary>Required inputs per LAD part, as in check_demo_xml.py. Parts not listed are not checked.</summary>
    private static readonly Dictionary<string, string[]> RequiredInputs = new()
    {
        ["Contact"] = new[] { "in", "operand" }, ["Coil"] = new[] { "in", "operand" }, ["SCoil"] = new[] { "in", "operand" },
        ["RCoil"] = new[] { "in", "operand" }, ["Move"] = new[] { "en", "in", "out1" },
        ["Add"] = new[] { "en", "in1", "in2", "out" }, ["Sub"] = new[] { "en", "in1", "in2", "out" }, ["Mul"] = new[] { "en", "in1", "in2", "out" },
        ["Gt"] = new[] { "pre", "in1", "in2" }, ["Ge"] = new[] { "pre", "in1", "in2" }, ["Lt"] = new[] { "pre", "in1", "in2" },
        ["Le"] = new[] { "pre", "in1", "in2" }, ["Eq"] = new[] { "pre", "in1", "in2" }, ["Ne"] = new[] { "pre", "in1", "in2" },
        ["TON"] = new[] { "IN", "PT" }, ["TOF"] = new[] { "IN", "PT" }, ["TP"] = new[] { "IN", "PT" },
    };

    /// <summary>The blocks and types the package creates.</summary>
    public static List<PackageBlock> Blocks(BlockPackage package)
    {
        var list = new List<PackageBlock>();
        foreach (var f in package.Files)
        {
            if (f.Extension is ".scl" or ".db" or ".udt")
            {
                foreach (Match m in SclHeader.Matches(f.Text))
                {
                    var kind = m.Groups[1].Value.ToUpperInvariant() switch
                    {
                        "FUNCTION_BLOCK" => "FB", "FUNCTION" => "FC", "ORGANIZATION_BLOCK" => "OB", "TYPE" => "UDT", _ => "DB",
                    };
                    string? inst = null;
                    if (kind == "DB")
                    {
                        // Instance DB: the line after the header attributes is the FB name in quotes
                        var body = f.Text[(m.Index + m.Length)..];
                        var beginAt = body.IndexOf("BEGIN", StringComparison.OrdinalIgnoreCase);
                        var head = beginAt >= 0 ? body[..beginAt] : body;
                        if (!Regex.IsMatch(head, @"\bSTRUCT\b|\bVAR\b", RegexOptions.IgnoreCase))
                        {
                            var im = DbInstanceOf.Match(head);
                            if (im.Success) inst = im.Groups[1].Value;
                        }
                    }
                    list.Add(new PackageBlock(m.Groups[2].Value, kind, kind is "FB" or "FC" or "OB" ? "SCL" : null, f.Name, inst));
                }
            }
            else if (f.Extension == ".xml")
            {
                try
                {
                    var el = XDocument.Parse(f.Text).Root?.Elements().FirstOrDefault(e => e.Name.LocalName.StartsWith("SW.Blocks.") || e.Name.LocalName.StartsWith("SW.Types."));
                    var attrs = el?.Element("AttributeList");
                    var name = attrs?.Element("Name")?.Value;
                    if (el == null || name == null) continue;
                    var kind = el.Name.LocalName switch
                    {
                        "SW.Blocks.FB" => "FB", "SW.Blocks.FC" => "FC", "SW.Blocks.OB" => "OB", "SW.Blocks.GlobalDB" => "DB",
                        "SW.Blocks.InstanceDB" => "DB", "SW.Types.PlcStruct" => "UDT", var other => other,
                    };
                    list.Add(new PackageBlock(name, kind, attrs?.Element("ProgrammingLanguage")?.Value, f.Name, attrs?.Element("InstanceOfName")?.Value));
                }
                catch (System.Xml.XmlException) { /* reported by Check */ }
            }
        }
        return list;
    }

    /// <summary>Names of everything the package creates: blocks, types and tags.</summary>
    public static HashSet<string> Names(BlockPackage package)
    {
        var names = new HashSet<string>(Blocks(package).Select(b => b.Name), StringComparer.OrdinalIgnoreCase);
        foreach (var f in package.Files.Where(f => f.Extension == ".json"))
        {
            try
            {
                foreach (var t in JsonSerializer.Deserialize<TagTables>(f.Text)?.Tables ?? new())
                    foreach (var tag in t.Tags) names.Add(tag.Name);
            }
            catch (JsonException) { /* reported by Check */ }
        }
        return names;
    }

    /// <summary>
    /// Problems in the package, in words Claude can act on. <paramref name="existing"/> are the names already in the
    /// PLC program (when known) and <paramref name="replaces"/> the ones this package may overwrite.
    /// </summary>
    public static List<string> Check(BlockPackage package, ISet<string>? existing = null, ISet<string>? replaces = null, IEnumerable<string>? mustChange = null)
    {
        var problems = new List<string>();
        if (package.Files.Count == 0)
        {
            problems.Add("The answer has no <file name=\"...\"> sections.");
            return problems;
        }
        if (package.Review.Length == 0) problems.Add("The <review> section is missing.");

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in package.Files)
        {
            if (!BlockPackage.FileNamePattern.IsMatch(f.Name))
                problems.Add($"{f.Name}: file names must look like 10_FB_Name.scl (two digits, underscore, letters/digits/_; .udt .db .scl .xml or .json).");
            if (!names.Add(f.Name)) problems.Add($"{f.Name}: the file appears twice.");
            switch (f.Extension)
            {
                case ".scl": case ".db": case ".udt": CheckSource(f, problems, replaces); break;
                case ".xml": CheckXml(f, problems); break;
                case ".json": CheckTags(f, problems, existing, replaces); break;
            }
        }

        var blocks = Blocks(package);
        foreach (var dup in blocks.GroupBy(b => b.Name, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
            problems.Add($"\"{dup.Key}\" is created by more than one file ({string.Join(", ", dup.Select(b => b.File))}).");

        // GRAPH FBs can't be multi-instances (refused by TIA on the 1516 FW V1.8): each needs its own instance DB.
        var graphFbs = blocks.Where(b => b.Kind == "FB" && b.Language == "GRAPH").Select(b => b.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var f in package.Files)
        {
            IEnumerable<(string member, string type)> members = f.Extension switch
            {
                ".scl" or ".udt" => SclMemberOfType.Matches(f.Text).Select(m => (m.Groups[1].Value, m.Groups[2].Value)),
                ".xml" => XmlMembersOfType(f.Text),
                _ => Enumerable.Empty<(string, string)>(),
            };
            foreach (var (member, type) in members)
                if (graphFbs.Contains(type))
                    problems.Add($"{f.Name}: {member} is a multi-instance of the GRAPH block \"{type}\". GRAPH FBs need their own instance DB (a .db file \"DB_x\" of \"{type}\"), called from the caller with that DB.");
        }

        // Instance DBs: their FB must exist in the package or in the program, and come earlier in the import order
        foreach (var db in blocks.Where(b => b.InstanceOf != null))
        {
            var fb = blocks.FirstOrDefault(b => b.Name.Equals(db.InstanceOf, StringComparison.OrdinalIgnoreCase));
            if (fb != null && string.CompareOrdinal(fb.File, db.File) > 0)
                problems.Add($"{db.File}: instance DB of \"{db.InstanceOf}\" must come after {fb.File} in the file order (number prefix).");
            if (fb == null && existing != null && !existing.Contains(db.InstanceOf!) && !BuiltInFb(db.InstanceOf!))
                problems.Add($"{db.File}: instance DB of \"{db.InstanceOf}\", which is neither in the package nor in the program.");
        }

        if (existing != null)
            foreach (var b in blocks)
                if (existing.Contains(b.Name) && (replaces == null || !replaces.Contains(b.Name)))
                    problems.Add($"\"{b.Name}\" ({b.File}) already exists in the PLC program and is not one of the blocks you were asked to change. Use a new name.");

        // A block the user asked to change must come back under its exact name, or the import adds a copy nobody calls
        foreach (var name in mustChange ?? Enumerable.Empty<string>())
            if (!blocks.Any(b => b.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
                problems.Add($"You were asked to change \"{name}\", but no file holds a block with exactly that name. Answer with the changed block under its existing name \"{name}\" (keep the quotes and every character); do not rename it.");
        return problems;
    }

    private static bool BuiltInFb(string name) => name.StartsWith("TON", StringComparison.OrdinalIgnoreCase) || name.StartsWith("TOF", StringComparison.OrdinalIgnoreCase)
                                                  || name.StartsWith("TP", StringComparison.OrdinalIgnoreCase) || name.StartsWith("CTU", StringComparison.OrdinalIgnoreCase);

    private static void CheckSource(PackageFile f, List<string> problems, ISet<string>? replaces = null)
    {
        var heads = SclHeader.Matches(f.Text);
        var ends = SclEnd.Matches(f.Text);
        if (heads.Count == 0) { problems.Add($"{f.Name}: no block header (FUNCTION_BLOCK \"Name\", FUNCTION \"Name\" : Void, DATA_BLOCK \"Name\" or TYPE \"Name\")."); return; }
        if (heads.Count > 1) problems.Add($"{f.Name}: {heads.Count} blocks in one file. Put each block, DB or type in its own file.");
        if (ends.Count != heads.Count) problems.Add($"{f.Name}: {heads.Count} block header(s) but {ends.Count} END_... line(s).");
        var kind = heads[0].Groups[1].Value.ToUpperInvariant();
        var expected = f.Extension switch { ".db" => "DATA_BLOCK", ".udt" => "TYPE", _ => null };
        if (expected != null && kind != expected) problems.Add($"{f.Name}: a {f.Extension} file must hold a {expected}, not a {kind}.");
        if (f.Extension == ".scl" && kind is "DATA_BLOCK" or "TYPE") problems.Add($"{f.Name}: put a {kind} in a {(kind == "TYPE" ? ".udt" : ".db")} file.");
        foreach (Match m in DeclaredDt.Matches(f.Text)) problems.Add($"{f.Name}: \"{m.Groups[1].Value}\" is a reserved word in SCL (\"dt\" is DATE_AND_TIME); rename that variable.");
        foreach (Match m in SelfResetTimer.Matches(f.Text))
            problems.Add($"{f.Name}: #{m.Groups[1].Value}(IN := NOT #{m.Groups[1].Value}.Q ...) does not pulse reliably on S7-1500; use a separate timer reset or compare elapsed time.");
        if (Regex.IsMatch(f.Text, @"\bF_(SCL|LAD|FBD)\b|S7_F_|\{\s*S7_Safety", RegexOptions.IgnoreCase))
            problems.Add($"{f.Name}: looks like a safety (F) block. F-blocks are out of scope for the generator.");
        // An existing block being changed keeps its name, whatever characters the plant used ("0.2. Inputs/Outputs Images")
        if (!heads[0].Groups[2].Value.All(c => char.IsLetterOrDigit(c) || c == '_') && replaces?.Contains(heads[0].Groups[2].Value) != true)
            problems.Add($"{f.Name}: block name \"{heads[0].Groups[2].Value}\" should only use letters, digits and _.");
    }

    private static void CheckXml(PackageFile f, List<string> problems)
    {
        XDocument doc;
        try { doc = XDocument.Parse(f.Text); }
        catch (System.Xml.XmlException ex) { problems.Add($"{f.Name}: not well-formed XML: {ex.Message}"); return; }
        if (doc.Root?.Name.LocalName != "Document") { problems.Add($"{f.Name}: the root element must be <Document>."); return; }
        var blockEls = doc.Root.Elements().Where(e => e.Name.LocalName.StartsWith("SW.Blocks.") || e.Name.LocalName.StartsWith("SW.Types.")).ToList();
        if (blockEls.Count != 1) { problems.Add($"{f.Name}: needs exactly one SW.Blocks.* element, found {blockEls.Count}."); return; }
        var el = blockEls[0];
        var attrs = el.Element("AttributeList");
        if (attrs?.Element("Name") == null) problems.Add($"{f.Name}: <Name> is missing in the AttributeList.");
        var lang = attrs?.Element("ProgrammingLanguage")?.Value;
        if (lang != null && lang.StartsWith("F_", StringComparison.OrdinalIgnoreCase))
            problems.Add($"{f.Name}: {lang} is a safety language. F-blocks are out of scope for the generator.");
        if (el.Name.LocalName is "SW.Blocks.FB" or "SW.Blocks.FC" or "SW.Blocks.OB" && lang is not ("LAD" or "FBD" or "GRAPH" or "SCL" or "STL"))
            problems.Add($"{f.Name}: ProgrammingLanguage must be LAD, FBD or GRAPH (got \"{lang}\"). Write SCL blocks as .scl sources.");

        // Interface member names, to check #local operands
        var iface = new HashSet<string>(StringComparer.Ordinal);
        var dupes = 0;
        foreach (var sec in el.Descendants(XName.Get("Section", IfNs)))
            foreach (var m in sec.Elements(XName.Get("Member", IfNs)))
                if (!iface.Add(m.Attribute("Name")?.Value ?? "")) dupes++;
        if (dupes > 0) problems.Add($"{f.Name}: {dupes} duplicate interface member name(s).");

        // IDs of the XML objects must be unique in the file
        var ids = el.DescendantsAndSelf().Select(e => e.Attribute("ID")?.Value).Where(v => v != null).ToList();
        var dupIds = ids.GroupBy(v => v).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
        if (dupIds.Count > 0) problems.Add($"{f.Name}: object ID(s) used more than once: {string.Join(", ", dupIds.Take(5))}. Every ID=\"..\" must be unique in the file (hex counter).");

        int n = 0;
        foreach (var flg in el.Descendants().Where(e => e.Name.LocalName == "FlgNet"))
        {
            n++;
            CheckFlgNet(flg, $"{f.Name} network {n}", iface, lang == "GRAPH", problems);
        }
    }

    private static void CheckFlgNet(XElement flg, string where, HashSet<string> iface, bool graph, List<string> problems)
    {
        var uids = new Dictionary<string, XElement>();
        foreach (var e in flg.Descendants())
        {
            var u = e.Attribute("UId")?.Value;
            if (u == null || e.Name.LocalName is "NameCon" or "IdentCon") continue;
            if (!uids.TryAdd(u, e)) problems.Add($"{where}: duplicate UId {u}.");
        }
        var connected = new Dictionary<string, HashSet<string>>();
        foreach (var wire in flg.Descendants().Where(e => e.Name.LocalName == "Wire"))
            foreach (var c in wire.Elements())
            {
                if (c.Name.LocalName is not ("NameCon" or "IdentCon")) continue;
                var u = c.Attribute("UId")?.Value ?? "";
                if (!uids.ContainsKey(u)) problems.Add($"{where}: wire {wire.Attribute("UId")?.Value} points to missing UId {u}.");
                if (c.Name.LocalName == "NameCon")
                {
                    if (!connected.TryGetValue(u, out var set)) connected[u] = set = new HashSet<string>();
                    set.Add(c.Attribute("Name")?.Value ?? "");
                }
            }
        foreach (var p in flg.Descendants().Where(e => e.Name.LocalName == "Part"))
        {
            var name = p.Attribute("Name")?.Value ?? "";
            if (!RequiredInputs.TryGetValue(name, out var need)) continue;
            var u = p.Attribute("UId")?.Value ?? "";
            var have = connected.GetValueOrDefault(u) ?? new HashSet<string>();
            var missing = need.Where(x => !have.Contains(x)).ToList();
            if (missing.Count > 0) problems.Add($"{where}: {name} UId {u} has unwired {string.Join(", ", missing)}.");
        }
        if (graph) return;   // GRAPH transitions/steps reference their own system members
        foreach (var a in flg.Descendants().Where(e => e.Name.LocalName == "Access" && e.Attribute("Scope")?.Value == "LocalVariable"))
        {
            var first = a.Descendants().FirstOrDefault(e => e.Name.LocalName == "Component")?.Attribute("Name")?.Value;
            if (first != null && iface.Count > 0 && !iface.Contains(first))
                problems.Add($"{where}: #{first} is not in the block interface.");
        }
    }

    private static IEnumerable<(string, string)> XmlMembersOfType(string xml)
    {
        XDocument doc;
        try { doc = XDocument.Parse(xml); } catch (System.Xml.XmlException) { yield break; }
        foreach (var m in doc.Descendants(XName.Get("Member", IfNs)))
        {
            var t = m.Attribute("Datatype")?.Value ?? "";
            if (t.Length > 2 && t[0] == '"' && t[^1] == '"') yield return (m.Attribute("Name")?.Value ?? "", t[1..^1]);
        }
    }

    private static void CheckTags(PackageFile f, List<string> problems, ISet<string>? existing, ISet<string>? replaces)
    {
        TagTables? tables;
        try { tables = JsonSerializer.Deserialize<TagTables>(f.Text); }
        catch (JsonException ex) { problems.Add($"{f.Name}: not valid tag table JSON: {ex.Message}"); return; }
        if (tables == null || tables.Tables.Count == 0) { problems.Add($"{f.Name}: no tag tables. Format: {{\"tables\":[{{\"name\":..,\"tags\":[..]}}]}}."); return; }
        var tagNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var addresses = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in tables.Tables)
        {
            if (string.IsNullOrWhiteSpace(t.Name)) problems.Add($"{f.Name}: a tag table has no name.");
            foreach (var tag in t.Tags)
            {
                var where = $"{f.Name}: tag \"{tag.Name}\"";
                if (!Identifier.IsMatch(tag.Name)) problems.Add($"{where}: names use letters, digits and _ only.");
                if (!tagNames.Add(tag.Name)) problems.Add($"{where} appears twice.");
                if (existing != null && existing.Contains(tag.Name) && (replaces == null || !replaces.Contains(tag.Name)))
                    problems.Add($"{where} already exists in the PLC program. Use the existing tag instead of creating it.");
                var m = Address.Match(tag.Address);
                if (!m.Success) { problems.Add($"{where}: address \"{tag.Address}\" must look like %I0.0, %QW64 or %MD100."); continue; }
                var size = m.Groups[2].Value[0] switch { 'B' or 'b' => 8, 'W' or 'w' => 16, 'D' or 'd' => 32, _ => 1 };
                var need = TypeSize(tag.DataType);
                if (need != null && need != size)
                    problems.Add($"{where}: a {tag.DataType} needs a {(need == 1 ? "bit address like %I0.0" : need == 8 ? "byte address like %IB0" : need == 16 ? "word address like %IW64" : "double word address like %MD100")}, not {tag.Address}.");
                if (addresses.TryGetValue(tag.Address, out var other)) problems.Add($"{where}: address {tag.Address} is also used by \"{other}\".");
                else addresses[tag.Address] = tag.Name;
            }
        }
    }

    private static int? TypeSize(string type) => type.Trim().ToLowerInvariant() switch
    {
        "bool" => 1,
        "byte" or "usint" or "sint" or "char" => 8,
        "word" or "int" or "uint" => 16,
        "dword" or "dint" or "udint" or "real" or "time" => 32,
        _ => null,
    };
}
