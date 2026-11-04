using System.Net;
using System.Text;
using System.Text.Json;
using Anthropic;
using Microsoft.Extensions.Logging.Abstractions;
using PlcKi.Core.Ask;
using PlcKi.Core.Status;
using PlcKi.Server.Ask;
using PlcKi.Server.Live;
using Xunit;

/// <summary>
/// Runs the assistant's question loop against a fake Claude API: checks what is sent (tools, caching, fallback opt-in,
/// thinking blocks passed back) and how answers, fallbacks and refusals are handled. No real API calls, no key needed.
/// </summary>
public class AskTests
{
    private sealed class FakeApi : HttpMessageHandler
    {
        private readonly Queue<string> _responses;
        public readonly List<JsonElement> Requests = new();
        public readonly List<string> BetaHeaders = new();
        public FakeApi(params string[] responses) => _responses = new Queue<string>(responses);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = await request.Content!.ReadAsStringAsync(ct);
            Requests.Add(JsonDocument.Parse(body).RootElement.Clone());
            BetaHeaders.Add(request.Headers.TryGetValues("anthropic-beta", out var v) ? string.Join(",", v) : "");
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(_responses.Dequeue(), Encoding.UTF8, "application/json"),
            };
        }
    }

    private static string Message(string stopReason, params object[] content) => JsonSerializer.Serialize(new
    {
        id = "msg_" + Guid.NewGuid().ToString("N")[..8],
        type = "message",
        role = "assistant",
        model = "claude-opus-5-5",
        content,
        stop_reason = stopReason,
        stop_sequence = (string?)null,
        usage = new { input_tokens = 100, output_tokens = 20, cache_creation_input_tokens = 0, cache_read_input_tokens = 50 },
    });

    private static (AskService Ask, FakeApi Api) Make(params string[] responses)
    {
        var history = new MachineHistory();
        var live = new LiveState(history, new LiveModelCheck(TestData.Model));
        var snap = TestData.Snapshot("step10-xv101-not-open");
        live.Publish(snap, TestData.Evaluate(snap));
        var tools = new MachineTools(TestData.Model, () => new LiveView(live.Status, live.Snapshot, history, DateTimeOffset.Now));
        var ask = new AskService(new AskOptions(), tools, live, NullLogger<AskService>.Instance);
        var api = new FakeApi(responses);
        ask.UseClient(new AnthropicClient
        {
            ApiKey = "test-key",
            BaseUrl = "http://fake-api.local",
            HttpClient = new HttpClient(api),
            MaxRetries = 0,
        });
        return (ask, api);
    }

    [Fact]
    public async Task Runs_the_tool_and_returns_the_answer()
    {
        var (ask, api) = Make(
            Message("tool_use",
                new { type = "thinking", thinking = "", signature = "sig-1" },
                new { type = "tool_use", id = "toolu_1", name = "get_alarm", input = new { id = "A02" } }),
            Message("end_turn", new { type = "text", text = "Valve **XV101** did not open." }));

        var answer = await ask.AskAsync(new AskRequest(null, "Why is it stopped?"), default);

        Assert.Equal("Valve **XV101** did not open.", answer.Answer);
        Assert.Equal("alarm A02", Assert.Single(answer.Steps).Label);
        Assert.Equal(2, api.Requests.Count);

        var first = api.Requests[0];
        Assert.Equal("claude-opus-5-5", first.GetProperty("model").GetString());
        Assert.Equal("default", first.GetProperty("fallbacks").GetString());
        Assert.Contains("server-side-fallback-2026-07-01", api.BetaHeaders[0]);
        Assert.Equal("ephemeral", first.GetProperty("system")[0].GetProperty("cache_control").GetProperty("type").GetString());
        Assert.Equal("ephemeral", first.GetProperty("cache_control").GetProperty("type").GetString());
        Assert.Equal("medium", first.GetProperty("output_config").GetProperty("effort").GetString());
        Assert.Equal(MachineTools.Specs.Count, first.GetProperty("tools").GetArrayLength());
        Assert.Contains("<machine_model_index>", first.GetProperty("system")[0].GetProperty("text").GetString());
        var question = first.GetProperty("messages")[0].GetProperty("content").GetString()!;
        Assert.Contains("<live_summary", question);
        Assert.Contains("step 10", question);
        Assert.EndsWith("Why is it stopped?", question);

        // Second request: thinking passed back with its signature, then the tool result for the same id
        var msgs = api.Requests[1].GetProperty("messages");
        Assert.Equal(3, msgs.GetArrayLength());
        var assistant = msgs[1].GetProperty("content");
        Assert.Equal("sig-1", assistant[0].GetProperty("signature").GetString());
        Assert.Equal("toolu_1", assistant[1].GetProperty("id").GetString());
        var result = msgs[2].GetProperty("content")[0];
        Assert.Equal("tool_result", result.GetProperty("type").GetString());
        Assert.Equal("toolu_1", result.GetProperty("tool_use_id").GetString());
        Assert.Contains("did not open", result.GetProperty("content").ToString());
    }

    [Fact]
    public async Task Follow_up_questions_continue_the_conversation()
    {
        var (ask, api) = Make(
            Message("end_turn", new { type = "text", text = "First." }),
            Message("end_turn", new { type = "text", text = "Second." }));
        var a1 = await ask.AskAsync(new AskRequest(null, "One?"), default);
        var a2 = await ask.AskAsync(new AskRequest(a1.ConversationId, "Two?"), default);
        Assert.Equal(a1.ConversationId, a2.ConversationId);
        Assert.Equal(3, api.Requests[1].GetProperty("messages").GetArrayLength());
    }

    [Fact]
    public async Task Fallback_marker_is_kept_in_the_history()
    {
        var (ask, api) = Make(
            Message("end_turn",
                new { type = "fallback", from = new { model = "claude-opus-5-5" }, to = new { model = "claude-opus-4-8" }, trigger = new { type = "refusal", category = "cyber" } },
                new { type = "text", text = "Answer from the fallback model." }),
            Message("end_turn", new { type = "text", text = "Next." }));
        var a1 = await ask.AskAsync(new AskRequest(null, "Question?"), default);
        Assert.Equal("Answer from the fallback model.", a1.Answer);
        Assert.NotNull(a1.Note);

        await ask.AskAsync(new AskRequest(a1.ConversationId, "And?"), default);
        var echoed = api.Requests[1].GetProperty("messages")[1].GetProperty("content")[0];
        Assert.Equal("fallback", echoed.GetProperty("type").GetString());
        Assert.Equal("claude-opus-4-8", echoed.GetProperty("to").GetProperty("model").GetString());
    }

    [Fact]
    public async Task A_refusal_is_explained_and_left_out_of_the_conversation()
    {
        var (ask, api) = Make(
            Message("refusal"),
            Message("end_turn", new { type = "text", text = "Fine." }));
        var a1 = await ask.AskAsync(new AskRequest(null, "Odd question"), default);
        Assert.Contains("declined", a1.Answer);
        await ask.AskAsync(new AskRequest(a1.ConversationId, "Normal question"), default);
        Assert.Equal(1, api.Requests[1].GetProperty("messages").GetArrayLength());
    }

    [Fact]
    public void Without_a_key_the_box_explains_what_to_set()
    {
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY"))) return;
        var history = new MachineHistory();
        var live = new LiveState(history, new LiveModelCheck(TestData.Model));
        var tools = new MachineTools(TestData.Model, () => new LiveView(null, null, history, DateTimeOffset.Now));
        var ask = new AskService(new AskOptions(), tools, live, NullLogger<AskService>.Instance);
        var (enabled, reason) = ask.Availability();
        Assert.False(enabled);
        Assert.Contains("Claude key", reason);
    }
}
