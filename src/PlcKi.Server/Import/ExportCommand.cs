using System.IO.Compression;
using System.Text.RegularExpressions;
using PlcKi.Core.Import;
using PlcKi.Server.Generate;

namespace PlcKi.Server.Import;

/// <summary>
/// "export": sends a TIA archive (.zap20) to the TIA PC running "TiaImporter serve", lets TIA export every PLC
/// program there, and fetches the export as a zip. The zip is what "import" reads to build the machine model.
/// </summary>
public static class ExportCommand
{
    public const string Usage = """
Usage: PlcKi.Server export --tia <http://tia-pc:5090> --archive <project.zap20> --out <export.zip>

  --tia <url>       the TIA PC running "TiaImporter serve" (http://localhost:5090 when it is this PC)
  --token <secret>  the token serve was started with (or set PLCKI_VM_TOKEN)
  --archive <file>  the TIA Portal archive, on this PC
  --out <file>      where to save the export zip
  --hardware-only yes   only the hardware configuration: --out is then the hardware.json to write (much quicker)
""";

    public static async Task<int> RunAsync(string[] args, VmClient? vm = null, CancellationToken ct = default)
    {
        var o = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i + 1 < args.Length; i += 2)
        {
            if (!args[i].StartsWith("--")) { Console.Error.Write(Usage); return 2; }
            o[args[i][2..]] = args[i + 1];
        }
        if (!o.ContainsKey("tia") || !o.ContainsKey("archive") || !o.ContainsKey("out")) { Console.Error.Write(Usage); return 2; }
        var archive = Path.GetFullPath(o["archive"]);
        if (!File.Exists(archive)) { Console.Error.WriteLine($"Archive not found: {archive}"); return 2; }
        var token = o.GetValueOrDefault("token") ?? Environment.GetEnvironmentVariable("PLCKI_VM_TOKEN");
        if (vm == null && string.IsNullOrWhiteSpace(token)) { Console.Error.WriteLine("Give --token or set PLCKI_VM_TOKEN."); return 2; }
        vm ??= new VmClient(o["tia"], token!);
        vm.Progress ??= text => Console.Write(text);

        // The TIA PC accepts plain names only
        var name = SafeName(Path.GetFileNameWithoutExtension(archive)) + "-" + DateTime.Now.ToString("yyyyMMdd-HHmmss");
        var ext = Path.GetExtension(archive).ToLowerInvariant();
        if (!Regex.IsMatch(ext, @"^\.zap\d+$")) { Console.Error.WriteLine("The archive must be a .zap20 file."); return 2; }

        try
        {
            await vm.StatusAsync(ct);
            Console.WriteLine($"TIA PC reachable at {o["tia"]}.");
            Console.WriteLine($"Sending {Path.GetFileName(archive)} ({new FileInfo(archive).Length / 1024 / 1024.0:0.0} MB)...");
            await vm.UploadArchiveAsync(archive, name + ext, ct);
            Console.WriteLine("TIA opens the archive in the background and exports every PLC program. This takes a few minutes.");
            var hardwareOnly = o.TryGetValue("hardware-only", out var h) && h is "yes" or "true";
            if (hardwareOnly) Console.WriteLine("Only the hardware configuration this time: racks, modules, addresses and channels.");
            var result = await vm.ExportAsync(name + ext, ct, hardwareOnly);
            if (result.ExitCode != 0)
            {
                Console.Error.WriteLine("The export on the TIA PC failed (see the log above).");
                return 1;
            }
            var outFile = Path.GetFullPath(o["out"]);
            Directory.CreateDirectory(Path.GetDirectoryName(outFile)!);
            if (hardwareOnly)
            {
                var tmp = outFile + ".zip.part";
                try
                {
                    await vm.DownloadExportAsync(name, tmp, ct);
                    using (var zip = ZipFile.OpenRead(tmp))
                    {
                        var entry = zip.Entries.Where(e => e.Name.Equals("hardware.json", StringComparison.OrdinalIgnoreCase)).OrderBy(e => e.FullName.Length).FirstOrDefault()
                                    ?? throw new IOException("The TIA PC sent no hardware.json. Update the TIA helper (Update-TIA-helper on the TIA PC).");
                        entry.ExtractToFile(outFile, overwrite: true);
                    }
                }
                finally { try { File.Delete(tmp); } catch { /* temp file */ } }
                var hw = PlcKi.Core.Hardware.HardwareConfig.Load(File.ReadAllText(outFile));
                Console.WriteLine();
                Console.WriteLine($"Hardware saved: {outFile}");
                foreach (var st in hw.Stations)
                    Console.WriteLine($"  {st.Name}: {st.Modules.Count} module(s){(st.Ips.Count > 0 ? ", " + string.Join(", ", st.Ips) : "")}");
                return 0;
            }
            await vm.DownloadExportAsync(name, outFile, ct);
            var export = ProgramExport.Load(outFile);
            Console.WriteLine();
            Console.WriteLine($"Export saved: {outFile}");
            foreach (var plc in export.Plcs)
                Console.WriteLine($"  PLC {plc.Name}{(plc.Cpu != null ? $" ({plc.Cpu})" : "")}: {plc.Blocks.Count} blocks, " +
                                  $"{ProgramExport.Tags(plc).Count(t => t.ModelType != null)} readable tags");
            return 0;
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or TaskCanceledException or IOException)
        {
            Console.Error.WriteLine($"Export failed: {ex.Message}");
            return 2;
        }
    }

    public static string SafeName(string s)
    {
        var n = Regex.Replace(s, @"[^A-Za-z0-9_.-]+", "_").Trim('_', '.');
        if (n.Length == 0) n = "archive";
        return n.Length > 40 ? n[..40] : n;
    }
}
