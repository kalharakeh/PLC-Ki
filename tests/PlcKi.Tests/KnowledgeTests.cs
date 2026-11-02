using System.Text.Json;
using PlcKi.Core.Ask;
using PlcKi.Core.Knowledge;
using Xunit;

/// <summary>The Siemens diagnostics knowledge base: the file is complete and typical questions find the right entry.</summary>
public class KnowledgeTests
{
    private static readonly SiemensDiagnostics Kb = SiemensDiagnostics.Default;
    private static readonly string[] Categories = { "CPU", "PROFINET", "Module", "Safety", "Drive", "Communication" };

    [Fact]
    public void Every_entry_is_complete_and_unique()
    {
        Assert.True(Kb.Entries.Count >= 50, $"only {Kb.Entries.Count} entries");
        Assert.Equal(Kb.Entries.Count, Kb.Entries.Select(e => e.Id).Distinct().Count());
        foreach (var e in Kb.Entries)
        {
            Assert.Contains(e.Category, Categories);
            Assert.False(string.IsNullOrWhiteSpace(e.Title), e.Id);
            Assert.False(string.IsNullOrWhiteSpace(e.Meaning), e.Id);
            Assert.NotEmpty(e.AppliesTo);
            Assert.True(e.Keywords.Count >= 2, $"{e.Id}: needs keywords to be found");
            Assert.NotEmpty(e.Checks);
            Assert.NotEmpty(e.Fixes);
        }
    }

    [Theory]
    [InlineData("F07900", "drive-f07900-motor-blocked")]
    [InlineData("drive shows F7900", "drive-f07900-motor-blocked")]
    [InlineData("OB86 was called", "cpu-rack-station-failure")]
    [InlineData("OB 121", "cpu-programming-error")]
    [InlineData("IO device failure ET200SP_1", "pn-device-failure")]
    [InlineData("Maximum cycle time exceeded", "cpu-cycle-time-exceeded")]
    [InlineData("wire break channel 3", "ch-wire-break")]
    [InlineData("analog input reads 32767", "ch-overflow-underflow")]
    [InlineData("F-module passivated after door opened", "safety-fio-passivated")]
    [InlineData("discrepancy error on the light curtain", "safety-fio-passivated")]
    [InlineData("the ERROR LED is flashing red", "cpu-leds")]
    [InlineData("load voltage missing on power module", "ch-missing-supply")]
    [InlineData("device name not assigned after replacing the drive", "pn-name-not-assigned")]
    [InlineData("SCADA can't read, PUT/GET", "comm-put-get")]
    [InlineData("memory card removed", "cpu-memory-card")]
    [InlineData("setpoint timeout on the conveyor drive", "drive-f01910-setpoint-timeout")]
    [InlineData("MRP ring open", "pn-mrp-ring-open")]
    public void Typical_questions_find_the_right_entry(string query, string expected)
    {
        var found = Kb.Search(query);
        Assert.NotEmpty(found);
        Assert.Equal(expected, found[0].Entry.Id);
    }

    [Fact]
    public void Category_filter_and_no_match()
    {
        Assert.All(Kb.Search("overtemperature", "Drive"), m => Assert.Equal("Drive", m.Entry.Category));
        Assert.Empty(Kb.Search("banana smoothie recipe"));
        Assert.Empty(Kb.Search("  "));
    }

    [Fact]
    public void Ask_tool_returns_entries_and_says_it_is_general_knowledge()
    {
        var tools = new MachineTools(TestData.Model, () => throw new InvalidOperationException("not needed"));
        var r = JsonDocument.Parse(tools.Run("lookup_siemens_diagnostic",
            JsonSerializer.SerializeToElement(new { query = "F30001 overcurrent" }))).RootElement;
        Assert.Contains("general Siemens knowledge", r.GetProperty("source").GetString());
        var first = r.GetProperty("matches")[0];
        Assert.Equal("drive-f30001-overcurrent", first.GetProperty("id").GetString());
        Assert.True(first.GetProperty("fixes").GetArrayLength() > 0);

        var none = JsonDocument.Parse(tools.Run("lookup_siemens_diagnostic",
            JsonSerializer.SerializeToElement(new { query = "banana smoothie" }))).RootElement;
        Assert.Equal(0, none.GetProperty("matches").GetArrayLength());
        Assert.True(none.TryGetProperty("note", out _));
    }
}
