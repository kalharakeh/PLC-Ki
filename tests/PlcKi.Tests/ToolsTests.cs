using System.Text.Json;
using PlcKi.Core.Ask;
using PlcKi.Core.Status;
using Xunit;

/// <summary>The lookups the assistant can make, run on the demo model and snapshot values.</summary>
public class ToolsTests
{
    private static (MachineTools Tools, MachineHistory History) Make(string snapshot)
    {
        var history = new MachineHistory();
        var snap = TestData.Snapshot(snapshot);
        var status = TestData.Evaluate(snap);
        history.Add(snap, status);
        var memory = new PlcKi.Core.Memory.AlarmMemory(null);
        memory.Observe(status);
        return (new MachineTools(TestData.Model, () => new LiveView(status, snap, history, DateTimeOffset.Now), memory), history);
    }

    private static JsonElement Call(MachineTools tools, string name, object? input = null) =>
        JsonDocument.Parse(tools.Run(name, JsonSerializer.SerializeToElement(input ?? new { }))).RootElement;

    [Fact]
    public void Every_tool_has_a_schema_and_answers_without_error()
    {
        var (tools, _) = Make("step30-agitator-blocked");
        var inputs = new Dictionary<string, object>
        {
            ["get_live_status"] = new { },
            ["read_tags"] = new { ids = new[] { "Level" } },
            ["get_tag_history"] = new { ids = new[] { "Level" } },
            ["get_recent_events"] = new { },
            ["get_sequence"] = new { },
            ["get_equipment"] = new { id = "M201" },
            ["get_alarm"] = new { id = "A02" },
            ["get_alarm_history"] = new { id = "A02" },
            ["search_program"] = new { query = "Level" },
            ["lookup_siemens_diagnostic"] = new { query = "IO device failure" },
        };
        Assert.Equal(inputs.Keys.OrderBy(k => k), MachineTools.Specs.Select(s => s.Name).OrderBy(k => k));
        foreach (var spec in MachineTools.Specs)
        {
            Assert.All(spec.Required, r => Assert.Contains(r, spec.Properties.Keys));
            var result = Call(tools, spec.Name, inputs[spec.Name]);
            Assert.False(result.TryGetProperty("error", out var err), $"{spec.Name}: {err}");
        }
    }

    [Fact]
    public void Equipment_found_by_name_shows_the_blocking_interlock()
    {
        var (tools, _) = Make("step30-agitator-blocked");
        var r = Call(tools, "get_equipment", new { id = "agitator" });
        Assert.Equal("M201", r.GetProperty("definition").GetProperty("id").GetString());
        var blocking = r.GetProperty("now").GetProperty("blocking");
        Assert.Contains("20 %", blocking[0].GetProperty("text").GetString());
        Assert.Contains(r.GetProperty("definition").GetProperty("permits").EnumerateArray(),
            p => p.GetProperty("programCondition").GetString()!.Contains("Level"));
    }

    [Fact]
    public void Alarm_shows_cause_fix_and_whether_it_is_active()
    {
        var (tools, _) = Make("step10-xv101-not-open");
        var r = Call(tools, "get_alarm", new { id = "a02" });
        Assert.True(r.GetProperty("activeNow").GetBoolean());
        Assert.NotEmpty(r.GetProperty("fix").EnumerateArray());
        Assert.Contains("XV101", r.GetProperty("location").GetString());

        var byText = Call(tools, "get_alarm", new { id = "did not open" });
        Assert.Equal("A02", byText.GetProperty("id").GetString());
    }

    [Fact]
    public void Read_tags_by_search_and_unknown_ids()
    {
        var (tools, _) = Make("step30-agitator-blocked");
        var r = Call(tools, "read_tags", new { ids = new[] { "Nope" }, search = "tank level" });
        var tag = Assert.Single(r.GetProperty("tags").EnumerateArray());
        Assert.Equal("Level", tag.GetProperty("id").GetString());
        Assert.Equal("15.0 %", tag.GetProperty("value").GetString());
        Assert.Equal("Nope", r.GetProperty("unknown")[0].GetString());
    }

    [Fact]
    public void Search_finds_steps_and_interlocks_that_use_a_tag()
    {
        var (tools, _) = Make("step30-mix-heat");
        var hits = Call(tools, "search_program", new { query = "Level" }).GetProperty("hits").EnumerateArray().ToList();
        Assert.Contains(hits, h => h.GetProperty("kind").GetString() == "interlock" && h.GetProperty("id").GetString() == "M201");
        Assert.Contains(hits, h => h.GetProperty("kind").GetString() == "step condition");
    }

    [Fact]
    public void Sequence_definition_lists_every_step_with_the_current_one()
    {
        var (tools, _) = Make("step50-drain");
        var r = Call(tools, "get_sequence");
        Assert.Equal(7, r.GetProperty("definition").GetProperty("steps").GetArrayLength());
        Assert.Equal(50, r.GetProperty("now").GetProperty("step").GetInt32());
    }

    [Fact]
    public void Bad_input_comes_back_as_an_error_not_an_exception()
    {
        var (tools, _) = Make("step50-drain");
        Assert.True(Call(tools, "no_such_tool").TryGetProperty("error", out _));
        Assert.True(Call(tools, "get_equipment", new { id = "teleporter" }).TryGetProperty("error", out _));
        Assert.True(Call(tools, "read_tags").TryGetProperty("error", out _));
    }

    [Fact]
    public void Model_index_names_everything()
    {
        var (tools, _) = Make("step50-drain");
        var index = tools.ModelIndex();
        Assert.Contains("A25 (", index);
        Assert.Contains("M201: ", index);
        Assert.Contains("step 50: ", index);
    }
}
