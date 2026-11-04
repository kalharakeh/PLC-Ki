using System;
using System.IO;
using System.Runtime.CompilerServices;

namespace TiaImporter
{
    internal static class Program
    {
        private const string Usage = @"TiaImporter - TIA Portal V20 Openness helper for the PLC troubleshooter

  TiaImporter export --archive <project.zap20> --out <folder> [--hardware-only]
      Opens the archive without showing TIA, exports every PLC program and the hardware configuration
      (hardware.json) to <folder>, and writes <folder>\manifest.json. The archive itself is never changed.
      --hardware-only skips the blocks: much quicker, for the PLC-Ki Hardware page.

  TiaImporter export --project <project.ap20> --out <folder>
      The same for a project as it is now (attaches to TIA Portal when it has the project open).

  TiaImporter setup-demo --project <project.ap20> --demo <plc-demo folder> [--plc <PLC name>]
      Loads the demo program (SCL sources and XML blocks) into an existing project
      that already contains an S7-1500 CPU, compiles it and saves the project.

  TiaImporter import-blocks --project <project.ap20> --package <package folder> [--plc <PLC name>]
                            [--version v002] [--replace] [--keep-on-error]
      Loads blocks written by PlcKi.Server generate (latest version, or --version) into the named project:
      tag tables, data types, DBs, SCL/LAD/FBD/GRAPH blocks. Compiles and writes import-report.json into the
      version folder. Refuses to overwrite existing blocks, types or tags the package was not asked to change
      (unless --replace) and keeps a copy of each replaced block in before\. Saves the project only when
      everything imported and compiled without errors (unless --keep-on-error).

  TiaImporter download --project <project.ap20> --plcsim-ip <address> [--plc <PLC name>]
      Downloads the PLC software to PLCSIM Advanced, and only there: the PLC's address in the project must be
      the PLCSIM address, and the download must go through a PLCSIM virtual adapter. Never hardware, never a real PLC.

  TiaImporter download --project <project.ap20> --real --allow <address> [--plc <PLC name>]
      Downloads the software to a REAL PLC: only if its address in the project is the --allow address, only after
      you type that address here, only when the CPU is already in STOP; it stays in STOP. Logged in real-downloads.log.

  TiaImporter serve --token <secret> [--port 5090] [--plcsim-ip <address>] [--allow-real <address,...>]
                    [--packages C:\PLCKi\gen] [--projects <folder with your TIA projects>] [--local]
      Lets PLC-Ki on another PC upload generated packages, import them, compile and download to PLCSIM
      (if --plcsim-ip is given), or to the real PLCs listed in --allow-real after you confirm each one in this
      window. Also exports uploaded .zap20 archives for the PLC-Ki import page. Every request needs the token.
      Projects default to Documents\Automation. --local: only PLC-Ki on this same PC may connect (no admin rights needed).

Only download and serve talk to a PLC.";

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
                        if (Arg("--project") is string proj && outDir != null)
                            return new Exporter(Console.Out).RunProject(new FileInfo(Path.GetFullPath(proj)), new DirectoryInfo(Path.GetFullPath(outDir)));
                        if (archive == null || outDir == null) { Console.WriteLine(Usage); return 2; }
                        return new Exporter(Console.Out).Run(new FileInfo(Path.GetFullPath(archive)), new DirectoryInfo(Path.GetFullPath(outDir)),
                            Array.IndexOf(args, "--hardware-only") >= 0);
                    }
                case "setup-demo":
                    {
                        var project = Arg("--project");
                        var demo = Arg("--demo");
                        if (project == null || demo == null) { Console.WriteLine(Usage); return 2; }
                        return new DemoSetup(Console.Out).Run(new FileInfo(Path.GetFullPath(project)), new DirectoryInfo(Path.GetFullPath(demo)), Arg("--plc"));
                    }
                case "import-blocks":
                    {
                        var project = Arg("--project");
                        var package = Arg("--package");
                        if (project == null || package == null) { Console.WriteLine(Usage); return 2; }
                        return new BlockImport(Console.Out).Run(new FileInfo(Path.GetFullPath(project)), new DirectoryInfo(Path.GetFullPath(package)),
                            Arg("--plc"), Arg("--version"), Array.IndexOf(args, "--replace") >= 0, Array.IndexOf(args, "--keep-on-error") >= 0);
                    }
                case "download":
                    {
                        var project = Arg("--project");
                        if (project == null) { Console.WriteLine(Usage); return 2; }
                        if (Array.IndexOf(args, "--real") >= 0)
                        {
                            var allow = Arg("--allow");
                            if (allow == null) { Console.WriteLine("--real needs --allow <the PLC's address>."); return 2; }
                            return new PlcsimDownload(Console.Out).RunReal(new FileInfo(Path.GetFullPath(project)), Arg("--plc"), new[] { allow },
                                q => { Console.Write(q); return Console.ReadLine(); },
                                Path.Combine(@"C:\PLCKi\gen", "real-downloads.log"), "command line");
                        }
                        return new PlcsimDownload(Console.Out).Run(new FileInfo(Path.GetFullPath(project)), Arg("--plc"), Arg("--plcsim-ip"));
                    }
                case "serve":
                    {
                        var projects = Arg("--projects") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Automation");
                        var allowReal = (Arg("--allow-real") ?? "").Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries);
                        return new Serve(int.Parse(Arg("--port") ?? "5090"), Arg("--token"), Arg("--plcsim-ip"),
                            new DirectoryInfo(Path.GetFullPath(Arg("--packages") ?? @"C:\PLCKi\gen")),
                            new DirectoryInfo(Path.GetFullPath(projects)), allowReal, Array.IndexOf(args, "--local") >= 0).Run();
                    }
                default:
                    Console.WriteLine(Usage);
                    return 2;
            }
        }
    }
}
