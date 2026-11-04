using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using Anthropic;
using Anthropic.Exceptions;
using Anthropic.Models.Beta.Messages;
using PlcKi.Core.Ask;
using PlcKi.Core.Status;
using PlcKi.Server.Live;

namespace PlcKi.Server.Ask;

public sealed record AskRequest(string? ConversationId, string? Question);

/// <summary>A lookup Claude made while answering, shown under the answer ("Looked at: live status, alarm A07").</summary>
public sealed record AskStep(string Tool, string Label);

public sealed record AskAnswer(
    string ConversationId, string Answer, List<AskStep> Steps, string Model,
    string? Note, long InputTokens, long OutputTokens, long CacheReadTokens, double Seconds);

/// <summary>
/// Answers questions about the machine with Claude. Claude gets the machine model index and the live summary,
/// and looks up the rest with <see cref="MachineTools"/> (read-only lookups of values PLC-Ki has already read).
/// Conversations are kept in memory so follow-up questions work. Nothing here talks to the PLC.
/// </summary>
public sealed class AskService
{
    // Server-side fallback: if Claude's safety classifiers decline a question (rarely, and sometimes wrongly),
    // the API answers it with Anthropic's recommended fallback model instead of returning a refusal.
    private const string FallbackBeta = "server-side-fallback-2026-07-01";
    private static readonly TimeSpan ConversationIdle = TimeSpan.FromHours(2);
    private const int MaxConversations = 50;

    private readonly AskOptions _opt;
    private readonly MachineTools _tools;
    private readonly LiveState _live;
    private readonly ILogger<AskService> _log;
    private readonly PlcKi.Core.Knowledge.DocumentLibrary? _docs;
    private readonly string _system;
    private readonly List<BetaToolUnion> _toolDefs;
    private readonly ConcurrentDictionary<string, Conversation> _conversations = new();
    private AnthropicClient? _client;

    private sealed class Conversation
    {
        public readonly List<BetaMessageParam> Messages = new();
        public readonly SemaphoreSlim Lock = new(1, 1);
        public DateTimeOffset LastUsed = DateTimeOffset.Now;
    }

    public AskService(AskOptions opt, MachineTools tools, LiveState live, ILogger<AskService> log, PlcKi.Core.Knowledge.DocumentLibrary? docs = null)
    {
        _docs = docs;
        _opt = opt;
        _tools = tools;
        _live = live;
        _log = log;
        _system = SystemPrompt.Build(tools.ModelIndex());
        _toolDefs = MachineTools.Specs.Select(ToTool).ToList();
    }

    /// <summary>Use this client instead of one made from ANTHROPIC_API_KEY (tests point it at a fake API).</summary>
    public void UseClient(AnthropicClient client) => _client = client;

    /// <summary>Whether the box can be used, and if not, why (shown on the page).</summary>
    public (bool Enabled, string? Reason) Availability()
    {
        if (!_opt.Enabled) return (false, "Ask is switched off for this PLC-Ki.");
        if (_client == null && string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY")))
            return (false, "Ask needs a Claude key, and none is set yet.");
        return (true, null);
    }

    public void Forget(string conversationId) => _conversations.TryRemove(conversationId, out _);

    public async Task<AskAnswer> AskAsync(AskRequest req, CancellationToken ct)
    {
        var (enabled, reason) = Availability();
        if (!enabled) throw new AskException(reason!, 503);
        var question = req.Question?.Trim();
        if (string.IsNullOrEmpty(question)) throw new AskException("Type a question first.", 400);
        if (question.Length > 4000) throw new AskException("The question is too long (4000 characters max).", 400);

        Cleanup();
        var id = req.ConversationId is { Length: > 0 } cid && _conversations.ContainsKey(cid) ? cid : Guid.NewGuid().ToString("N");
        var conv = _conversations.GetOrAdd(id, _ => new Conversation());
        await conv.Lock.WaitAsync(ct);
        int rollback = conv.Messages.Count;
        try
        {
            conv.LastUsed = DateTimeOffset.Now;
            conv.Messages.Add(new BetaUserMessageParam { Content = UserTurn(question) });
            var result = await RunLoop(id, conv, ct);
            if (result.Rollback) conv.Messages.RemoveRange(rollback, conv.Messages.Count - rollback);
            return result.Answer;
        }
        catch
        {
            // Keep the conversation valid for the next question
            conv.Messages.RemoveRange(rollback, conv.Messages.Count - rollback);
            throw;
        }
        finally
        {
            conv.Lock.Release();
        }
    }

    private async Task<(AskAnswer Answer, bool Rollback)> RunLoop(string id, Conversation conv, CancellationToken ct)
    {
        var client = _client ??= new AnthropicClient();   // reads ANTHROPIC_API_KEY
        var started = DateTimeOffset.Now;
        var steps = new List<AskStep>();
        long inTok = 0, outTok = 0, cacheTok = 0;
        string model = _opt.Model;
        string? note = null;

        for (int round = 0; round < Math.Max(1, _opt.MaxToolRounds); round++)
        {
            bool lastRound = round == _opt.MaxToolRounds - 1;
            var parameters = new MessageCreateParams
            {
                Model = _opt.Model,
                MaxTokens = _opt.MaxTokens,
                System = new List<BetaTextBlockParam> { new() { Text = _system, CacheControl = new BetaCacheControlEphemeral() } },
                Tools = _toolDefs,
                // On the last round Claude must answer with what it has
                ToolChoice = lastRound ? new BetaToolChoiceNone() : new BetaToolChoiceAuto(),
                Messages = conv.Messages.ToList(),
                OutputConfig = new BetaOutputConfig { Effort = ParseEffort(_opt.Effort) },
                // Caches the conversation so far, so each tool round and follow-up only pays for what is new
                CacheControl = new BetaCacheControlEphemeral(),
                Betas = [FallbackBeta],
                Fallbacks = new Default(),   // "default": Anthropic picks the fallback model per refusal category
            };

            BetaMessage resp;
            try
            {
                resp = await client.Beta.Messages.Create(parameters, ct);
            }
            catch (AnthropicUnauthorizedException)
            {
                throw new AskException("The Claude key was not accepted. An administrator enters a new one under Setup, Settings (Assistant).", 502);
            }
            catch (AnthropicRateLimitException)
            {
                throw new AskException("Too many requests to Claude right now. Wait a minute and ask again.", 429);
            }
            catch (AnthropicApiException ex)
            {
                _log.LogWarning(ex, "Claude API error");
                throw new AskException($"Claude API error: {ex.Message}", 502);
            }
            catch (AnthropicIOException ex)
            {
                throw new AskException($"Cannot reach the Claude API (internet connection or proxy?): {ex.Message}", 502);
            }

            inTok += resp.Usage.InputTokens + (resp.Usage.CacheCreationInputTokens ?? 0) + (resp.Usage.CacheReadInputTokens ?? 0);
            outTok += resp.Usage.OutputTokens;
            cacheTok += resp.Usage.CacheReadInputTokens ?? 0;
            model = resp.Model.ToString()?.Trim('"') ?? model;

            var assistant = new List<BetaContentBlockParam>();
            var results = new List<BetaContentBlockParam>();
            var text = new StringBuilder();
            foreach (var block in resp.Content)
            {
                if (block.TryPickText(out var t))
                {
                    assistant.Add(new BetaTextBlockParam { Text = t.Text });
                    text.Append(t.Text);
                }
                else if (block.TryPickThinking(out var th))
                    assistant.Add(new BetaThinkingBlockParam { Thinking = th.Thinking, Signature = th.Signature });
                else if (block.TryPickRedactedThinking(out var rt))
                    assistant.Add(new BetaRedactedThinkingBlockParam { Data = rt.Data });
                else if (block.TryPickFallback(out var fb))
                {
                    // The question was handed to the fallback model; the marker must stay in the history
                    assistant.Add(JsonSerializer.Deserialize<BetaFallbackBlockParam>(JsonSerializer.Serialize(fb))!);
                    note = "Answered by the fallback model because the main model declined this question.";
                    text.Clear();
                }
                else if (block.TryPickToolUse(out var tu))
                {
                    assistant.Add(new BetaToolUseBlockParam { ID = tu.ID, Name = tu.Name, Input = tu.Input });
                    var input = JsonSerializer.SerializeToElement(tu.Input);
                    var output = _tools.Run(tu.Name, input);
                    steps.Add(new AskStep(tu.Name, StepLabel(tu.Name, input)));
                    _log.LogDebug("Tool {Tool} {Input} -> {Length} chars", tu.Name, input.GetRawText(), output.Length);
                    results.Add(new BetaToolResultBlockParam { ToolUseID = tu.ID, Content = output });
                }
            }

            var stop = resp.StopReason?.ToString() ?? "";
            if (stop.Contains("refusal", StringComparison.OrdinalIgnoreCase))
            {
                var why = resp.StopDetails is { } d ? $" ({d})" : "";
                _log.LogWarning("Claude declined the question{Why}", why);
                return (Done(id, "Claude declined to answer this question. Try asking it differently, for example about a specific alarm, step or device.",
                    steps, model, "declined" + why, inTok, outTok, cacheTok, started), true);
            }

            if (assistant.Count > 0) conv.Messages.Add(new BetaAssistantMessageParam { Content = assistant });

            if (results.Count > 0)
            {
                conv.Messages.Add(new BetaUserMessageParam { Content = results });
                continue;
            }

            var answer = text.ToString().Trim();
            if (stop.Contains("max_tokens", StringComparison.OrdinalIgnoreCase))
                note = "The answer was cut off because it got too long. Ask for a shorter answer or a specific part.";
            if (answer.Length == 0) answer = "(No answer text came back. Please ask again.)";
            return (Done(id, answer, steps, model, note, inTok, outTok, cacheTok, started), false);
        }

        return (Done(id, "I needed more lookups than allowed for one question. Please ask something more specific.",
            steps, model, null, inTok, outTok, cacheTok, started), true);
    }

    private static AskAnswer Done(string id, string answer, List<AskStep> steps, string model, string? note,
        long inTok, long outTok, long cacheTok, DateTimeOffset started) =>
        new(id, answer, steps, model, note, inTok, outTok, cacheTok, Math.Round((DateTimeOffset.Now - started).TotalSeconds, 1));

    /// <summary>The question plus the live summary, so most questions start with the current picture.</summary>
    private string UserTurn(string question)
    {
        var st = _live.Status;
        var sb = new StringBuilder();
        sb.AppendLine($"<live_summary time=\"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss}\">");
        if (st == null) sb.AppendLine("No status yet: PLC-Ki is still starting.");
        else
        {
            sb.AppendLine($"PLC link: {st.Connection.State}" + (st.Connection.Error is { Length: > 0 } e ? $" ({e})" : ""));
            foreach (var line in st.Summary) sb.AppendLine(line);
            // What the machine memory knows about the alarms that are on: what fixed them before
            foreach (var a in st.ActiveAlarms.Take(5))
                sb.AppendLine($"Machine memory, {a.Id}: {_live.Memory.Summary(a.Id, DateTimeOffset.Now).Insight}");
        }
        sb.AppendLine("</live_summary>");
        // Which manuals and drawings exist (they can be added at any time, so not in the cached system prompt)
        var docs = _docs?.List().Where(d => d.State == "ready").ToList();
        if (docs is { Count: > 0 })
            sb.AppendLine("<documents>" + string.Join("; ", docs.Take(40).Select(d => d.File + (d.Device != null ? $" ({d.Device})" : "") + $" [{d.Kind}]")) +
                          (docs.Count > 40 ? $"; ... {docs.Count - 40} more" : "") + "</documents>");
        sb.AppendLine();
        sb.Append(question);
        return sb.ToString();
    }

    private static string StepLabel(string tool, JsonElement input)
    {
        string? S(string n) => input.ValueKind == JsonValueKind.Object && input.TryGetProperty(n, out var p) ? p.ValueKind switch
        {
            JsonValueKind.String => p.GetString(),
            JsonValueKind.Array => string.Join(", ", p.EnumerateArray().Select(x => x.ToString())),
            JsonValueKind.Number => p.GetRawText(),
            _ => null,
        } : null;
        return tool switch
        {
            "get_live_status" => "live status",
            "read_tags" => "values " + (S("ids") ?? S("search") ?? ""),
            "get_tag_history" => $"history of {S("ids")}" + (S("minutes") is { } m ? $" ({m} min)" : ""),
            "get_recent_events" => "recent events",
            "get_sequence" => "sequence " + (S("id") ?? ""),
            "get_equipment" => "device " + S("id"),
            "get_alarm" => "alarm " + S("id"),
            "search_program" => $"program search \"{S("query")}\"",
            "lookup_siemens_diagnostic" => $"Siemens message \"{S("query")}\"",
            "lookup_device_fault" => $"fault code {S("code")}" + (S("device") is { } dv ? $" ({dv})" : ""),
            "search_documents" => $"manuals \"{S("query")}\"",
            "get_alarm_capture" => "seconds before " + (S("alarm") ?? S("capture") ?? "the alarms"),
            _ => tool,
        };
    }

    private static BetaToolUnion ToTool(ToolSpec spec) => new BetaTool
    {
        Name = spec.Name,
        Description = spec.Description,
        InputSchema = new()
        {
            Properties = spec.Properties.ToDictionary(p => p.Key, p => JsonSerializer.SerializeToElement(p.Value)),
            Required = spec.Required.ToList(),
        },
    };

    internal static Effort ParseEffort(string? s) => s?.Trim().ToLowerInvariant() switch
    {
        "low" => Effort.Low,
        "high" => Effort.High,
        "max" => Effort.Max,
        _ => Effort.Medium,
    };

    private void Cleanup()
    {
        var now = DateTimeOffset.Now;
        foreach (var (key, c) in _conversations)
            if (now - c.LastUsed > ConversationIdle) _conversations.TryRemove(key, out _);
        if (_conversations.Count > MaxConversations)
            foreach (var key in _conversations.OrderBy(kv => kv.Value.LastUsed).Take(_conversations.Count - MaxConversations).Select(kv => kv.Key))
                _conversations.TryRemove(key, out _);
    }
}

public sealed class AskException : Exception
{
    public int Status { get; }
    public AskException(string message, int status) : base(message) => Status = status;
}
