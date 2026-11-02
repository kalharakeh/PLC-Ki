using System.Reflection;
using Xunit;

/// <summary>The bundled driver must not offer any way to write to the PLC or change its mode.</summary>
public class ReadOnlyTests
{
    [Fact]
    public void Driver_has_no_public_write_or_mode_change_methods()
    {
        var asm = typeof(S7CommPlusDriver.S7CommPlusConnection).Assembly;
        var forbidden = new[] { "WriteValues", "WriteTags", "SetPlcOperatingState" };
        var found = asm.GetTypes()
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            .Where(m => forbidden.Contains(m.Name))
            .Select(m => m.DeclaringType!.Name + "." + m.Name)
            .ToList();
        Assert.Empty(found);
    }
}
