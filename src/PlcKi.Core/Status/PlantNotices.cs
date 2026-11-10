using System.Text.RegularExpressions;
using PlcKi.Core.Import;

namespace PlcKi.Core.Status;

/// <summary>A notice shown on every page while it is true: test switches on, or the PLC's blocks differ from the import.</summary>
/// <param name="Kind">"test" or "program".</param>
/// <param name="Text">One plain sentence.</param>
/// <param name="Items">What it is about, short (switch names with their comments, block names).</param>
/// <param name="Link">Where to look, a page address, or null.</param>
public sealed record PlantNotice(string Kind, string Text, List<string> Items, string? Link = null);

/// <summary>One on/off value in a test data block: "DB_FaultInjection".XV101_StuckClosed with its comment.</summary>
public sealed record TestSwitch(string Symbol, string Block, string Name, string? Comment);

/// <summary>The test switches found in a program, and whether the limits cut the list.</summary>
public sealed record TestSwitchSet(List<TestSwitch> Switches, List<string> Blocks, List<string> Skipped)
{
    public static readonly TestSwitchSet None = new(new(), new(), new());
}

/// <summary>
/// Finds the test and simulation switches of a program, read with each poll:
/// - every on/off (Bool) member of a global data block whose name or title says test or fault injection, such as the
///   demo's "DB_FaultInjection" ("Fault injection (testing only)");
/// - in any global data block, an on/off member named like a simulation switch: SimActive, Simulation_On, EnableSim.
/// A block named or titled "simulated" is not enough: the demo's DB_IO ("Field signals (simulated)") holds the
/// machine's signals, not switches. Instance data blocks are left out: they belong to a block's own logic.
/// </summary>
public static class TestSwitches
{
    /// <summary>At most this many blocks and switches are read, so a program full of test blocks does not slow the poll.</summary>
    public const int MaxBlocks = 4, MaxSwitches = 64;

    // In a name, the word may follow "DB_" or a lower-case letter must not come before it ("Latest", "Contest" do not count)
    private static readonly Regex NameRx = new(@"(?<![a-z])(test|fault_?inject)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex TitleRx = new(@"\b(test|testing|tests|fault injection)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    // The whole member name: Sim, SimActive, Sim_On, SimulationMode, Simulation_Enabled, EnableSim, UseSimulation
    private static readonly Regex SimSwitchRx = new(@"^((sim|simulation)_?(on|active|mode|enable|enabled|run)?|(enable|use|activate)_?(sim|simulation))$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static bool IsTestBlock(ExportBlock b) =>
        b.Kind == "GlobalDB" && (NameRx.IsMatch(b.Name) || (b.Title != null && TitleRx.IsMatch(b.Title)));

    public static bool IsSimSwitch(string memberName) => SimSwitchRx.IsMatch(memberName);

    public static TestSwitchSet Find(ExportPlc? plc)
    {
        if (plc == null) return TestSwitchSet.None;
        // Test blocks first, then the other global data blocks for their simulation switches
        var blocks = plc.Blocks.Where(b => b.Kind == "GlobalDB")
            .OrderBy(b => IsTestBlock(b) ? 0 : 1).ThenBy(b => b.Name, StringComparer.OrdinalIgnoreCase).ToList();
        var switches = new List<TestSwitch>();
        var used = new List<string>();
        var skipped = new List<string>();
        foreach (var b in blocks)
        {
            var found = new List<TestSwitch>();
            foreach (var m in b.Interface.Where(m => m.Section is "Static" or ""))
                Collect(b.Name, m, "", found, depth: 0);
            if (!IsTestBlock(b)) found = found.Where(s => IsSimSwitch(s.Name.Split('.')[^1])).ToList();
            if (found.Count == 0) continue;
            if (used.Count >= MaxBlocks || switches.Count >= MaxSwitches) { skipped.Add(b.Name); continue; }
            var room = MaxSwitches - switches.Count;
            if (found.Count > room) { found = found.Take(room).ToList(); skipped.Add(b.Name + " (in part)"); }
            switches.AddRange(found);
            used.Add(b.Name);
        }
        return new TestSwitchSet(switches, used, skipped);
    }

    /// <summary>Bool members, and those one struct deep ("Valves.XV101_Stuck").</summary>
    private static void Collect(string block, ExportMember m, string prefix, List<TestSwitch> into, int depth)
    {
        var name = prefix + m.Name;
        if (string.Equals(m.Datatype, "Bool", StringComparison.OrdinalIgnoreCase))
            into.Add(new TestSwitch($"\"{block}\".{name}", block, name, string.IsNullOrWhiteSpace(m.Comment) ? null : m.Comment.Trim()));
        else if (depth == 0 && m.Members.Count > 0)
            foreach (var c in m.Members) Collect(block, c, name + ".", into, depth + 1);
    }

    /// <summary>"A test switch is on in the PLC ...", with "XV101_StuckClosed: Valve XV101 does not open". Null when none is on.</summary>
    public static PlantNotice? Notice(TestSwitchSet set, IReadOnlyDictionary<string, (object? Value, string? Error)> values)
    {
        var on = set.Switches.Where(s => values.TryGetValue(s.Symbol, out var v) && v.Value is true).ToList();
        if (on.Count == 0) return null;
        var text = on.Count == 1
            ? "A test switch is on in the PLC. A fault may be caused on purpose, not by the machine."
            : $"{on.Count} test switches are on in the PLC. Faults may be caused on purpose, not by the machine.";
        var blocks = on.Select(s => s.Block).Distinct().ToList();
        return new PlantNotice("test", text,
            on.Select(s => s.Comment != null ? $"{s.Name}: {s.Comment}" : s.Name).ToList(),
            blocks.Count == 1 ? "#/program/" + Uri.EscapeDataString(blocks[0]) : null);
    }
}

/// <summary>A block as the PLC lists it: name, kind (OB, FB, FC, DB, UDT) and programming language.</summary>
public sealed record PlcBlock(string Name, string Kind, string? Language);

/// <summary>
/// Compares the blocks in the PLC with the imported program. The PLC's list has no checksum and no change time,
/// so only blocks added or missing are seen, never a change inside a block: the notice says exactly that much.
/// </summary>
public static class ProgramDrift
{
    // Blocks the CPU, the safety program, technology objects and ProDiag make themselves: never in an export
    private static readonly HashSet<string> SystemLanguages = new(StringComparer.OrdinalIgnoreCase)
    {
        "CPU_DB", "CPU_SDB", "SDB", "TechnoDB", "Motion_DB", "ProDiag", "ProDiag_OB", "F_DB", "F_CALL", "F_STL", "F_LAD", "F_FBD",
        "F_LAD_LIB", "F_FBD_LIB", "S7_PDIAG", "RSE", "CEM", "UDT", "SDT",
    };

    private static string? KindOf(string exportKind) => exportKind switch
    {
        "OB" or "FB" or "FC" => exportKind,
        "GlobalDB" or "InstanceDB" or "ArrayDB" => "DB",
        _ => null,
    };

    /// <summary>Blocks in the PLC that the import does not have, and blocks of the import the PLC does not have.</summary>
    public static (List<string> Added, List<string> Missing) Compare(IReadOnlyList<PlcBlock> onPlc, ExportPlc plc)
    {
        var imported = plc.Blocks.Where(b => KindOf(b.Kind) != null)
            .Select(b => (b.Name, Kind: KindOf(b.Kind)!)).ToList();
        var importedKeys = imported.Select(b => b.Kind + ":" + b.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var plcKinds = new HashSet<string>(new[] { "OB", "FB", "FC", "DB" }, StringComparer.OrdinalIgnoreCase);
        var plcKeys = onPlc.Where(b => plcKinds.Contains(b.Kind)).Select(b => b.Kind + ":" + b.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Safety (F) and system blocks of the import are on the PLC too, so they still count as missing when absent
        var added = onPlc
            .Where(b => plcKinds.Contains(b.Kind) && !SystemLanguages.Contains(b.Language ?? "") && !importedKeys.Contains(b.Kind + ":" + b.Name))
            .Select(b => b.Name).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
        var missing = imported.Where(b => !plcKeys.Contains(b.Kind + ":" + b.Name))
            .Select(b => b.Name).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
        return (added, missing);
    }

    /// <summary>"2 blocks in the PLC aren't in the imported program." and/or "1 block of the imported program isn't in the PLC."</summary>
    public static PlantNotice? Notice(List<string> added, List<string> missing)
    {
        if (added.Count == 0 && missing.Count == 0) return null;
        var parts = new List<string>();
        if (added.Count > 0) parts.Add(added.Count == 1 ? "1 block in the PLC isn't in the imported program" : $"{added.Count} blocks in the PLC aren't in the imported program");
        if (missing.Count > 0) parts.Add(missing.Count == 1 ? "1 block of the imported program isn't in the PLC" : $"{missing.Count} blocks of the imported program aren't in the PLC");
        var text = string.Join(", and ", parts) + ". Import the program again, so the explanations match what runs.";
        var items = added.Take(8).Select(n => "In the PLC only: " + n)
            .Concat(missing.Take(8).Select(n => "In the import only: " + n)).ToList();
        return new PlantNotice("program", char.ToUpperInvariant(text[0]) + text[1..], items, "#/setup/program");
    }
}
