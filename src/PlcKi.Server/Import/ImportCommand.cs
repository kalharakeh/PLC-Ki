using System.Text.Encodings.Web;
using System.Text.Json;
using Anthropic;
using Anthropic.Exceptions;
using PlcKi.Core.Import;
using PlcKi.Core.Model;
using PlcKi.Server.Ask;

namespace PlcKi.Server.Import;

/// <summary>
/// "import": reads a program export (from tools/TiaImporter) and writes the machine model for one PLC with Claude.
///   PlcKi.Server import --export export.zip --out models/my-machine.json [--plc PLC_1] [--compare models/reference.json]
///                       [--effort low|medium|high|max] [--model claude-opus-5-5] [--digest-only digest.txt]
/// Exit code 0 = model written and checked, 1 = model written but some problems are left, 2 = bad input, 3 = API error.
/// </summary>
public static class ImportCommand
{
    public const string Usage = """
Usage: PlcKi.Server import --export <export.zip or folder> --out <model.json> [options]
  --plc <name>          which PLC in the export (needed when there is more than one)
  --compare <model>     compare the result with another model, e.g. a hand-written one
  --effort <level>      low, medium, high (default) or max: how hard Claude thinks
  --model <id>          Claude model (default claude-opus-5-5)
  --digest-only <file>  only write the program text Claude would read, no API call
""";

    public static async Task<int> RunAsync(string[] args, AnthropicClient? client = null, CancellationToken ct = default)
    {
        var o = Parse(args);
        if (o == null) { Console.Error.Write(Usage); return 2; }

        ProgramExport export;
        try { export = ProgramExport.Load(Path.GetFullPath(o["export"])); }
        catch (Exception ex) { Console.Error.WriteLine($"Cannot read the export '{o["export"]}': {ex.Message}"); return 2; }

        ExportPlc? plc = o.TryGetValue("plc", out var plcName)
            ? export.Plcs.FirstOrDefault(p => p.Name.Equals(plcName, StringComparison.OrdinalIgnoreCase))
            : export.Plcs.Count == 1 ? export.Plcs[0] : null;
        if (plc == null)
        {
            Console.Error.WriteLine(export.Plcs.Count == 0 ? "The export has no PLC."
                : $"Choose the PLC with --plc. In the export: {string.Join(", ", export.Plcs.Select(p => p.Name))}.");
            return 2;
        }
        var tagCount = ProgramExport.Tags(plc).Count(t => t.ModelType != null);
        Console.WriteLine($"Export: project {export.Project}, PLC {plc.Name}" + (plc.Cpu != null ? $" ({plc.Cpu})" : "") +
                          $", {plc.Blocks.Count} blocks, {tagCount} readable tags.");
        foreach (var p in export.Problems.Concat(plc.Problems)) Console.WriteLine("  Export problem: " + p);

        if (o.TryGetValue("digest-only", out var digestFile))
        {
            var digest = ProgramDigest.Build(export, plc);
            File.WriteAllText(digestFile, digest);
            Console.WriteLine($"Wrote the program text ({digest.Length / 1000} kB, about {digest.Length / 4 / 1000}k tokens) to {digestFile}.");
            return 0;
        }

        if (client == null)
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY")))
            {
                Console.Error.WriteLine("ANTHROPIC_API_KEY is not set. The import asks Claude to write the model, so it needs the key.");
                return 2;
            }
            client = new AnthropicClient();
        }

        var builder = new ModelBuilder(client, o.GetValueOrDefault("model", "claude-opus-5-5"),
            AskService.ParseEffort(o.GetValueOrDefault("effort", "high")), Console.WriteLine);
        ModelBuildResult result;
        try
        {
            result = await builder.BuildAsync(export, plc, ct);
        }
        catch (Exception ex) when (ex is AnthropicApiException or AnthropicIOException or InvalidOperationException)
        {
            Console.Error.WriteLine();
            Console.Error.WriteLine(ex switch
            {
                AnthropicUnauthorizedException => "The API key was not accepted. Check ANTHROPIC_API_KEY.",
                AnthropicRateLimitException => "The API is busy or the rate limit was reached. Try again in a minute.",
                AnthropicIOException => "Cannot reach the Claude API: " + ex.Message,
                _ => "The import failed: " + ex.Message,
            });
            return 3;
        }

        if (result.Model == null)
        {
            Console.Error.WriteLine("Claude did not return a usable model:");
            foreach (var p in result.Problems) Console.Error.WriteLine("  - " + p);
            var raw = Path.ChangeExtension(o["out"], ".failed.txt");
            File.WriteAllText(raw, result.Json);
            Console.Error.WriteLine($"The last answer is in {raw}.");
            return 1;
        }

        var outPath = Path.GetFullPath(o["out"]);
        Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
        File.WriteAllText(outPath, ToJson(result.Model));

        var m = result.Model;
        Console.WriteLine();
        Console.WriteLine($"Wrote {o["out"]}: \"{m.Name}\", {m.Tags.Count} tags, {m.Sequences.Count} sequence(s) with " +
                          $"{m.Sequences.Sum(s => s.Steps.Count)} steps, {m.Equipment.Count} devices, {m.Alarms.Count} alarms, {m.Exchange.Count} exchange signals.");
        Console.WriteLine($"Claude: {result.Attempts} round(s), {result.Time.TotalMinutes:0.0} min, {result.InputTokens / 1000}k tokens in " +
                          $"({result.CacheReadTokens / 1000}k from cache), {result.OutputTokens / 1000}k out, about ${Cost(result):0.00}.");

        if (o.TryGetValue("compare", out var refPath))
        {
            Console.WriteLine();
            Console.WriteLine($"Compared with {refPath}:");
            try
            {
                var reference = MachineModel.Load(Path.GetFullPath(refPath));
                foreach (var line in ModelCompare.Compare(m, reference).Lines) Console.WriteLine("  " + line);
            }
            catch (Exception ex) { Console.WriteLine("  Cannot load it: " + ex.Message); }
        }

        if (result.Problems.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine($"{result.Problems.Count} problem(s) are left. PLC-Ki will not start with this model until they are fixed:");
            foreach (var p in result.Problems) Console.WriteLine("  - " + p);
            return 1;
        }
        Console.WriteLine();
        Console.WriteLine("The model fits the program. Review it, then start PLC-Ki with it:");
        Console.WriteLine($"  dotnet run --project src\\PlcKi.Server -- --PlcKi:Model={o["out"]}");
        return 0;
    }

    /// <summary>The model as readable JSON, the same layout as the hand-written models.</summary>
    public static string ToJson(MachineModel model) => JsonSerializer.Serialize(model, new JsonSerializerOptions(MachineModel.JsonOptions)
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    });

    /// <summary>Rough price in USD at claude-opus-5-5 rates ($4 / $20 per million tokens, cache reads at a tenth).</summary>
    private static double Cost(ModelBuildResult r) =>
        ((r.InputTokens - r.CacheReadTokens) * 4.0 + r.CacheReadTokens * 0.4 + r.OutputTokens * 20.0) / 1_000_000;

    private static Dictionary<string, string>? Parse(string[] args)
    {
        var o = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < args.Length; i++)
        {
            if (!args[i].StartsWith("--") || i + 1 >= args.Length) return null;
            o[args[i][2..]] = args[++i];
        }
        if (!o.ContainsKey("export")) return null;
        if (!o.ContainsKey("out") && !o.ContainsKey("digest-only")) return null;
        return o;
    }
}
