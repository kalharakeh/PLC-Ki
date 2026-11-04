using System.Diagnostics;
using System.Text;
using Anthropic;
using Anthropic.Models.Beta.Messages;
using PlcKi.Core.Generate;

namespace PlcKi.Server.Generate;

/// <summary>What the generator works from. Only <see cref="Request"/> or <see cref="TiaProblems"/> is needed.</summary>
public sealed class GenerateInput
{
    public string Request { get; init; } = "";
    public string Standard { get; init; } = "";
    /// <summary>Text of the existing PLC program (ProgramDigest), so new code fits it. Optional.</summary>
    public string? ProgramContext { get; init; }
    /// <summary>Existing blocks to edit, as (name, extension, source).</summary>
    public List<(string Name, string Extension, string Text)> Existing { get; init; } = new();
    /// <summary>Existing PLC tags the user asked to change (address, type, comment).</summary>
    public List<string> EditTags { get; init; } = new();
    /// <summary>The latest package version, for a change or a fix.</summary>
    public BlockPackage? Previous { get; init; }
    /// <summary>Import refusals and compile messages from TIA for <see cref="Previous"/>.</summary>
    public List<string> TiaProblems { get; init; } = new();
    /// <summary>Names already in the PLC program (blocks, types, tags), when known.</summary>
    public ISet<string>? ExistingNames { get; init; }
    public ISet<string>? Replaces { get; init; }
    /// <summary>Include the LAD/GRAPH SimaticML references (about 40k tokens, cached). Off for SCL-only requests.</summary>
    public bool XmlReferences { get; init; } = true;
}

public sealed class GenerateResult
{
    public BlockPackage? Package { get; set; }
    public List<string> Problems { get; set; } = new();
    public string LastAnswer { get; set; } = "";
    public int Attempts { get; set; }
    public long InputTokens { get; set; }
    public long OutputTokens { get; set; }
    public long CacheReadTokens { get; set; }
    public TimeSpan Time { get; set; }
}

/// <summary>
/// Writes a block package with Claude: the request (or TIA's error report) goes in with the block standard, proven
/// reference files and the program context; the answer is checked offline and sent back for correction a few times
/// if needed. TIA's compile is the last check, done by tools/TiaImporter import-blocks.
/// </summary>
public sealed class BlockGenerator
{
    private const string FallbackBeta = "server-side-fallback-2026-07-01";
    private readonly AnthropicClient _client;
    private readonly string _model;
    private readonly Effort _effort;
    private readonly Action<string> _log;

    public int MaxRepairs { get; init; } = 3;
    public int MaxTokens { get; init; } = 128000;   // edits of big real-plant blocks plus thinking need room

    public BlockGenerator(AnthropicClient client, string model, Effort effort, Action<string> log)
    {
        _client = client;
        _model = model;
        _effort = effort;
        _log = log;
    }

    public async Task<GenerateResult> GenerateAsync(GenerateInput input, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var result = new GenerateResult();
        string? draft = null;
        List<string> problems = new();
        for (int attempt = 1; attempt <= 1 + MaxRepairs; attempt++)
        {
            result.Attempts = attempt;
            _log(attempt == 1 ? "Ki is writing the blocks..." : $"Correcting {problems.Count} problem(s), round {attempt - 1}...");
            var text = await AskAsync(input, draft, problems, result, ct);
            result.LastAnswer = text;
            var package = BlockPackage.Parse(text);
            problems = PackageCheck.Check(package, input.ExistingNames, input.Replaces, input.Existing.Where(e => e.Extension != ".json").Select(e => e.Name).Concat(input.EditTags));
            if (package.Files.Count > 0)
            {
                result.Package = package;
                result.Problems = problems;
            }
            draft = package.Files.Count > 0 ? package.ToAnswerText() : (text.Length > 4000 ? text[..4000] : text);
            if (problems.Count == 0) break;
            _log($"  {problems.Count} problem(s) found by the offline check.");
        }
        result.Time = sw.Elapsed;
        return result;
    }

    private static BetaCacheControlEphemeral HourCache() => new() { Ttl = Ttl.Ttl1h };

    private async Task<string> AskAsync(GenerateInput input, string? draft, List<string> problems, GenerateResult result, CancellationToken ct)
    {
        // Fixed part first and cached for an hour: standard, references and program context are the same in every round,
        // and a TIA import + compile + download between two rounds often takes longer than the default 5 minutes
        // (a later change request on the same package within the hour reads it from the cache too)
        var fixedPart = new StringBuilder();
        fixedPart.Append("<standard>\n").Append(input.Standard.Trim()).Append("\n</standard>\n\n");
        fixedPart.Append(GeneratePrompt.References(input.XmlReferences));
        if (input.ProgramContext != null)
            fixedPart.Append("<program>\nThe PLC program the new blocks go into:\n").Append(input.ProgramContext).Append("\n</program>\n");
        var content = new List<BetaContentBlockParam>
        {
            new BetaTextBlockParam { Text = fixedPart.ToString(), CacheControl = HourCache() },
        };

        var task = new StringBuilder();
        foreach (var e in input.Existing)
            task.Append("<existing name=\"").Append(e.Name).Append(e.Extension).Append("\">\n").Append(e.Text.TrimEnd()).Append("\n</existing>\n\n");
        if (input.Previous != null)
            task.Append("The current package (all its files):\n<package>\n").Append(input.Previous.ToAnswerText()).Append("</package>\n\n");
        if (input.TiaProblems.Count > 0)
            task.Append("TIA Portal imported and compiled this package and reported:\n")
                .Append(string.Join("\n", input.TiaProblems.Select(p => "- " + p))).Append("\n\n");
        if (input.Request.Length > 0)
            task.Append("Request:\n").Append(input.Request.Trim()).Append("\n\n");
        task.Append(input.Previous != null
            ? "Answer with the complete new package (every file, changed or not) and a review that says what changed since the current package."
            : input.Existing.Count > 0
                ? "Answer with the changed blocks only, as complete files, and the review."
                : "Answer with all files the request needs and the review.");
        content.Add(new BetaTextBlockParam { Text = task.ToString() });

        if (draft != null)
            content.Add(new BetaTextBlockParam
            {
                Text = "Your last answer:\n<draft>\n" + draft + "\n</draft>\n\nChecking it found these problems:\n" +
                       string.Join("\n", problems.Select(p => "- " + p)) + "\n\nFix every problem and answer with the complete corrected package.",
            });

        var parameters = new MessageCreateParams
        {
            Model = _model,
            MaxTokens = MaxTokens,
            System = new List<BetaTextBlockParam> { new() { Text = GeneratePrompt.System, CacheControl = HourCache() } },
            Messages = [new BetaUserMessageParam { Content = content }],
            OutputConfig = new BetaOutputConfig { Effort = _effort },
            Betas = [FallbackBeta],
            Fallbacks = new Default(),
        };

        var text = new StringBuilder();
        string? stop = null;
        int dots = 0;
        await foreach (var ev in _client.Beta.Messages.CreateStreaming(parameters, ct))
        {
            if (ev.TryPickStart(out var start))
            {
                var u = start.Message.Usage;
                result.InputTokens += u.InputTokens + (u.CacheCreationInputTokens ?? 0) + (u.CacheReadInputTokens ?? 0);
                result.CacheReadTokens += u.CacheReadInputTokens ?? 0;
            }
            else if (ev.TryPickContentBlockDelta(out var d) && d.Delta.TryPickText(out var t))
            {
                text.Append(t.Text);
                if (text.Length / 4000 > dots) { dots++; Console.Write("."); }
            }
            else if (ev.TryPickDelta(out var md))
            {
                stop = md.Delta.StopReason?.ToString();
                result.OutputTokens += md.Usage.OutputTokens;
            }
        }
        if (dots > 0) Console.WriteLine();
        if (stop != null && stop.Contains("refusal", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Claude declined to write these blocks.");
        if (stop != null && stop.Contains("max_tokens", StringComparison.OrdinalIgnoreCase))
            _log("  The answer hit the length limit and is cut off. Ask for fewer or smaller blocks per request, or choose a lower 'How hard Ki thinks'.");
        return text.ToString();
    }
}
