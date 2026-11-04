using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Siemens.Engineering;
using Siemens.Engineering.Compiler;
using Siemens.Engineering.SW;
using Siemens.Engineering.SW.Blocks;
using Siemens.Engineering.SW.ExternalSources;
using Siemens.Engineering.SW.Tags;
using Siemens.Engineering.SW.Types;

namespace TiaImporter
{
    /// <summary>
    /// Loads one version of a generated block package (from "PlcKi.Server generate") into the TIA project the user
    /// names: tag tables, then the files in their number order (.udt/.db/.scl as external sources, .xml as SimaticML),
    /// then compiles the PLC software and writes import-report.json into the version folder.
    ///
    /// Safety: refuses to overwrite blocks, types or tags that already exist in the project unless the package lists
    /// them as blocks it was asked to change (or --replace is given); exports a copy of every block it replaces to
    /// "before\" first; saves the project only when everything imported and compiled without errors (unless
    /// --keep-on-error). Never downloads anything to a PLC.
    /// </summary>
    internal sealed class BlockImport
    {
        private static readonly Regex FileName = new Regex(@"^\d{2}_[A-Za-z0-9_]+\.(udt|db|scl|xml|json)$", RegexOptions.IgnoreCase);
        private static readonly Regex SclHeader = new Regex(@"^\s*(FUNCTION_BLOCK|FUNCTION|DATA_BLOCK|ORGANIZATION_BLOCK|TYPE)\s+""([^""]+)""", RegexOptions.Multiline | RegexOptions.IgnoreCase);

        private readonly TextWriter _log;
        public BlockImport(TextWriter log) { _log = log; }

        private sealed class Item
        {
            public FileInfo File;
            public string Kind;      // "tags", "source", "block", "type"
            public List<string> Names = new List<string>();
            public bool IsType;
        }

        public int Run(FileInfo projectFile, DirectoryInfo package, string plcName, string versionName, bool replaceAll, bool keepOnError)
        {
            // 1. Which version folder, and which existing names it may replace
            DirectoryInfo dir;
            var replaces = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var manifest = new FileInfo(Path.Combine(package.FullName, "generated.json"));
            var parentManifest = new FileInfo(Path.Combine(package.Parent?.FullName ?? package.FullName, "generated.json"));
            if (manifest.Exists)
            {
                using (var doc = JsonDocument.Parse(File.ReadAllText(manifest.FullName)))
                {
                    var versions = doc.RootElement.GetProperty("versions").EnumerateArray().ToList();
                    if (versions.Count == 0) throw new InvalidOperationException("The package has no versions yet.");
                    var v = versionName == null ? versions.Last()
                        : versions.FirstOrDefault(x => x.GetProperty("folder").GetString().Equals(versionName, StringComparison.OrdinalIgnoreCase)
                                                    || x.GetProperty("version").GetInt32().ToString() == versionName.TrimStart('v', 'V').TrimStart('0'));
                    if (v.ValueKind != JsonValueKind.Object) throw new InvalidOperationException($"Version {versionName} is not in {manifest.FullName}.");
                    dir = new DirectoryInfo(Path.Combine(package.FullName, v.GetProperty("folder").GetString()));
                    if (v.TryGetProperty("replaces", out var r)) foreach (var x in r.EnumerateArray()) replaces.Add(x.GetString());
                }
            }
            else
            {
                dir = package;   // a version folder given directly
                if (parentManifest.Exists)
                    using (var doc = JsonDocument.Parse(File.ReadAllText(parentManifest.FullName)))
                        foreach (var v in doc.RootElement.GetProperty("versions").EnumerateArray())
                            if (v.GetProperty("folder").GetString().Equals(dir.Name, StringComparison.OrdinalIgnoreCase) && v.TryGetProperty("replaces", out var r))
                                foreach (var x in r.EnumerateArray()) replaces.Add(x.GetString());
            }
            if (!dir.Exists) throw new DirectoryNotFoundException(dir.FullName);
            var own = OwnNames(manifest.Exists ? package : dir.Parent, projectFile);
            if (own.Count > 0) replaces.UnionWith(own);

            // 2. The files, in their number order, and the names each creates
            var items = ReadItems(dir);
            if (items.Count == 0) throw new InvalidOperationException($"No package files (like 10_FB_Name.scl) in {dir.FullName}.");
            _log.WriteLine($"Package {dir.Name}: {items.Count} file(s), {string.Join(", ", items.SelectMany(i => i.Kind == "tags" ? new[] { i.Names.Count + " tag(s)" } : i.Names.ToArray()))}");

            var report = new Dictionary<string, object>
            {
                ["project"] = projectFile.FullName,
                ["version"] = dir.Name,
                ["time"] = DateTimeOffset.Now.ToString("o"),
            };
            var fileErrors = new List<Dictionary<string, string>>();
            var messages = new List<Dictionary<string, string>>();

            _log.WriteLine("Starting TIA Portal V20 without user interface...");
            using (var tia = new TiaPortal(TiaPortalMode.WithoutUserInterface))
            {
                var project = tia.Projects.Open(projectFile);
                try
                {
                    var plcs = TiaHelpers.FindPlcs(project);
                    var plc = plcName == null ? (plcs.Count == 1 ? plcs[0] : null) : plcs.FirstOrDefault(p => p.Software.Name == plcName || p.DeviceName == plcName);
                    if (plc == null)
                        throw new InvalidOperationException(plcs.Count == 0 ? "No PLC in the project." : $"Choose the PLC with --plc: {string.Join(", ", plcs.Select(p => p.Software.Name))}");
                    var sw = plc.Software;
                    report["plc"] = sw.Name;
                    _log.WriteLine($"Project {project.Name}, PLC {sw.Name} ({plc.TypeIdentifier})");

                    // 3. Refuse to overwrite anything the package was not asked to change
                    var blocks = AllBlocks(sw.BlockGroup).ToDictionary(b => b.Name, StringComparer.OrdinalIgnoreCase);
                    var types = AllTypes(sw.TypeGroup).ToDictionary(t => t.Name, StringComparer.OrdinalIgnoreCase);
                    var tags = new Dictionary<string, PlcTag>(StringComparer.OrdinalIgnoreCase);
                    foreach (var t in AllTables(sw.TagTableGroup)) foreach (PlcTag tag in t.Tags) tags[tag.Name] = tag;

                    var conflicts = items.SelectMany(i => i.Names.Where(n => (i.Kind == "tags" ? tags.ContainsKey(n) : blocks.ContainsKey(n) || types.ContainsKey(n)) && !replaces.Contains(n)))
                        .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                    if (conflicts.Count > 0 && !replaceAll)
                    {
                        _log.WriteLine("These already exist in the project and the package was not asked to change them:");
                        foreach (var c in conflicts) _log.WriteLine("  " + c);
                        _log.WriteLine("Nothing was changed. Rename them in the request, or run again with --replace to overwrite them.");
                        return 4;
                    }

                    // 4. Copy of everything that gets replaced, for rollback
                    var replaced = items.Where(i => i.Kind != "tags").SelectMany(i => i.Names).Where(n => blocks.ContainsKey(n) || types.ContainsKey(n)).ToList();
                    if (replaced.Count > 0)
                    {
                        var before = Directory.CreateDirectory(Path.Combine(dir.FullName, "before"));
                        foreach (var n in replaced)
                        {
                            var target = new FileInfo(Path.Combine(before.FullName, Exporter.Safe(n) + ".xml"));
                            if (target.Exists) target.Delete();
                            if (blocks.TryGetValue(n, out var b)) b.Export(target, ExportOptions.WithDefaults);
                            else types[n].Export(target, ExportOptions.WithDefaults);
                            _log.WriteLine($"  saved a copy of {n} to before\\{target.Name}");
                        }
                    }

                    // 5. Import in order
                    foreach (var item in items)
                    {
                        try
                        {
                            switch (item.Kind)
                            {
                                case "tags":
                                    ImportTags(sw, item.File, tags);
                                    break;
                                case "source":
                                    _log.WriteLine($"  source {item.File.Name}");
                                    var existing = sw.ExternalSourceGroup.ExternalSources.Find(item.File.Name);
                                    existing?.Delete();
                                    var source = sw.ExternalSourceGroup.ExternalSources.CreateFromFile(item.File.Name, item.File.FullName);
                                    source.GenerateBlocksFromSource();
                                    break;
                                case "type":
                                    _log.WriteLine($"  type {item.File.Name}");
                                    sw.TypeGroup.Types.Import(item.File, ImportOptions.Override);
                                    break;
                                default:
                                    _log.WriteLine($"  block {item.File.Name}");
                                    sw.BlockGroup.Blocks.Import(item.File, ImportOptions.Override);
                                    break;
                            }
                        }
                        catch (Exception ex)
                        {
                            _log.WriteLine($"    REFUSED: {ex.Message}");
                            fileErrors.Add(new Dictionary<string, string> { ["file"] = item.File.Name, ["error"] = ex.Message });
                        }
                    }

                    // 6. Compile
                    var result = sw.GetService<ICompilable>().Compile();
                    _log.WriteLine($"Compile: {result.State}, {result.ErrorCount} error(s), {result.WarningCount} warning(s)");
                    Collect(result.Messages, messages, "  ");
                    report["compileState"] = result.State.ToString();
                    report["errorCount"] = result.ErrorCount;
                    report["warningCount"] = result.WarningCount;

                    var ok = fileErrors.Count == 0 && result.ErrorCount == 0;
                    if (ok || keepOnError)
                    {
                        project.Save();
                        report["saved"] = true;
                        _log.WriteLine(ok ? "Saved the project." : "Saved the project with errors (--keep-on-error).");
                    }
                    else
                    {
                        report["saved"] = false;
                        _log.WriteLine("Not saved: the project on disk is unchanged. Fix the errors and import again.");
                    }
                    report["fileErrors"] = fileErrors;
                    report["messages"] = messages;
                    var reportFile = Path.Combine(dir.FullName, "import-report.json");
                    File.WriteAllText(reportFile, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
                    _log.WriteLine($"Report: {reportFile}");
                    return ok ? 0 : 3;
                }
                finally
                {
                    project.Close();   // without Save, the changes are dropped
                }
            }
        }

        private void ImportTags(PlcSoftware sw, FileInfo file, Dictionary<string, PlcTag> existing)
        {
            using (var doc = JsonDocument.Parse(File.ReadAllText(file.FullName)))
            {
                foreach (var t in doc.RootElement.GetProperty("tables").EnumerateArray())
                {
                    var tableName = t.GetProperty("name").GetString();
                    var table = sw.TagTableGroup.TagTables.Find(tableName) ?? sw.TagTableGroup.TagTables.Create(tableName);
                    _log.WriteLine($"  tag table {tableName}");
                    foreach (var tag in t.GetProperty("tags").EnumerateArray())
                    {
                        var name = tag.GetProperty("name").GetString();
                        var dataType = tag.GetProperty("dataType").GetString();
                        var address = tag.GetProperty("address").GetString();
                        PlcTag target;
                        if (existing.TryGetValue(name, out var old))
                        {
                            // Only reached for tags the package may change: changed in place, so it stays in its own
                            // table and every block that uses it follows the new address
                            _log.WriteLine($"    {name}: {old.LogicalAddress} -> {address}");
                            if (!string.Equals(old.DataTypeName, dataType, StringComparison.OrdinalIgnoreCase)) old.DataTypeName = dataType;
                            old.LogicalAddress = address;
                            target = old;
                        }
                        else target = table.Tags.Create(name, dataType, address);
                        if (tag.TryGetProperty("comment", out var c) && c.ValueKind == JsonValueKind.String)
                            SetComment(target.Comment, c.GetString());
                    }
                }
            }
        }

        private static void SetComment(MultilingualText text, string value)
        {
            try
            {
                var item = text.Items.FirstOrDefault(i => i.Language.Culture.Name == "en-US") ?? text.Items.FirstOrDefault();
                if (item != null) item.Text = value;
            }
            catch (EngineeringException) { /* comments are nice to have */ }
        }

        private void Collect(CompilerResultMessageComposition list, List<Dictionary<string, string>> into, string indent)
        {
            foreach (CompilerResultMessage m in list)
            {
                if (m.State != CompilerResultState.Success && !string.IsNullOrWhiteSpace(m.Description))
                {
                    _log.WriteLine($"{indent}{m.State}: {m.Path} {m.Description}");
                    into.Add(new Dictionary<string, string> { ["state"] = m.State.ToString(), ["path"] = m.Path, ["description"] = m.Description });
                }
                Collect(m.Messages, into, indent + "  ");
            }
        }

        /// <summary>The package files of one version folder, in number order, with the names each creates.</summary>
        private static List<Item> ReadItems(DirectoryInfo dir)
        {
            var items = new List<Item>();
            foreach (var f in dir.GetFiles().Where(f => FileName.IsMatch(f.Name)).OrderBy(f => f.Name, StringComparer.Ordinal))
            {
                var file = new FileInfo(f.FullName);   // full path: Openness rejects relative ones on .NET Framework
                var ext = file.Extension.ToLowerInvariant();
                var item = new Item { File = file };
                var text = File.ReadAllText(file.FullName);
                if (ext == ".json")
                {
                    item.Kind = "tags";
                    using (var doc = JsonDocument.Parse(text))
                        foreach (var t in doc.RootElement.GetProperty("tables").EnumerateArray())
                            foreach (var tag in t.GetProperty("tags").EnumerateArray())
                                item.Names.Add(tag.GetProperty("name").GetString());
                }
                else if (ext == ".xml")
                {
                    var el = XDocument.Parse(text).Root?.Elements().FirstOrDefault(e => e.Name.LocalName.StartsWith("SW."));
                    if (el == null) throw new InvalidOperationException($"{file.Name}: no SW.Blocks or SW.Types element.");
                    if ((el.Element("AttributeList")?.Element("ProgrammingLanguage")?.Value ?? "").StartsWith("F_"))
                        throw new InvalidOperationException($"{file.Name} is a safety (F) block. The importer does not load F-blocks.");
                    item.Kind = el.Name.LocalName.StartsWith("SW.Types.") ? "type" : "block";
                    item.IsType = item.Kind == "type";
                    item.Names.Add(el.Element("AttributeList")?.Element("Name")?.Value ?? Path.GetFileNameWithoutExtension(file.Name));
                }
                else
                {
                    item.Kind = "source";
                    foreach (Match m in SclHeader.Matches(text))
                    {
                        item.Names.Add(m.Groups[2].Value);
                        if (m.Groups[1].Value.Equals("TYPE", StringComparison.OrdinalIgnoreCase)) item.IsType = true;
                    }
                }
                items.Add(item);
            }
            return items;
        }

        /// <summary>
        /// Names this package already loaded into this project: versions whose import-report.json says they were
        /// saved into the same project file. Importing them again (a re-run, or the next version) is not a clash.
        /// </summary>
        private static HashSet<string> OwnNames(DirectoryInfo packageRoot, FileInfo projectFile)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (packageRoot == null || !packageRoot.Exists) return names;
            foreach (var v in packageRoot.GetDirectories("v*").Where(d => Regex.IsMatch(d.Name, @"^v\d{3}$")))
            {
                var report = new FileInfo(Path.Combine(v.FullName, "import-report.json"));
                if (!report.Exists) continue;
                try
                {
                    using (var doc = JsonDocument.Parse(File.ReadAllText(report.FullName)))
                    {
                        var r = doc.RootElement;
                        if (!(r.TryGetProperty("saved", out var saved) && saved.ValueKind == JsonValueKind.True)) continue;
                        if (!(r.TryGetProperty("project", out var p) && string.Equals(p.GetString(), projectFile.FullName, StringComparison.OrdinalIgnoreCase))) continue;
                    }
                    foreach (var i in ReadItems(v)) foreach (var n in i.Names) names.Add(n);
                }
                catch (Exception) { /* an unreadable old version gives no permission */ }
            }
            return names;
        }

        private static IEnumerable<PlcBlock> AllBlocks(PlcBlockGroup group)
        {
            foreach (PlcBlock b in group.Blocks) yield return b;
            foreach (PlcBlockUserGroup sub in group.Groups) foreach (var b in AllBlocks(sub)) yield return b;
        }

        private static IEnumerable<PlcType> AllTypes(PlcTypeGroup group)
        {
            foreach (PlcType t in group.Types) yield return t;
            foreach (PlcTypeUserGroup sub in group.Groups) foreach (var t in AllTypes(sub)) yield return t;
        }

        private static IEnumerable<PlcTagTable> AllTables(PlcTagTableGroup group)
        {
            foreach (PlcTagTable t in group.TagTables) yield return t;
            foreach (PlcTagTableUserGroup sub in group.Groups) foreach (var t in AllTables(sub)) yield return t;
        }
    }
}
