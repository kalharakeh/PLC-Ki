using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Anthropic;
using Anthropic.Models.Beta.Messages;
using PlcKi.Core.Import;
using PlcKi.Core.Model;
using PlcKi.Core.Status;
using PlcKi.Server.Ask;
using PlcKi.Server.Jobs;
using PlcKi.Server.Live;

namespace PlcKi.Server.Import;

/// <summary>The machine model file PLC-Ki runs on (full path).</summary>
public sealed record ModelFile(string Path);

public sealed record ModelEntryRequest(string? Target, string? Json, string? Note, string? NewTags = null);

public sealed class ModelFixException(string message, int status = 400) : Exception(message)
{
    public int Status { get; } = status;
}

/// <summary>
/// Corrects one entry of the machine model (a device, an alarm, a sequence, a setting or a tag) without a new import:
/// read it, let Ki propose a corrected entry from the program and what the live machine did, check the result
/// against the program, and save it (a copy of the old model is kept next to it).
/// </summary>
public sealed class ModelFixService
{
    private const string FallbackBeta = "server-side-fallback-2026-07-01";
    private readonly ModelFile _file;
    private readonly ProgramService _program;
    private readonly LiveState _live;
    private readonly AskOptions _ask;
    private AnthropicClient? _client;

    public ModelFixService(ModelFile file, ProgramService program, LiveState live, AskOptions ask, AnthropicClient? client = null)
    {
        _file = file;
        _program = program;
        _live = live;
        _ask = ask;
        _client = client;
    }

    public bool CanAsk => _client != null || !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY"));

    /// <summary>"equipment:XV1" -> ("equipment", "XV1"). Also takes the kinds the live check uses (sequence, alarm, setting, tag).</summary>
    public static (string Array, string Key, string Id) ParseTarget(string? target)
    {
        var t = target?.Trim() ?? "";
        var i = t.IndexOf(':');
        if (i <= 0 || i == t.Length - 1) throw new ModelFixException("Say which entry: for example equipment:XV1 or alarm:A02.");
        var kind = t[..i].ToLowerInvariant();
        var id = t[(i + 1)..];
        return kind switch
        {
            "equipment" or "device" => ("equipment", "id", id),
            "alarm" => ("alarms", "id", id),
            "sequence" => ("sequences", "id", id),
            "setting" => ("settings", "tag", id),
            "tag" => ("tags", "id", id),
            _ => throw new ModelFixException($"Unknown kind of entry '{kind}'."),
        };
    }

    private JsonObject LoadRoot()
    {
        try
        {
            return JsonNode.Parse(File.ReadAllText(_file.Path), documentOptions: new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip }) as JsonObject
                   ?? throw new ModelFixException("The model file is empty.", 500);
        }
        catch (IOException ex) { throw new ModelFixException($"Cannot read the model file {_file.Path}: {ex.Message}", 500); }
    }

    private static JsonObject? Find(JsonObject root, string array, string key, string id) =>
        (root[array] as JsonArray)?.OfType<JsonObject>().FirstOrDefault(o => o[key]?.GetValue<string>() == id);

    public object Get(string? target)
    {
        var (array, key, id) = ParseTarget(target);
        var entry = Find(LoadRoot(), array, key, id);
        return new
        {
            target = $"{(array == "equipment" ? "equipment" : array.TrimEnd('s'))}:{id}",
            exists = entry != null,
            json = entry?.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) ?? "",
            findings = Findings(target!),
            canAsk = CanAsk,
            file = _file.Path,
        };
    }

    private List<ModelFinding> Findings(string target)
    {
        var (array, _, id) = ParseTarget(target);
        var prefix = array switch { "equipment" => "equipment:", "alarms" => "alarm:", "sequences" => "sequence:", "settings" => "setting:", _ => "tag:" };
        return _live.Check.Report().Findings.Where(f => f.Target == prefix + id).ToList();
    }

    /// <summary>Puts the entry into a copy of the model and checks the whole model (and the symbols, when the program is loaded).</summary>
    public (JsonObject Root, MachineModel Model, List<string> Problems) Merge(string? target, string json, JsonArray? newTags = null)
    {
        var (array, key, id) = ParseTarget(target);
        JsonObject entry;
        try { entry = JsonNode.Parse(json, documentOptions: new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip }) as JsonObject
                      ?? throw new ModelFixException("The entry must be one JSON object { ... }."); }
        catch (JsonException ex) { throw new ModelFixException("The entry is not valid JSON: " + ex.Message); }
        if (entry[key]?.GetValue<string>() is not { Length: > 0 } newId)
            throw new ModelFixException($"The entry needs its \"{key}\".");

        var root = LoadRoot();
        if (root[array] is not JsonArray list) root[array] = list = new JsonArray();
        var old = list.OfType<JsonObject>().FirstOrDefault(o => o[key]?.GetValue<string>() == id);
        var index = old != null ? list.IndexOf(old) : -1;
        if (newId != id && list.OfType<JsonObject>().Any(o => o[key]?.GetValue<string>() == newId))
            throw new ModelFixException($"Another entry already uses \"{newId}\".");
        if (index >= 0) list[index] = entry; else list.Add(entry);

        // Tags Ki needs for the corrected entry and the model does not have yet
        if (newTags != null && root["tags"] is JsonArray tags)
            foreach (var t in newTags.OfType<JsonObject>())
                if (t["id"]?.GetValue<string>() is { Length: > 0 } tid && !tags.OfType<JsonObject>().Any(o => o["id"]?.GetValue<string>() == tid))
                    tags.Add(t.DeepClone());

        var problems = new List<string>();
        MachineModel? model = null;
        try { model = root.Deserialize<MachineModel>(MachineModel.JsonOptions); }
        catch (JsonException ex) { problems.Add("The entry does not match the model format: " + ex.Message); }
        if (model != null)
        {
            if (_program.Loaded.Plc is { } plc) problems.AddRange(ModelCheck.Check(model, plc));
            else
                try { model.Validate(); }
                catch (InvalidDataException ex) { problems.AddRange(ex.Message.Split('\n').Skip(1).Select(l => l.Trim()).Where(l => l.Length > 0)); }
        }
        return (root, model ?? new MachineModel(), problems);
    }

    /// <summary>Saves the corrected entry. Keeps the previous model as "&lt;file&gt;.before-&lt;time&gt;.json".</summary>
    public object Save(string? target, string? json, JsonArray? newTags = null)
    {
        if (string.IsNullOrWhiteSpace(json)) throw new ModelFixException("The entry is empty.");
        var (root, _, problems) = Merge(target, json, newTags);
        if (problems.Count > 0)
            throw new ModelFixException("Not saved, the model would have problems:\n" + string.Join("\n", problems.Take(12)));
        var backup = System.IO.Path.ChangeExtension(_file.Path, null) + $".before-{DateTime.Now:yyyyMMdd-HHmmss}.json";
        File.Copy(_file.Path, backup, overwrite: false);
        var tmp = _file.Path + ".part";
        File.WriteAllText(tmp, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
        File.Move(tmp, _file.Path, overwrite: true);
        return new { saved = true, backup, restart = true };
    }

    /// <summary>Ki proposes a corrected entry. Nothing is saved.</summary>
    public async Task<object> AskAsync(ModelEntryRequest req, CancellationToken ct)
    {
        if (!CanAsk) throw new ModelFixException("Ki needs a Claude key, and none is set yet (Setup > Settings).", 503);
        var (array, key, id) = ParseTarget(req.Target);
        var root = LoadRoot();
        var current = req.Json is { Length: > 0 } j ? j : Find(root, array, key, id)?.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) ?? "(no entry yet)";
        var findings = Findings(req.Target!);

        // Words to find the program part: the entry's tag symbols (DB and member names), its location, its id
        var model = root.Deserialize<MachineModel>(MachineModel.JsonOptions) ?? new MachineModel();
        var tagIds = Identifiers(current).Where(x => model.Tags.Any(t => t.Id == x)).ToHashSet();
        var words = new List<string> { id };
        foreach (var t in model.Tags.Where(t => tagIds.Contains(t.Id)))
            words.AddRange(t.Symbol.Split('.', '"', '[', ']').Select(p => p.Trim()).Where(p => p.Length >= 3));
        foreach (var m in System.Text.RegularExpressions.Regex.Matches(current, "\"location\"\\s*:\\s*\"([^\"]*)\"").Cast<System.Text.RegularExpressions.Match>())
            words.AddRange(m.Groups[1].Value.Split(new[] { ' ', ',', '(', ')', '\\', '"' }, StringSplitOptions.RemoveEmptyEntries).Where(w => w.Length >= 4));

        var (export, plc, error) = _program.Loaded;
        var digest = export != null && plc != null ? ProgramDigest.Focused(export, plc, words) : $"(The program is not loaded: {error})";

        var snap = _live.Snapshot;
        var live = new StringBuilder();
        foreach (var t in model.Tags.Where(t => tagIds.Contains(t.Id)))
            live.AppendLine($"{t.Id} = {ValueFormat.Format(t, snap?.Values.GetValueOrDefault(t.Id))}");

        var sb = new StringBuilder();
        sb.AppendLine("<program>").AppendLine(digest).AppendLine("</program>").AppendLine();
        sb.AppendLine("<model_tags>");
        foreach (var t in model.Tags) sb.AppendLine($"{t.Id} = {t.Symbol} : {t.Type}" + (t.Label != null ? $" // {t.Label}" : ""));
        sb.AppendLine("</model_tags>").AppendLine();
        sb.AppendLine($"The entry to correct ({array}, {key} \"{id}\"):").AppendLine("<entry>").AppendLine(current).AppendLine("</entry>").AppendLine();
        if (findings.Count > 0)
        {
            sb.AppendLine("What PLC-Ki saw on the live machine that this entry does not explain:");
            foreach (var f in findings) sb.AppendLine($"- {f.Title} ({f.Count} times): {f.Detail}");
            sb.AppendLine();
        }
        if (live.Length > 0) sb.AppendLine("Live values now:").Append(live).AppendLine();
        if (req.Note is { Length: > 0 } note) sb.AppendLine("What the engineer says is wrong:").AppendLine(note.Trim()).AppendLine();
        sb.AppendLine("""
Correct this one entry so it matches the program and what the machine did. Keep what is right; change only what is wrong.
Answer with one JSON object and nothing else:
{ "entry": { ...the complete corrected entry... }, "newTags": [ ...tags the entry needs that are not in model_tags, same format as model tags... ], "why": "two or three plain sentences: what was wrong and what you changed" }
""");

        var client = _client ??= new AnthropicClient();
        var messages = new List<BetaMessageParam> { new BetaUserMessageParam { Content = sb.ToString() } };
        for (int round = 1; round <= 2; round++)
        {
            var text = await CallAsync(client, messages, ct);
            var json = ModelBuilder.ExtractJson(text);
            JsonObject? answer = json != null ? JsonNode.Parse(json) as JsonObject : null;
            var entry = answer?["entry"] as JsonObject;
            var newTags = answer?["newTags"] as JsonArray;
            List<string> problems;
            string entryJson = entry?.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }) ?? "";
            if (entry == null) problems = new() { "The answer had no \"entry\" object." };
            else
            {
                try { problems = Merge(req.Target, entryJson, newTags).Problems; }
                catch (ModelFixException ex) { problems = new() { ex.Message }; }
            }
            if (problems.Count == 0 || round == 2)
                return new
                {
                    json = entryJson,
                    newTags = newTags?.ToJsonString() ?? "[]",
                    why = answer?["why"]?.GetValue<string>() ?? "",
                    problems,
                };
            messages = new List<BetaMessageParam> { new BetaUserMessageParam { Content = sb.ToString() + "\nYour last answer:\n<draft>\n" + text +
                "\n</draft>\nChecking it against the program found:\n" + string.Join("\n", problems.Select(p => "- " + p)) + "\nFix them and answer again in the same format." } };
        }
        throw new ModelFixException("Ki did not answer.", 502);
    }

    private async Task<string> CallAsync(AnthropicClient client, List<BetaMessageParam> messages, CancellationToken ct)
    {
        var parameters = new MessageCreateParams
        {
            Model = _ask.Model,
            MaxTokens = 32000,
            System = new List<BetaTextBlockParam> { new() { Text = ModelPrompt.System, CacheControl = new BetaCacheControlEphemeral() } },
            Messages = messages,
            OutputConfig = new BetaOutputConfig { Effort = AskService.ParseEffort("medium") },
            Betas = [FallbackBeta],
            Fallbacks = new Default(),
        };
        var text = new StringBuilder();
        await foreach (var ev in client.Beta.Messages.CreateStreaming(parameters, ct))
            if (ev.TryPickContentBlockDelta(out var d) && d.Delta.TryPickText(out var t))
                text.Append(t.Text);
        return text.ToString();
    }

    private static IEnumerable<string> Identifiers(string json) =>
        System.Text.RegularExpressions.Regex.Matches(json, "[A-Za-z_][A-Za-z0-9_]*").Select(m => m.Value).Distinct();
}
