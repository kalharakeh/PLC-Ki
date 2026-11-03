using Anthropic;
using PlcKi.Core.Generate;

namespace PlcKi.Server.Generate;

/// <summary>
/// "develop": the whole loop in one command, with the TIA PC running "TiaImporter serve".
///   request -> Claude writes the blocks -> upload -> import + compile in the named project
///   -> errors back to Claude -> ... until it compiles -> download to PLCSIM -> watch CPU state and alarms
///   -> runtime problems back to Claude -> ...
/// Every round is a version in the package folder, as with "generate".
/// </summary>
public static class DevelopCommand
{
    public const string Usage = """
Usage: PlcKi.Server develop --vm <http://tia-pc:5090> --project <project .ap20 on the TIA PC> --out <package folder>
                            [--request "<what you need>"] [options]

  --vm <url>            the TIA PC running "TiaImporter serve" (e.g. http://192.168.10.3:5090)
  --token <secret>      the token serve was started with (or set PLCKI_VM_TOKEN)
  --project <path>      the TIA project to change, as a path on the TIA PC
  --out <folder>        package folder on this PC (versions v001, v002, ...)
  --request <text>      what to write or change; leave out to send the latest version as it is
  --plc <name>          which PLC in the project
  --rounds <n>          fix rounds after the first try (default 3)
  --download            after a clean compile, download to PLCSIM (serve must have --plcsim-ip)
  --plc-address <ip>    PLCSIM address to watch after the download (read-only), e.g. 192.168.10.10
  --watch <seconds>     how long to watch (default 30)
  and the generate options: --context --edit --standard --scl-only --effort --model --request-file
""";

    private static readonly string[] Passthrough = { "context", "edit", "standard", "effort", "model", "request-file" };

    public static async Task<int> RunAsync(string[] args, AnthropicClient? client = null, VmClient? vm = null,
        Func<string, PlcWatch>? watchFactory = null, CancellationToken ct = default)
    {
        var o = Parse(args);
        if (o == null) { Console.Error.Write(Usage); return 2; }
        var outDir = Path.GetFullPath(o["out"]);
        var package = Path.GetFileName(outDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        var token = o.GetValueOrDefault("token") ?? Environment.GetEnvironmentVariable("PLCKI_VM_TOKEN");
        if (vm == null && string.IsNullOrWhiteSpace(token)) { Console.Error.WriteLine("Give --token (the one TiaImporter serve was started with) or set PLCKI_VM_TOKEN."); return 2; }
        vm ??= new VmClient(o["vm"], token!);
        var rounds = int.TryParse(o.GetValueOrDefault("rounds"), out var r) ? r : 3;
        var watchSeconds = int.TryParse(o.GetValueOrDefault("watch"), out var w) ? w : 30;
        o.TryGetValue("plc-address", out var plcAddress);
        o.TryGetValue("plc", out var plc);

        try { await vm.StatusAsync(ct); Console.WriteLine($"TIA PC reachable at {o["vm"]}."); }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or TaskCanceledException)
        {
            Console.Error.WriteLine($"Cannot reach TiaImporter serve at {o["vm"]}: {ex.Message}");
            return 2;
        }

        string[] GenArgs(params string[] first)
        {
            var list = new List<string>(first) { "--out", outDir };
            foreach (var key in Passthrough)
                if (o.TryGetValue(key, out var v) && !(key == "request-file" && first.Contains("--fix"))) { list.Add("--" + key); list.Add(v); }
            if (o.ContainsKey("scl-only")) list.Add("--scl-only");
            if (plc != null) { list.Add("--plc"); list.Add(plc); }
            return list.ToArray();
        }

        // 1. New version from the request (or use the latest as it is)
        if (o.ContainsKey("request") || o.ContainsKey("request-file"))
        {
            var code = await GenerateCommand.RunAsync(o.ContainsKey("request") ? GenArgs("--request", o["request"]) : GenArgs(), client, ct);
            if (code >= 2) return code;
        }
        else if (PackageHistory.Load(outDir).Latest == null) { Console.Error.WriteLine($"No package in {outDir}. Give --request."); return 2; }

        for (int round = 0; ; round++)
        {
            var version = PackageHistory.Load(outDir).Latest!;
            var reportPath = Path.Combine(outDir, version.Folder, BlockPackage.ReportFile);
            Console.WriteLine();
            Console.WriteLine($"=== {version.Folder}: import and compile on the TIA PC ===");
            await vm.UploadAsync(outDir, package, ct);
            var import = await vm.ImportAsync(package, o["project"], plc, version.Folder, ct);
            Console.Write(Indent(import.Log));
            var report = import.ReportJson != null ? System.Text.Json.JsonSerializer.Deserialize<ImportReport>(import.ReportJson) ?? new() : new ImportReport();
            if (import.ReportJson == null)
                report.FileErrors.Add(new ReportFileError { File = "(import)", Error = LastLine(import.Log) });
            report.Save(reportPath);

            if (report.Compiled && o.ContainsKey("download"))
            {
                PlcSnapshot? before = null;
                PlcWatch? watch = plcAddress != null ? (watchFactory ?? (a => new PlcWatch(a, Console.WriteLine)))(plcAddress) : null;
                if (watch != null)
                {
                    try { before = await watch.SnapshotAsync(ct); Console.WriteLine($"PLC before download: {before.State}, {before.Alarms.Count} active alarm(s)."); }
                    catch (Exception ex) { Console.WriteLine($"Cannot read the PLC at {plcAddress} ({ex.Message}); the download goes ahead without watching."); watch = null; }
                }
                Console.WriteLine($"=== {version.Folder}: download to PLCSIM ===");
                var dl = await vm.DownloadAsync(o["project"], plc, ct);
                Console.Write(Indent(dl.Log));
                report.Download = dl.ExitCode == 0 ? "OK" : LastLine(dl.Log);
                if (dl.ExitCode == 0 && watch != null)
                {
                    Console.WriteLine($"=== watching the PLC for {watchSeconds} s (read-only) ===");
                    await Task.Delay(TimeSpan.FromSeconds(Math.Min(5, watchSeconds)), ct);
                    try { report.Runtime = await watch.WatchAsync(before, TimeSpan.FromSeconds(watchSeconds), ct); }
                    catch (Exception ex) { report.Runtime = new() { $"Lost the connection to the PLC: {ex.Message}" }; }
                }
                report.Save(reportPath);
            }

            var done = report.Compiled && (!o.ContainsKey("download") || report.Download == "OK") && report.Runtime.Count == 0;
            if (done)
            {
                Console.WriteLine();
                Console.WriteLine($"{version.Folder} imported and compiled" + (o.ContainsKey("download") ? ", downloaded to PLCSIM" : "") +
                                  (plcAddress != null && o.ContainsKey("download") ? " and ran without new alarms" : "") + ".");
                Console.WriteLine($"Review notes: {Path.Combine(outDir, version.Folder, BlockPackage.ReviewFile)}");
                return 0;
            }
            var problems = report.AsProblems(withWarnings: false);
            if (round >= rounds)
            {
                Console.WriteLine();
                Console.WriteLine($"Still {problems.Count} problem(s) after {rounds} fix round(s):");
                foreach (var p in problems) Console.WriteLine("  - " + p);
                Console.WriteLine("Look at them in TIA, or run develop again with a clearer --request.");
                return 1;
            }
            Console.WriteLine();
            Console.WriteLine($"=== fix round {round + 1} of {rounds}: {problems.Count} problem(s) go back to Claude ===");
            var fix = await GenerateCommand.RunAsync(GenArgs("--fix"), client, ct);
            if (fix >= 2) return fix;
        }
    }

    private static string Indent(string log) => string.Concat(log.Split('\n').Where(l => l.Length > 0).Select(l => "  | " + l.TrimEnd('\r') + "\n"));

    private static string LastLine(string log) => log.Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.Length > 0) ?? "no answer";

    private static readonly HashSet<string> Flags = new(StringComparer.OrdinalIgnoreCase) { "download", "scl-only" };

    private static Dictionary<string, string>? Parse(string[] args)
    {
        var o = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < args.Length; i++)
        {
            if (!args[i].StartsWith("--")) return null;
            var key = args[i][2..];
            if (Flags.Contains(key)) { o[key] = "true"; continue; }
            if (i + 1 >= args.Length) return null;
            o[key] = args[++i];
        }
        return o.ContainsKey("vm") && o.ContainsKey("project") && o.ContainsKey("out") ? o : null;
    }
}
