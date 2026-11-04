using System.Text.Json;
using Anthropic;
using Anthropic.Exceptions;
using PlcKi.Core.Generate;
using PlcKi.Core.Import;
using PlcKi.Server.Ask;

namespace PlcKi.Server.Generate;

/// <summary>
/// "generate": writes PLC blocks (FB, FC, DB, PLC data types, tag tables in SCL, LAD, FBD or GRAPH) from a plain request
/// with Claude, into a package folder with one subfolder per version. tools/TiaImporter import-blocks loads a version
/// into a TIA project and compiles it; "generate --fix" sends TIA's report back to Claude for the next version.
/// </summary>
public static class GenerateCommand
{
    public const string Usage = """
Usage: PlcKi.Server generate --out <package folder> --request "<what you need>" [options]
       PlcKi.Server generate --out <package folder> --fix

  --request <text>       what to write or change, in plain words
  --request-file <file>  the same, read from a text file (for long requests)
  --context <export>     TiaImporter export (folder or zip) of the PLC program the blocks go into;
                         Claude reuses its tags, types and blocks and avoids its names
  --plc <name>           which PLC in the export (needed when there is more than one)
  --edit <names>         existing blocks to change, comma-separated (needs --context), e.g. FB_Valve,FB_Motor
  --standard <file>      your block standard (naming, interfaces, languages); default knowledge/block-standard.md
  --fix                  read import-report.json of the latest version and let Claude fix what TIA reported
  --scl-only             leave the LAD/GRAPH reference files out of the prompt (cheaper for SCL-only work)
  --effort <level>       low, medium, high (default) or max
  --model <id>           Claude model (default claude-opus-5-5)

A second request on the same --out folder changes the latest version. Every round is saved as a new version folder
(v001, v002, ...) and listed in generated.json, so you can compare versions or import an older one again.
Generated code must be reviewed by an engineer before it goes to a real machine.
""";

    public static async Task<int> RunAsync(string[] args, AnthropicClient? client = null, CancellationToken ct = default)
    {
        var o = Parse(args);
        if (o == null) { Console.Error.Write(Usage); return 2; }
        var outDir = Path.GetFullPath(o["out"]);
        var fix = o.ContainsKey("fix");

        string request = o.GetValueOrDefault("request", "");
        if (o.TryGetValue("request-file", out var rf))
        {
            try { request = File.ReadAllText(Path.GetFullPath(rf)); }
            catch (Exception ex) { Console.Error.WriteLine($"Cannot read {rf}: {ex.Message}"); return 2; }
        }
        if (!fix && string.IsNullOrWhiteSpace(request)) { Console.Error.WriteLine("Say what you need with --request \"...\"."); Console.Error.Write(Usage); return 2; }

        var history = Directory.Exists(outDir) ? PackageHistory.Load(outDir) : new PackageHistory();
        var latest = history.Latest;
        BlockPackage? previous = latest != null ? BlockPackage.Load(Path.Combine(outDir, latest.Folder)) : null;

        // TIA's report on the latest version
        var tiaProblems = new List<string>();
        if (fix)
        {
            if (latest == null) { Console.Error.WriteLine($"No package in {outDir} yet. Make one with --request first."); return 2; }
            var reportPath = Path.Combine(outDir, latest.Folder, BlockPackage.ReportFile);
            if (!File.Exists(reportPath))
            {
                Console.Error.WriteLine($"No {BlockPackage.ReportFile} in {latest.Folder}. Import it first:");
                Console.Error.WriteLine($"  TiaImporter import-blocks --project <your project .ap20> --package {outDir}");
                return 2;
            }
            var report = ImportReport.Load(reportPath);
            tiaProblems = report.AsProblems();
            if (report.Clean && tiaProblems.Count == 0 && request.Length == 0)
            {
                Console.WriteLine($"TIA imported and compiled {latest.Folder} without errors or warnings. Nothing to fix.");
                return 0;
            }
            Console.WriteLine($"TIA report for {latest.Folder}: {report.FileErrors.Count} refused file(s), {report.ErrorCount} error(s), {report.WarningCount} warning(s).");
        }

        // The PLC program the blocks go into
        string? contextPath = o.GetValueOrDefault("context") ?? history.Context;
        string? plcName = o.GetValueOrDefault("plc") ?? history.Plc;
        string? digest = null;
        HashSet<string>? existingNames = null;
        var existing = new List<(string Name, string Extension, string Text)>();
        var editTags = new List<string>();
        if (contextPath != null)
        {
            contextPath = Path.GetFullPath(contextPath);
            ProgramExport export;
            try { export = ProgramExport.Load(contextPath); }
            catch (Exception ex) { Console.Error.WriteLine($"Cannot read the export '{contextPath}': {ex.Message}"); return 2; }
            var plc = plcName != null ? export.Plcs.FirstOrDefault(p => p.Name.Equals(plcName, StringComparison.OrdinalIgnoreCase))
                : export.Plcs.Count == 1 ? export.Plcs[0] : null;
            if (plc == null) { Console.Error.WriteLine($"Choose the PLC with --plc. In the export: {string.Join(", ", export.Plcs.Select(p => p.Name))}."); return 2; }
            plcName = plc.Name;
            digest = ProgramDigest.Build(export, plc);
            existingNames = new HashSet<string>(plc.Blocks.Select(b => b.Name).Concat(plc.Types.Select(t => t.Name)).Concat(plc.PlcTags.Select(t => t.Name)), StringComparer.OrdinalIgnoreCase);
            Console.WriteLine($"Program context: {export.Project}, PLC {plc.Name}, {plc.Blocks.Count} blocks, {plc.PlcTags.Count} PLC tags.");

            if (o.TryGetValue("edit", out var editList))
            {
                var names = editList.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
                // PLC tags in the list: their address, type or comment may change (standard tags only)
                var tagEdits = plc.PlcTags.Where(t => names.Contains(t.Name, StringComparer.OrdinalIgnoreCase)).ToList();
                var safety = plc.SafetyTags();
                var refused = tagEdits.Where(t => safety.Contains(t.Name)).Select(t => t.Name).ToList();
                if (refused.Count > 0)
                {
                    Console.Error.WriteLine($"{string.Join(", ", refused)}: used by the safety program. PLC-Ki does not change safety tags; change them in TIA Portal (safety password needed).");
                    return 2;
                }
                names = names.Where(n => !tagEdits.Any(t => t.Name.Equals(n, StringComparison.OrdinalIgnoreCase))).ToList();
                existing = ExportSources.Read(contextPath, plc.Name, names);
                if (tagEdits.Count > 0)
                {
                    existing.Add(("PLC tags to change", ".json", JsonSerializer.Serialize(new
                    {
                        tables = tagEdits.GroupBy(t => t.Table).Select(g => new
                        {
                            name = g.Key,
                            tags = g.Select(t => new { name = t.Name, dataType = t.Datatype, address = t.Address, comment = t.Comment }),
                        }),
                    }, new JsonSerializerOptions { WriteIndented = true })));
                    editTags = tagEdits.Select(t => t.Name).ToList();
                    Console.WriteLine($"Changing tags: {string.Join(", ", tagEdits.Select(t => $"{t.Name} ({t.Address})"))}");
                }
                var missing = names.Where(n => existing.All(e => !e.Name.Equals(n, StringComparison.OrdinalIgnoreCase))).ToList();
                if (missing.Count > 0) { Console.Error.WriteLine($"Not found in the export: {string.Join(", ", missing)}."); return 2; }
                Console.WriteLine($"Editing: {string.Join(", ", existing.Select(e => e.Name + (e.Extension == ".scl" ? " (SCL)" : " (XML)")))}");
            }
        }
        else if (o.ContainsKey("edit"))
        {
            Console.Error.WriteLine("--edit needs --context <export> to read the current blocks from. Make one with TiaImporter export.");
            return 2;
        }

        // Blocks this package may overwrite in the project: the ones asked to edit, and its own from earlier versions
        var replaces = new HashSet<string>(latest?.Replaces ?? new(), StringComparer.OrdinalIgnoreCase);
        foreach (var e in existing.Where(e => e.Extension != ".json")) replaces.Add(e.Name);
        replaces.UnionWith(editTags);
        foreach (var v in history.Versions)
        {
            var dir = Path.Combine(outDir, v.Folder);
            if (Directory.Exists(dir)) replaces.UnionWith(PackageCheck.Names(BlockPackage.Load(dir)));
        }

        string standard;
        if (o.TryGetValue("standard", out var stdPath))
        {
            try { standard = File.ReadAllText(Path.GetFullPath(stdPath)); }
            catch (Exception ex) { Console.Error.WriteLine($"Cannot read the standard '{stdPath}': {ex.Message}"); return 2; }
        }
        else standard = GeneratePrompt.DefaultStandard;

        if (client == null)
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY")))
            {
                Console.Error.WriteLine("ANTHROPIC_API_KEY is not set. Claude writes the blocks, so the generator needs the key.");
                return 2;
            }
            client = new AnthropicClient();
        }

        var model = o.GetValueOrDefault("model", "claude-opus-5-5");
        var generator = new BlockGenerator(client, model, AskService.ParseEffort(o.GetValueOrDefault("effort", "high")), Console.WriteLine);
        var input = new GenerateInput
        {
            Request = request,
            Standard = standard,
            ProgramContext = digest,
            Existing = existing,
            EditTags = editTags,
            Previous = previous,
            TiaProblems = tiaProblems,
            ExistingNames = existingNames,
            Replaces = replaces,
            XmlReferences = !o.ContainsKey("scl-only"),
        };

        GenerateResult result;
        try { result = await generator.GenerateAsync(input, ct); }
        catch (Exception ex) when (ex is AnthropicApiException or AnthropicIOException or InvalidOperationException)
        {
            Console.Error.WriteLine(ex switch
            {
                AnthropicUnauthorizedException => "The API key was not accepted. Check ANTHROPIC_API_KEY.",
                AnthropicRateLimitException => "The API is busy or the rate limit was reached. Try again in a minute.",
                AnthropicIOException => "Cannot reach the Claude API: " + ex.Message,
                _ => "Generating failed: " + ex.Message,
            });
            return 3;
        }

        if (result.Package == null)
        {
            Directory.CreateDirectory(outDir);
            var raw = Path.Combine(outDir, "failed-answer.txt");
            File.WriteAllText(raw, result.LastAnswer);
            Console.Error.WriteLine($"Claude did not return any files. The answer is in {raw}.");
            return 1;
        }

        // An edit of PLC blocks (no earlier version) only holds the changed blocks; a change or fix is the whole package
        Directory.CreateDirectory(outDir);
        history.Context = contextPath;
        history.Plc = plcName;
        var version = history.Add(outDir, result.Package, new PackageVersion
        {
            Time = DateTimeOffset.Now,
            Kind = fix ? "fix" : previous != null ? "change" : existing.Count > 0 ? "edit" : "new",
            Request = fix && request.Length == 0 ? "Fix what TIA reported for " + latest!.Folder : request.Trim(),
            Model = model,
            Replaces = replaces.OrderBy(r => r).ToList(),   // its own earlier blocks too: a later version replaces what an earlier one imported
            Problems = result.Problems,
        }, previous);

        var folder = Path.Combine(outDir, version.Folder);
        Console.WriteLine();
        Console.WriteLine($"Wrote {version.Folder} ({version.Kind}) to {folder}:");
        foreach (var b in PackageCheck.Blocks(result.Package))
            Console.WriteLine($"  {b.File,-32} {b.Kind,-4} {b.Name}" + (b.Language != null ? $" ({b.Language})" : "") + (b.InstanceOf != null ? $" of {b.InstanceOf}" : ""));
        foreach (var f in result.Package.Files.Where(f => f.Extension == ".json"))
            Console.WriteLine($"  {f.Name,-32} tag tables");
        Console.WriteLine(previous != null ? $"What Ki changed against {latest!.Folder}:" : "What Ki made:");
        foreach (var line in PackageSummary.Compare(previous, result.Package).Text) Console.WriteLine("  - " + line);
        if (version.Replaces.Count > 0)
            Console.WriteLine($"Replaces in the project: {string.Join(", ", version.Replaces)} (the importer saves a copy of each first).");
        Console.WriteLine($"Ki (Claude): {result.Attempts} round(s), {result.Time.TotalMinutes:0.0} min, {result.InputTokens / 1000}k tokens in " +
                          $"({result.CacheReadTokens / 1000}k from cache), {result.OutputTokens / 1000}k out, about ${Cost(result):0.00}.");
        Console.WriteLine($"Review notes: {Path.Combine(folder, BlockPackage.ReviewFile)}");

        if (result.Problems.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine($"{result.Problems.Count} problem(s) are left after the offline check:");
            foreach (var p in result.Problems) Console.WriteLine("  - " + p);
        }
        if (!o.ContainsKey("from-develop"))
        {
            if (existingNames == null)
            {
                Console.WriteLine();
                Console.WriteLine("Note: without --context the names were not checked against your PLC program. If a block, type or tag");
                Console.WriteLine("already exists there, import-blocks refuses it; give the export with --context to avoid that.");
            }
            Console.WriteLine();
            Console.WriteLine("Next, on the TIA PC (works on the project you name, nothing is downloaded to a PLC):");
            Console.WriteLine($"  TiaImporter import-blocks --project <your project .ap20> --package <this folder>{(plcName != null ? " --plc " + plcName : "")}");
            Console.WriteLine("If TIA reports errors, bring the folder back and run:");
            Console.WriteLine($"  dotnet run --project src\\PlcKi.Server -- generate --out {o["out"]} --fix");
        }
        return result.Problems.Count == 0 ? 0 : 1;
    }

    /// <summary>Rough price in USD at claude-opus-5-5 rates ($4 / $20 per million tokens, cache reads at a tenth).</summary>
    private static double Cost(GenerateResult r) =>
        ((r.InputTokens - r.CacheReadTokens) * 4.0 + r.CacheReadTokens * 0.4 + r.OutputTokens * 20.0) / 1_000_000;

    private static readonly HashSet<string> Flags = new(StringComparer.OrdinalIgnoreCase) { "fix", "scl-only", "from-develop" };

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
        return o.ContainsKey("out") ? o : null;
    }
}
