using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Anthropic;
using Anthropic.Models.Beta.Messages;
using PlcKi.Core.Import;
using PlcKi.Core.Model;

namespace PlcKi.Server.Import;

public sealed class ModelBuildResult
{
    public MachineModel? Model { get; set; }
    public string Json { get; set; } = "";
    public List<string> Problems { get; set; } = new();
    public int Attempts { get; set; }
    public long InputTokens { get; set; }
    public long OutputTokens { get; set; }
    public long CacheReadTokens { get; set; }
    public TimeSpan Time { get; set; }
}

/// <summary>
/// Writes a machine model from a program export with Claude, once, at import time. Claude reads the program digest
/// and answers with the model as JSON. The result is checked against the real tag list and the model rules; if
/// anything is wrong, Claude gets the draft and the problems back and corrects it (a few rounds at most).
/// </summary>
public sealed class ModelBuilder
{
    private const string FallbackBeta = "server-side-fallback-2026-07-01";
    private readonly AnthropicClient _client;
    private readonly string _model;
    private readonly Effort _effort;
    private readonly Action<string> _log;

    public int MaxRepairs { get; init; } = 3;
    public int MaxTokens { get; init; } = 64000;

    public ModelBuilder(AnthropicClient client, string model, Effort effort, Action<string> log)
    {
        _client = client;
        _model = model;
        _effort = effort;
        _log = log;
    }

    public async Task<ModelBuildResult> BuildAsync(ProgramExport export, ExportPlc plc, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var result = new ModelBuildResult();
        var digest = ProgramDigest.Build(export, plc);
        _log($"Program digest: {digest.Length / 1000} kB of text for {plc.Blocks.Count} blocks.");

        string? draft = null;
        List<string> problems = new();
        for (int attempt = 1; attempt <= 1 + MaxRepairs; attempt++)
        {
            result.Attempts = attempt;
            _log(attempt == 1 ? "Claude is reading the program and writing the model..." : $"Correcting {problems.Count} problem(s), round {attempt - 1}...");
            var text = await AskAsync(digest, draft, problems, result, ct);
            var json = ExtractJson(text);
            if (json == null)
            {
                problems = new() { "The answer was not a JSON object. Answer with the model JSON only." };
                draft = text.Length > 2000 ? text[..2000] : text;
                continue;
            }
            result.Json = json;
            MachineModel? model;
            try
            {
                model = JsonSerializer.Deserialize<MachineModel>(json, MachineModel.JsonOptions);
            }
            catch (JsonException ex)
            {
                problems = new() { $"The JSON does not match the format: {ex.Message}" };
                draft = json;
                continue;
            }
            if (model == null) { problems = new() { "Empty model." }; draft = json; continue; }

            problems = ModelCheck.Check(model, plc);
            result.Model = model;
            result.Problems = problems;
            draft = json;
            if (problems.Count == 0) break;
            _log($"  {problems.Count} problem(s) found.");
        }
        result.Time = sw.Elapsed;
        return result;
    }

    private async Task<string> AskAsync(string digest, string? draft, List<string> problems, ModelBuildResult result, CancellationToken ct)
    {
        var content = new List<BetaContentBlockParam>
        {
            // The program is the same in every round: cached, so corrections only pay for the draft and the problems
            new BetaTextBlockParam { Text = "<program>\n" + digest + "\n</program>", CacheControl = new BetaCacheControlEphemeral() },
        };
        if (draft == null)
            content.Add(new BetaTextBlockParam { Text = "Write the machine model for this program." });
        else
            content.Add(new BetaTextBlockParam
            {
                Text = "Here is your draft of the machine model:\n<draft>\n" + draft + "\n</draft>\n\n" +
                       "Checking it against the program found these problems:\n" + string.Join("\n", problems.Select(p => "- " + p)) +
                       "\n\nFix every problem and answer with the complete corrected model JSON.",
            });

        var parameters = new MessageCreateParams
        {
            Model = _model,
            MaxTokens = MaxTokens,
            System = new List<BetaTextBlockParam> { new() { Text = ModelPrompt.System, CacheControl = new BetaCacheControlEphemeral() } },
            Messages = [new BetaUserMessageParam { Content = content }],
            OutputConfig = new BetaOutputConfig { Effort = _effort },
            Betas = [FallbackBeta],
            Fallbacks = new Default(),   // a wrongly declined request is answered by Anthropic's recommended fallback model
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
                if (text.Length / 2000 > dots) { dots++; Console.Write("."); }
            }
            else if (ev.TryPickDelta(out var md))
            {
                stop = md.Delta.StopReason?.ToString();
                result.OutputTokens += md.Usage.OutputTokens;
            }
        }
        if (dots > 0) Console.WriteLine();
        if (stop != null && stop.Contains("refusal", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Claude declined to write the model for this program.");
        if (stop != null && stop.Contains("max_tokens", StringComparison.OrdinalIgnoreCase))
            _log("  The answer hit the length limit; it may be cut off.");
        return text.ToString();
    }

    /// <summary>The JSON object in the answer: from the first { to the last }, tolerating code fences around it.</summary>
    public static string? ExtractJson(string text)
    {
        int a = text.IndexOf('{'), b = text.LastIndexOf('}');
        if (a < 0 || b <= a) return null;
        var json = text[a..(b + 1)];
        try { using var _ = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip }); }
        catch (JsonException) { return null; }
        return json;
    }
}
