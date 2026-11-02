using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
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

    /// <summary>Every method or field the server uses from other assemblies, with the assembly it comes from.</summary>
    private static List<(string Name, string Assembly)> MemberReferences(string path)
    {
        using var stream = File.OpenRead(path);
        using var pe = new PEReader(stream);
        var md = pe.GetMetadataReader();

        string AssemblyOf(EntityHandle h)
        {
            switch (h.Kind)
            {
                case HandleKind.TypeReference:
                    var scope = md.GetTypeReference((TypeReferenceHandle)h).ResolutionScope;
                    return scope.Kind switch
                    {
                        HandleKind.AssemblyReference => md.GetString(md.GetAssemblyReference((AssemblyReferenceHandle)scope).Name),
                        HandleKind.TypeReference => AssemblyOf(scope),   // nested type
                        _ => "",
                    };
                case HandleKind.TypeSpecification:
                    // Generic instance: the first coded handle after the GENERICINST/CLASS bytes is the open type
                    var blob = md.GetBlobReader(md.GetTypeSpecification((TypeSpecificationHandle)h).Signature);
                    if (blob.ReadSignatureTypeCode() == SignatureTypeCode.GenericTypeInstance)
                    {
                        blob.ReadSignatureTypeCode();
                        return AssemblyOf(blob.ReadTypeHandle());
                    }
                    return "";
                default:
                    return "";
            }
        }

        return md.MemberReferences.Select(h => md.GetMemberReference(h))
            .Select(r => (md.GetString(r.Name), AssemblyOf(r.Parent))).Distinct().ToList();
    }

    [Fact]
    public void Server_calls_no_write_or_cpu_control_functions()
    {
        var path = ServerDll();
        Assert.True(File.Exists(path), $"Build the server first: {path}");
        var refs = MemberReferences(path);
        var driver = refs.Where(r => r.Assembly.Contains("S7CommPlus", StringComparison.OrdinalIgnoreCase)).ToList();
        Assert.Contains(driver, r => r.Name == "ReadAsync");   // sanity: we are looking at the right code and assembly
        // Named write and CPU-control functions are banned from any assembly; from the driver, anything that starts with Write
        var bad = refs.Where(r => Forbidden.Contains(r.Name)).Concat(driver.Where(r => r.Name.StartsWith("Write", StringComparison.Ordinal)))
            .Select(r => $"{r.Assembly}: {r.Name}").Distinct().ToList();
        Assert.Empty(bad);
    }
}
