using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using Xunit;

/// <summary>
/// PLC-Ki must never write to a PLC or change its state. The driver package has such functions behind a
/// WriteEnabled gate, so this test reads the compiled server (without running it) and fails if the app
/// calls any of them or touches the gate.
/// </summary>
public class ReadOnlyTests
{
    private static readonly HashSet<string> Forbidden = new(StringComparer.Ordinal)
    {
        "set_WriteEnabled",
        "WriteAsync", "WriteValues", "WriteTags", "SetPlcOperatingState",
        "StartCpuAsync", "StopCpuAsync",
        "OpenTisTraceAsync", "ActivateTraceAsync", "DeactivateTraceAsync", "DeleteTraceAsync",
    };

    private static string ServerDll()
    {
        // tests/PlcKi.Tests/bin/<Config>/net8.0 -> src/PlcKi.Server/bin/<Config>/net8.0
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        var config = dir.Parent!.Name;
        var root = dir.Parent!.Parent!.Parent!.Parent!.Parent!.FullName;
        return Path.Combine(root, "src", "PlcKi.Server", "bin", config, "net8.0", "PlcKi.Server.dll");
    }

    private static List<string> MemberReferenceNames(string path)
    {
        using var stream = File.OpenRead(path);
        using var pe = new PEReader(stream);
        var md = pe.GetMetadataReader();
        return md.MemberReferences.Select(h => md.GetString(md.GetMemberReference(h).Name)).Distinct().ToList();
    }

    [Fact]
    public void Server_calls_no_write_or_cpu_control_functions()
    {
        var path = ServerDll();
        Assert.True(File.Exists(path), $"Build the server first: {path}");
        var names = MemberReferenceNames(path);
        Assert.Contains("ReadAsync", names);   // sanity: we are looking at the right code
        var bad = names.Where(n => Forbidden.Contains(n) || (n.StartsWith("Write", StringComparison.Ordinal) && n != "WriteLine")).ToList();
        Assert.Empty(bad);
    }
}
