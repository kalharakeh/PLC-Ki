using System.Text.Json;
using System.Text.RegularExpressions;
using PlcKi.Server.Live;
using Xunit;

/// <summary>Holding a simulated input happens in one place only, and the change log still reads its old lines.</summary>
public class HoldTests
{
    private static string Root()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "PlcKi.sln"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("PlcKi.sln not found above the test folder.");
    }

    [Fact]
    public void Only_PlcsimInputs_sets_simulated_inputs()
    {
        var src = Path.Combine(Root(), "src");
        var callers = Directory.GetFiles(src, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .Where(f => Regex.IsMatch(File.ReadAllText(f), @"WritePlcsimInputAsync\s*\(|plcsim/input/write"))
            .Select(Path.GetFileName).OrderBy(n => n).ToList();
        // VmClient defines the call (and its path), PlcsimInputs is the only caller
        Assert.Equal(new[] { "PlcsimInputs.cs", "VmClient.cs" }, callers);
    }

    [Fact]
    public void Old_log_lines_read_as_changes()
    {
        var old = "{\"time\":\"2026-11-05T09:12:17.5627727+01:00\",\"user\":\"kassem\",\"plc\":\"192.168.10.10\",\"target\":\"plcsim\",\"symbol\":\"\\u0022DI_BluePB\\u0022\",\"value\":\"TRUE\",\"before\":\"FALSE\",\"after\":\"FALSE\",\"held\":false,\"reason\":null,\"ok\":true,\"error\":null}";
        var e = JsonSerializer.Deserialize<WriteLogEntry>(old, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.Equal("\"DI_BluePB\"", e.Symbol);
        Assert.Null(e.Action);
        Assert.Null(e.Address);
    }
}
