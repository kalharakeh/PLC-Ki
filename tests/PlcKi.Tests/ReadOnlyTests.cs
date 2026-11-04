using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using Xunit;

/// <summary>
/// PLC-Ki reads the PLC; it never starts, stops or traces it. Changing a value is allowed in one place only:
/// PlcWriter, which WriteEndpoints calls after its checks (admin, safety program, PLCSIM or a confirmed real PLC).
/// This test reads the compiled server (without running it) and fails if CPU-control or trace functions are
/// used anywhere, or if any code other than PlcWriter opens the driver's write gate or calls a write function.
/// </summary>
public class ReadOnlyTests
{
    /// <summary>Never, anywhere.</summary>
    private static readonly HashSet<string> Forbidden = new(StringComparer.Ordinal)
    {
        "SetPlcOperatingState", "StartCpuAsync", "StopCpuAsync",
        "OpenTisTraceAsync", "ActivateTraceAsync", "DeactivateTraceAsync", "DeleteTraceAsync", "DeleteStoredTraceMeasurementAsync",
    };

    /// <summary>Only from PlcWriter (plus any driver member whose name starts with Write).</summary>
    private static readonly HashSet<string> WriteOnly = new(StringComparer.Ordinal)
    {
        "set_WriteEnabled", "WriteAsync", "WriteValues", "WriteTags",
    };

    private const string Writer = "PlcKi.Server.Live.PlcWriter";

    private static string ServerDll()
    {
        // tests/PlcKi.Tests/bin/<Config>/net8.0 -> src/PlcKi.Server/bin/<Config>/net8.0
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        var config = dir.Parent!.Name;
        var root = dir.Parent!.Parent!.Parent!.Parent!.Parent!.FullName;
        return Path.Combine(root, "src", "PlcKi.Server", "bin", config, "net8.0", "PlcKi.Server.dll");
    }

    /// <summary>Every method or field the server uses from other assemblies, with the assembly it comes from.</summary>
    private static Dictionary<MemberReferenceHandle, (string Name, string Assembly)> MemberReferences(MetadataReader md)
    {

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

        return md.MemberReferences.ToDictionary(h => h, h =>
        {
            var r = md.GetMemberReference(h);
            return (md.GetString(r.Name), AssemblyOf(r.Parent));
        });
    }

    /// <summary>For every method of the server: its type (outer type for compiler-made nested types) and the members it calls.</summary>
    private static List<(string Type, string Method, string Member, string Assembly)> Calls(string path)
    {
        using var stream = File.OpenRead(path);
        using var pe = new PEReader(stream);
        var md = pe.GetMetadataReader();
        var refs = MemberReferences(md);
        // IL operand sizes from the runtime's own opcode table
        var opcodes = typeof(System.Reflection.Emit.OpCodes).GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
            .Select(f => (System.Reflection.Emit.OpCode)f.GetValue(null)!).ToDictionary(o => (ushort)o.Value, o => o);
        string Outer(TypeDefinitionHandle h)
        {
            var t = md.GetTypeDefinition(h);
            var outer = t.GetDeclaringType();
            if (!outer.IsNil) return Outer(outer);
            var ns = md.GetString(t.Namespace);
            return (ns.Length > 0 ? ns + "." : "") + md.GetString(t.Name);
        }
        var list = new List<(string, string, string, string)>();
        foreach (var th in md.TypeDefinitions)
        {
            var type = Outer(th);
            foreach (var mh in md.GetTypeDefinition(th).GetMethods())
            {
                var m = md.GetMethodDefinition(mh);
                if (m.RelativeVirtualAddress == 0) continue;
                var il = pe.GetMethodBody(m.RelativeVirtualAddress).GetILBytes()!;
                for (int i = 0; i < il.Length;)
                {
                    ushort code = il[i];
                    if (code == 0xFE) { code = (ushort)(0xFE00 | il[i + 1]); i += 2; } else i += 1;
                    var op = opcodes[code];
                    int size = op.OperandType switch
                    {
                        System.Reflection.Emit.OperandType.InlineNone => 0,
                        System.Reflection.Emit.OperandType.ShortInlineBrTarget or System.Reflection.Emit.OperandType.ShortInlineI or System.Reflection.Emit.OperandType.ShortInlineVar => 1,
                        System.Reflection.Emit.OperandType.InlineVar => 2,
                        System.Reflection.Emit.OperandType.InlineI8 or System.Reflection.Emit.OperandType.InlineR => 8,
                        System.Reflection.Emit.OperandType.InlineSwitch => 4 + 4 * BitConverter.ToInt32(il, i),
                        _ => 4,
                    };
                    if (op.OperandType is System.Reflection.Emit.OperandType.InlineMethod or System.Reflection.Emit.OperandType.InlineTok or System.Reflection.Emit.OperandType.InlineField)
                    {
                        var token = BitConverter.ToInt32(il, i);
                        var handle = MetadataTokens.EntityHandle(token);
                        if (handle.Kind == HandleKind.MethodSpecification) handle = md.GetMethodSpecification((MethodSpecificationHandle)handle).Method;
                        if (handle.Kind == HandleKind.MemberReference && refs.TryGetValue((MemberReferenceHandle)handle, out var r))
                            list.Add((type, md.GetString(m.Name), r.Name, r.Assembly));
                    }
                    i += size;
                }
            }
        }
        return list;
    }

    [Fact]
    public void Server_never_controls_the_cpu_and_writes_only_from_PlcWriter()
    {
        var path = ServerDll();
        Assert.True(File.Exists(path), $"Build the server first: {path}");
        var calls = Calls(path);
        bool Driver(string asm) => asm.Contains("S7CommPlus", StringComparison.OrdinalIgnoreCase);
        Assert.Contains(calls, c => Driver(c.Assembly) && c.Member == "ReadAsync");   // sanity: we are looking at the right code and assembly
        Assert.Contains(calls, c => c.Type == Writer && Driver(c.Assembly) && c.Member == "WriteAsync");   // and the IL scan finds calls

        var cpu = calls.Where(c => Forbidden.Contains(c.Member)).Select(c => $"{c.Type}.{c.Method}: {c.Member}").Distinct().ToList();
        Assert.Empty(cpu);

        var writes = calls.Where(c => WriteOnly.Contains(c.Member) || (Driver(c.Assembly) && c.Member.StartsWith("Write", StringComparison.Ordinal)))
            .Where(c => c.Type != Writer).Select(c => $"{c.Type}.{c.Method}: {c.Member} ({c.Assembly})").Distinct().ToList();
        Assert.Empty(writes);
    }
}
