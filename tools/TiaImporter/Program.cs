using System;
using System.IO;
using System.Runtime.CompilerServices;

namespace TiaImporter
{
    internal static class Program
    {
        private const string Usage = @"TiaImporter - TIA Portal V20 Openness helper for the PLC troubleshooter

  TiaImporter export --archive <project.zap20> --out <folder>
      Opens the archive without showing TIA, exports every PLC program to <folder>,
      and writes <folder>\manifest.json. The archive itself is never changed.

  TiaImporter setup-demo --project <project.ap20> --demo <plc-demo folder> [--plc <PLC name>]
      Loads the demo program (SCL sources and XML blocks) into an existing project
      that already contains an S7-1500 CPU, compiles it and saves the project.

Nothing in this tool connects to a PLC or downloads to one.";

        private static int Main(string[] args)
        {
            OpennessResolver.Register();
            try
            {
                return Run(args);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("ERROR: " + ex.Message);
                return 1;
            }
        }

        // Kept separate so Openness types are only loaded after the resolver is registered.
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static int Run(string[] args)
        {
            if (args.Length == 0) { Console.WriteLine(Usage); return 2; }

            string Arg(string name)
            {
                var i = Array.IndexOf(args, name);
                return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
            }

            switch (args[0])
            {
                case "export":
                    {
                        var archive = Arg("--archive");
                        var outDir = Arg("--out");
                        if (archive == null || outDir == null) { Console.WriteLine(Usage); return 2; }
                        return new Exporter(Console.Out).Run(new FileInfo(Path.GetFullPath(archive)), new DirectoryInfo(Path.GetFullPath(outDir)));
                    }
                case "setup-demo":
                    {
                        var project = Arg("--project");
                        var demo = Arg("--demo");
                        if (project == null || demo == null) { Console.WriteLine(Usage); return 2; }
                        return new DemoSetup(Console.Out).Run(new FileInfo(Path.GetFullPath(project)), new DirectoryInfo(Path.GetFullPath(demo)), Arg("--plc"));
                    }
                default:
                    Console.WriteLine(Usage);
                    return 2;
            }
        }
    }
}
