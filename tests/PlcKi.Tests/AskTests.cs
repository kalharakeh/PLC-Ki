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

    private static (AskService Ask, FakeApi Api) Make(params string[] responses) => MakeWith(null, null, responses);

    private static (AskService Ask, FakeApi Api) MakeWith(AskUsage? usage, Func<double>? limit, params string[] responses)
    {
        var history = new MachineHistory();
        var live = new LiveState(history, new LiveModelCheck(TestData.Model));
        var snap = TestData.Snapshot("step10-xv101-not-open");
        live.Publish(snap, TestData.Evaluate(snap));
        var tools = new MachineTools(TestData.Model, () => new LiveView(live.Status, live.Snapshot, history, DateTimeOffset.Now));
        var ask = new AskService(new AskOptions(), tools, live, NullLogger<AskService>.Instance, null, usage, limit);
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
    public async Task A_German_person_gets_the_answer_in_German()
    {
        var (ask, api) = Make(Message("end_turn", new { type = "text", text = "Das Ventil XV101 öffnet nicht." }));
        await ask.AskAsync(new AskRequest(null, "Why?", "de"), default);

        var system = api.Requests[0].GetProperty("system");
        Assert.Equal(2, system.GetArrayLength());                                       // the cached prompt stays as it is, the language comes after it
        Assert.Equal("ephemeral", system[0].GetProperty("cache_control").GetProperty("type").GetString());
        Assert.Contains("German", system[1].GetProperty("text").GetString());
        Assert.False(system[1].TryGetProperty("cache_control", out _));
        Assert.Null(PlcKi.Server.Ask.SystemPrompt.LanguageNote("en"));                 // English or none: the answer follows the question as before
        Assert.Null(PlcKi.Server.Ask.SystemPrompt.LanguageNote(null));
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
    public async Task Every_answer_is_counted_and_the_monthly_limit_stops_new_questions()
    {
        var file = Path.Combine(Path.GetTempPath(), "plcki-ask-usage-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var usage = new AskUsage(file);
            double limit = 0;
            var (ask, api) = MakeWith(usage, () => limit,
                Message("tool_use", new { type = "tool_use", id = "toolu_1", name = "get_alarm", input = new { id = "A02" } }),
                Message("end_turn", new { type = "text", text = "Done." }));
            await ask.AskAsync(new AskRequest(null, "Why?"), default);

            var m = usage.ThisMonth();
            Assert.Equal(1, m.Questions);
            Assert.Equal(200, m.Input);           // two answers of 100 input tokens each
            Assert.Equal(40, m.Output);
            Assert.Equal(100, m.CacheRead);
            Assert.Equal(AskUsage.Cost("claude-opus-5-5", 200, 40, 0, 100), m.Cost, 9);
            Assert.True(File.Exists(file));

            limit = m.Cost;                       // reached: Ask switches off with a plain reason, at once
            var (on, why) = ask.Availability();
            Assert.False(on);
            Assert.Contains("limit", why);
            var refused = await Assert.ThrowsAsync<AskException>(() => ask.AskAsync(new AskRequest(null, "And now?"), default));
            Assert.Equal(2, api.Requests.Count);  // nothing more was sent to Claude
            Assert.Equal(1, new AskUsage(file).ThisMonth().Questions);     // read back from the file

            limit = 0;                            // no limit
            Assert.True(ask.Availability().Enabled);
        }
        finally { File.Delete(file); }
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
