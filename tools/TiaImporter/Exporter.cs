using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Siemens.Engineering;
using Siemens.Engineering.SW;
using Siemens.Engineering.SW.Blocks;
using Siemens.Engineering.SW.ExternalSources;
using Siemens.Engineering.SW.Tags;
using Siemens.Engineering.SW.Types;

namespace TiaImporter
{
    /// <summary>
    /// Opens a .zap archive without the TIA user interface and exports every PLC program as
    /// SimaticML XML (all languages), SCL source (SCL blocks only) and a manifest.json.
    /// Read-only towards the archive: it is unpacked into a temporary folder that is deleted afterwards.
    /// </summary>
    internal sealed class Exporter
    {
        private readonly TextWriter _log;
        private readonly List<object> _problems = new List<object>();

        public Exporter(TextWriter log) { _log = log; }

        /// <param name="hardwareOnly">only hardware.json (and the manifest): quick, for the PLC-Ki Hardware page</param>
        public int Run(FileInfo archive, DirectoryInfo outDir, bool hardwareOnly = false)
        {
            if (!archive.Exists) throw new FileNotFoundException("Archive not found", archive.FullName);
            if (outDir.Exists && outDir.EnumerateFileSystemInfos().Any())
                throw new IOException($"Output folder {outDir.FullName} is not empty. Use a new folder.");
            outDir.Create();

            var temp = new DirectoryInfo(Path.Combine(Path.GetTempPath(), "TiaImporter_" + Guid.NewGuid().ToString("N")));
            temp.Create();
            var manifest = new Dictionary<string, object>
            {
                ["archive"] = archive.Name,
                ["exportedAt"] = DateTime.UtcNow.ToString("o"),
                ["tool"] = "TiaImporter 0.1",
            };
            var plcList = new List<object>();

            try
            {
                _log.WriteLine("Starting TIA Portal V20 without user interface...");
                using (var tia = new TiaPortal(TiaPortalMode.WithoutUserInterface))
                {
                    _log.WriteLine($"Retrieving {archive.Name}...");
                    var project = Retrieve(tia, archive, temp);
                    try
                    {
                        manifest["project"] = project.Name;
                        foreach (var plc in TiaHelpers.FindPlcs(project))
                        {
                            _log.WriteLine($"PLC {plc.DeviceName} / {plc.CpuName} ({plc.TypeIdentifier})");
                            var plcDir = outDir.CreateSubdirectory(Safe(plc.Software.Name));
                            var counts = hardwareOnly ? new Dictionary<string, int>() : ExportPlc(plc.Software, plcDir);
                            plcList.Add(new Dictionary<string, object>
                            {
                                ["name"] = plc.Software.Name,
                                ["folder"] = plcDir.Name,
                                ["device"] = plc.DeviceName,
                                ["cpu"] = plc.CpuName,
                                ["typeIdentifier"] = plc.TypeIdentifier,
                                ["ipAddresses"] = plc.IpAddresses,
                                ["counts"] = counts,
                            });
                        }
                        // The racks, modules, addresses and channels, for the PLC-Ki Hardware and PLC pages
                        _log.WriteLine("Exporting the hardware configuration...");
                        try { HardwareExport.Write(project, Path.Combine(outDir.FullName, "hardware.json"), _log); }
                        catch (Exception ex) { Problem("hardware", project.Name, ex); }
                    }
                    finally
                    {
                        project.Close();
                    }
                }
            }
            finally
            {
                try { temp.Delete(true); } catch { /* TIA may still hold a lock; temp is cleaned by Windows later */ }
            }

            manifest["plcs"] = plcList;
            manifest["problems"] = _problems;
            File.WriteAllText(Path.Combine(outDir.FullName, "manifest.json"),
                JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));
            _log.WriteLine($"Done. {plcList.Count} PLC(s), {_problems.Count} problem(s). See manifest.json.");
            return 0;
        }

        private static Project Retrieve(TiaPortal tia, FileInfo archive, DirectoryInfo target)
        {
            try
            {
                return tia.Projects.Retrieve(archive, target);
            }
            catch (EngineeringException)
            {
                // Archive from an older TIA version: upgrade a copy while retrieving.
                return tia.Projects.RetrieveWithUpgrade(archive, target);
            }
        }

        private Dictionary<string, int> ExportPlc(PlcSoftware sw, DirectoryInfo dir)
        {
            var counts = new Dictionary<string, int> { ["blocks"] = 0, ["types"] = 0, ["tagTables"] = 0, ["sclSources"] = 0 };
            var sclBlocks = new List<PlcBlock>();

            // Progress lines "blocks 12/52 (23%)": the PLC-Ki import page turns them into a percentage
            _blocksTotal = CountBlocks(sw.BlockGroup);
            _blocksDone = 0;
            _log.WriteLine($"Exporting {_blocksTotal} blocks...");
            ExportBlockGroup(sw.BlockGroup, dir.CreateSubdirectory("Blocks"), counts, sclBlocks);
            _log.WriteLine("Exporting data types and tag tables...");
            ExportTypeGroup(sw.TypeGroup, dir.CreateSubdirectory("Types"), counts);
            ExportTagGroup(sw.TagTableGroup, dir.CreateSubdirectory("Tags"), counts);

            // Readable SCL text in addition to the XML, one file per SCL block.
            if (sclBlocks.Count > 0) _log.WriteLine($"Writing the SCL source of {sclBlocks.Count} blocks...");
            var sclDir = dir.CreateSubdirectory("SclSource");
            foreach (var block in sclBlocks)
            {
                try
                {
                    sw.ExternalSourceGroup.GenerateSource(new[] { block },
                        new FileInfo(Path.Combine(sclDir.FullName, Safe(block.Name) + ".scl")),
                        GenerateOptions.None);
                    counts["sclSources"]++;
                }
                catch (Exception ex) { Problem("scl-source", block.Name, ex); }
            }
            return counts;
        }

        private int _blocksTotal, _blocksDone;

        private static int CountBlocks(PlcBlockGroup group)
        {
            var n = group.Blocks.Count;
            foreach (PlcBlockUserGroup sub in group.Groups) n += CountBlocks(sub);
            return n;
        }

        /// <summary>One more block handled; about every 5 % a progress line.</summary>
        private void BlockDone()
        {
            _blocksDone++;
            if (_blocksTotal <= 0) return;
            var step = Math.Max(1, _blocksTotal / 20);
            if (_blocksDone == _blocksTotal || _blocksDone % step == 0)
                _log.WriteLine($"  blocks {_blocksDone}/{_blocksTotal} ({_blocksDone * 100 / _blocksTotal}%)");
        }

        private void ExportBlockGroup(PlcBlockGroup group, DirectoryInfo dir, Dictionary<string, int> counts, List<PlcBlock> sclBlocks)
        {
            foreach (PlcBlock block in group.Blocks)
            {
                BlockDone();
                if (block.IsKnowHowProtected)
                {
                    _problems.Add(new { kind = "know-how-protected", name = block.Name, detail = "Only the interface can be explained." });
                    continue;
                }
                try
                {
                    block.Export(new FileInfo(Path.Combine(dir.FullName, Safe(block.Name) + ".xml")), ExportOptions.WithDefaults);
                    counts["blocks"]++;
                    if (block.ProgrammingLanguage == ProgrammingLanguage.SCL) sclBlocks.Add(block);
                }
                catch (Exception ex) { Problem("block", block.Name, ex); }
            }
            foreach (PlcBlockUserGroup sub in group.Groups)
                ExportBlockGroup(sub, dir.CreateSubdirectory(Safe(sub.Name)), counts, sclBlocks);
        }

        private void ExportTypeGroup(PlcTypeGroup group, DirectoryInfo dir, Dictionary<string, int> counts)
        {
            foreach (PlcType type in group.Types)
            {
                try
                {
                    type.Export(new FileInfo(Path.Combine(dir.FullName, Safe(type.Name) + ".xml")), ExportOptions.WithDefaults);
                    counts["types"]++;
                }
                catch (Exception ex) { Problem("type", type.Name, ex); }
            }
            foreach (PlcTypeUserGroup sub in group.Groups)
                ExportTypeGroup(sub, dir.CreateSubdirectory(Safe(sub.Name)), counts);
        }

        private void ExportTagGroup(PlcTagTableGroup group, DirectoryInfo dir, Dictionary<string, int> counts)
        {
            foreach (PlcTagTable table in group.TagTables)
            {
                try
                {
                    table.Export(new FileInfo(Path.Combine(dir.FullName, Safe(table.Name) + ".xml")), ExportOptions.WithDefaults);
                    counts["tagTables"]++;
                }
                catch (Exception ex) { Problem("tag-table", table.Name, ex); }
            }
            foreach (PlcTagTableUserGroup sub in group.Groups)
                ExportTagGroup(sub, dir.CreateSubdirectory(Safe(sub.Name)), counts);
        }

        private void Problem(string kind, string name, Exception ex)
        {
            _problems.Add(new { kind, name, detail = ex.Message });
            _log.WriteLine($"  ! {kind} {name}: {ex.Message}");
        }

        internal static string Safe(string name)
        {
            foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
            return name;
        }
    }
}
