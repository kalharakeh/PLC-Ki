using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Anthropic;
using PlcKi.Core.Import;
using PlcKi.Core.Model;
using PlcKi.Server.Import;
using Xunit;

/// <summary>
/// Runs the model builder against a fake, streaming Claude API with the real demo export: checks what is sent
/// (program digest, caching, fallback opt-in) and that a draft with a wrong symbol goes back for correction.
/// No real API calls, no key needed.
/// </summary>
public class ModelBuilderTests
{
    private static readonly string ModelFile = Path.Combine(AppContext.BaseDirectory, "models", "demo-mixing-station.json");
    private static readonly string ExportFile = Path.Combine(AppContext.BaseDirectory, "data", "demo-export.zip");

    internal sealed class FakeStreamingApi : HttpMessageHandler
    {
        private readonly Queue<string> _answers;
        public readonly List<JsonElement> Requests = new();
        public readonly List<string> BetaHeaders = new();
        public FakeStreamingApi(params string[] answers) => _answers = new Queue<string>(answers);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = await request.Content!.ReadAsStringAsync(ct);
            Requests.Add(JsonDocument.Parse(body).RootElement.Clone());
            BetaHeaders.Add(request.Headers.TryGetValues("anthropic-beta", out var v) ? string.Join(",", v) : "");
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(Sse(_answers.Dequeue()), Encoding.UTF8, "text/event-stream"),
            };
        }

        /// <summary>The answer as server-sent events, cut into small text deltas like the real API.</summary>
        private static string Sse(string text)
        {
            var sb = new StringBuilder();
            void Ev(string type, object data) => sb.Append($"event: {type}\ndata: {JsonSerializer.Serialize(data)}\n\n");
            Ev("message_start", new
            {
                type = "message_start",
                message = new
                {
                    id = "msg_1", type = "message", role = "assistant", model = "claude-opus-5-5", content = Array.Empty<object>(),
                    stop_reason = (string?)null, stop_sequence = (string?)null,
                    usage = new { input_tokens = 1000, output_tokens = 1, cache_creation_input_tokens = 0, cache_read_input_tokens = 12000 },
                },
            });
            Ev("content_block_start", new { type = "content_block_start", index = 0, content_block = new { type = "text", text = "" } });
            for (int i = 0; i < text.Length; i += 700)
                Ev("content_block_delta", new { type = "content_block_delta", index = 0, delta = new { type = "text_delta", text = text.Substring(i, Math.Min(700, text.Length - i)) } });
            Ev("content_block_stop", new { type = "content_block_stop", index = 0 });
            Ev("message_delta", new { type = "message_delta", delta = new { stop_reason = "end_turn", stop_sequence = (string?)null }, usage = new { output_tokens = 5000 } });
            Ev("message_stop", new { type = "message_stop" });
            return sb.ToString();
        }
    }

    internal static AnthropicClient Client(FakeStreamingApi api) => new()
    {
        ApiKey = "test-key",
        BaseUrl = "http://fake-api.local",
        HttpClient = new HttpClient(api),
        MaxRetries = 0,
    };

    /// <summary>The hand-written model with the first tag's symbol pointing at a member that does not exist.</summary>
    private static string BrokenDraft(out string tagId)
    {
        var node = JsonNode.Parse(File.ReadAllText(ModelFile), documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true })!;
        var tag = node["tags"]![0]!;
        tagId = tag["id"]!.GetValue<string>();
        tag["symbol"] = tag["symbol"]!.GetValue<string>() + "_typo";
        return node.ToJsonString();
    }

    [Fact]
    public async Task Sends_the_program_and_corrects_a_wrong_symbol()
    {
        var api = new FakeStreamingApi("```json\n" + BrokenDraft(out var tagId) + "\n```", File.ReadAllText(ModelFile));
        var export = ProgramExport.Load(ExportFile);
        var log = new List<string>();
        var result = await new ModelBuilder(Client(api), "claude-opus-5-5", Anthropic.Models.Beta.Messages.Effort.High, log.Add)
            .BuildAsync(export, export.Plcs[0], CancellationToken.None);

        Assert.Equal(2, result.Attempts);
        Assert.Empty(result.Problems);
        Assert.NotNull(result.Model);
        Assert.Equal(TestData.Model.Tags.Count, result.Model!.Tags.Count);
        Assert.Equal(2 * 13000, result.InputTokens);
        Assert.Equal(2 * 12000, result.CacheReadTokens);
        Assert.Equal(2 * 5000, result.OutputTokens);

        Assert.Equal(2, api.Requests.Count);
        foreach (var (req, beta) in api.Requests.Zip(api.BetaHeaders))
        {
            Assert.True(req.GetProperty("stream").GetBoolean());
            Assert.Equal("claude-opus-5-5", req.GetProperty("model").GetString());
            Assert.Equal("high", req.GetProperty("output_config").GetProperty("effort").GetString());
            Assert.Contains("server-side-fallback", beta);
            Assert.True(req.GetProperty("system")[0].TryGetProperty("cache_control", out _));
            var program = req.GetProperty("messages")[0].GetProperty("content")[0];
            Assert.True(program.TryGetProperty("cache_control", out _));
            var text = program.GetProperty("text").GetString()!;
            Assert.Contains("## Readable tags", text);
            Assert.Contains("\"DB_Alarms\".A02_XV101_NotOpen", text);
        }
        // The correction round sends the draft and names the wrong symbol
        var fix = api.Requests[1].GetProperty("messages")[0].GetProperty("content")[1].GetProperty("text").GetString()!;
        Assert.Contains("<draft>", fix);
        Assert.Contains($"Tag '{tagId}'", fix);
        Assert.Contains("does not exist in the program", fix);
    }

    [Fact]
    public async Task Stops_after_the_last_repair_round_and_reports_what_is_left()
    {
        var draft = BrokenDraft(out var tagId);
        var api = new FakeStreamingApi(draft, draft);
        var export = ProgramExport.Load(ExportFile);
        var result = await new ModelBuilder(Client(api), "claude-opus-5-5", Anthropic.Models.Beta.Messages.Effort.Medium, _ => { }) { MaxRepairs = 1 }
            .BuildAsync(export, export.Plcs[0], CancellationToken.None);
        Assert.Equal(2, result.Attempts);
        Assert.Contains(result.Problems, p => p.Contains($"Tag '{tagId}'"));
    }

    [Fact]
    public async Task Import_command_writes_a_model_that_loads_and_compares()
    {
        var dir = Directory.CreateTempSubdirectory("plcki-import-");
        try
        {
            var outFile = Path.Combine(dir.FullName, "generated.json");
            var api = new FakeStreamingApi(File.ReadAllText(ModelFile));
            var code = await ImportCommand.RunAsync(["--export", ExportFile, "--out", outFile, "--compare", ModelFile], Client(api));
            Assert.Equal(0, code);
            var written = MachineModel.Load(outFile);
            var cmp = ModelCompare.Compare(written, TestData.Model);
            Assert.Equal(1.0, cmp.TagCoverage);
            Assert.Equal(cmp.ReferenceSteps, cmp.MatchedSteps);
            Assert.Equal(cmp.ReferenceDevices, cmp.MatchedDevices);
        }
        finally { dir.Delete(true); }
    }

    [Fact]
    public async Task Import_command_rejects_missing_arguments()
    {
        Assert.Equal(2, await ImportCommand.RunAsync(["--out", "x.json"]));
        Assert.Equal(2, await ImportCommand.RunAsync(["--export", "does-not-exist.zip", "--out", "x.json"]));
    }

    [Fact]
    public void Extracts_the_json_object_from_an_answer()
    {
        Assert.Equal("{\"a\":1}", ModelBuilder.ExtractJson("Here:\n```json\n{\"a\":1}\n```"));
        Assert.Null(ModelBuilder.ExtractJson("no model"));
        Assert.Null(ModelBuilder.ExtractJson("{ broken"));
    }
}
