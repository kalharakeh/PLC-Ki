using System;
using System.IO;
using System.Linq;
using Siemens.Engineering;
using Siemens.Engineering.Compiler;
using Siemens.Engineering.SW;
using Siemens.Engineering.SW.Blocks;
using Siemens.Engineering.SW.ExternalSources;

namespace TiaImporter
{
    /// <summary>
    /// Loads the demo program into an existing TIA project (which must already contain an S7-1500 CPU):
    /// SCL/DB sources from plc-demo\scl, then LAD/GRAPH blocks from plc-demo\lad and plc-demo\graph (SimaticML XML).
    /// Compiles and saves. Never downloads anything to a PLC.
    /// </summary>
    internal sealed class DemoSetup
    {
        private readonly TextWriter _log;
        public DemoSetup(TextWriter log) { _log = log; }

        public int Run(FileInfo projectFile, DirectoryInfo demoDir, string plcName)
        {
            _log.WriteLine("Starting TIA Portal V20 without user interface...");
            using (var tia = new TiaPortal(TiaPortalMode.WithoutUserInterface))
            {
                var project = tia.Projects.Open(projectFile);
                try
                {
                    var plcs = TiaHelpers.FindPlcs(project);
                    var plc = plcName == null ? plcs.FirstOrDefault() : plcs.FirstOrDefault(p => p.Software.Name == plcName);
                    if (plc == null) throw new InvalidOperationException("No PLC found in the project. Add a CPU 1516-3 PN/DP first.");
                    var sw = plc.Software;
                    _log.WriteLine($"Using PLC {sw.Name} ({plc.TypeIdentifier})");

                    // 1. Data blocks and SCL blocks: sources in file-name order (DBs first, then FBs).
                    var sclDir = new DirectoryInfo(Path.Combine(demoDir.FullName, "scl"));
                    foreach (var file in sclDir.GetFiles().Where(f => f.Extension == ".db" || f.Extension == ".scl").OrderBy(f => f.Name))
                    {
                        _log.WriteLine($"  source {file.Name}");
                        var existing = sw.ExternalSourceGroup.ExternalSources.Find(file.Name);
                        existing?.Delete();
                        var source = sw.ExternalSourceGroup.ExternalSources.CreateFromFile(file.Name, file.FullName);
                        source.GenerateBlocksFromSource();
                    }

                    // 2. LAD and GRAPH blocks exported as SimaticML XML.
                    foreach (var sub in new[] { "lad", "graph" })
                    {
                        var dir = new DirectoryInfo(Path.Combine(demoDir.FullName, sub));
                        if (!dir.Exists) continue;
                        foreach (var file in dir.GetFiles("*.xml").OrderBy(f => f.Name))
                        {
                            _log.WriteLine($"  block {file.Name}");
                            sw.BlockGroup.Blocks.Import(file, ImportOptions.Override);
                        }
                    }

                    // 3. Compile and report.
                    var result = sw.GetService<ICompilable>().Compile();
                    _log.WriteLine($"Compile: {result.State}, {result.ErrorCount} error(s), {result.WarningCount} warning(s)");
                    PrintMessages(result.Messages, "  ");
                    project.Save();
                    return result.ErrorCount == 0 ? 0 : 3;
                }
                finally
                {
                    project.Close();
                }
            }
        }

        private void PrintMessages(CompilerResultMessageComposition messages, string indent)
        {
            foreach (CompilerResultMessage m in messages)
            {
                if (m.State != CompilerResultState.Success)
                    _log.WriteLine($"{indent}{m.State}: {m.Path} {m.Description}");
                PrintMessages(m.Messages, indent + "  ");
            }
        }
    }
}
