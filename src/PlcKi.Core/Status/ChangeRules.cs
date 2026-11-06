using PlcKi.Core.Hardware;
using PlcKi.Core.Import;

namespace PlcKi.Core.Status;

/// <summary>Change once (a write to the PLC) or Hold (a simulated input set in PLCSIM, which stays).</summary>
public enum ChangeKind { Change, Hold }

/// <summary>
/// Where the value would change. Plcsim means confirmed by the TIA helper; anything PLC-Ki cannot confirm as PLCSIM
/// is a real PLC, on Setup's list of real PLCs whose values may be changed or not.
/// </summary>
public enum ChangeTarget { Plcsim, RealAllowed, RealNotAllowed }

/// <summary>
/// The answer for one value. Refused is null when it may be changed; then Rule names the row of the rule table
/// that allows it (it goes into the change log), and Warning, when set, must be acknowledged ("I understand") first.
/// </summary>
public sealed record ChangeDecision(string? Refused, string? Rule, string? Warning, string? Address, string Datatype)
{
    public bool Allowed => Refused == null;
    public static ChangeDecision No(string why, string? address = null, string datatype = "") => new(why, null, null, address, datatype);
}

/// <summary>
/// What PLC-Ki may change, in one place for "Change a value" and "Hold on PLCSIM", so the two cannot disagree.
///
///   Confirmed PLCSIM: inputs (change and hold), outputs (change once), memory and data block values (change),
///                     also safety inputs and values the safety program reads, with a warning to acknowledge.
///   Real PLC on the allowed list: standard memory and data block values (with a reason and the address typed again).
///                     Never inputs, outputs, or a value the safety program reads.
///   Never: F-blocks and their data (F-DBs, instance data of F-FBs, F-runtime blocks), or without a loaded program.
///
/// When the TIA helper cannot confirm PLCSIM, the real-PLC rules apply, and a refusal says why PLCSIM was not confirmed.
/// </summary>
public static class ChangeRules
{
    public const string FInput = "This is a safety (F-) input. Overwriting it makes the F-module fault in PLCSIM until the safety program acknowledges it.";
    public const string FOutput = "This is a safety (F-) output. The safety program rewrites it every cycle, and overwriting it can make the F-module fault in PLCSIM.";
    public const string SafetyReads = "The safety program reads this value.";
    public const string SafetyReadsReal = "The safety program reads this value. PLC-Ki never changes it on a real machine.";
    public static string Unchecked(string what) => $"PLC-Ki can't tell whether this {what} is on a safety (F-) module. Import the hardware to check.";

    /// <summary>
    /// Is the PLC PLC-Ki reads the TIA helper's PLCSIM instance? The helper must have that address as its PLCSIM
    /// address, find a running instance there without an error, and when both RUN/STOP states are known they must
    /// agree. Returns why not (in the helper's words where it has them), or null when it is confirmed.
    /// </summary>
    public static string? PlcsimNotConfirmed(string plc, bool applies, bool found, string? instance, string? why, string? error,
        string? plcState, string? simState)
    {
        if (!applies) return string.IsNullOrWhiteSpace(why) ? $"it reports no PLCSIM CPU at {plc}" : why.Trim();
        if (!found && !string.IsNullOrWhiteSpace(error)) return "it could not read PLCSIM just now: " + error.Trim();
        if (!found || string.IsNullOrWhiteSpace(instance)) return $"it finds no running PLCSIM instance at {plc}";
        if (!string.IsNullOrWhiteSpace(error)) return "it reports a PLCSIM problem: " + error.Trim();
        var a = RunStop(plcState);
        var b = RunStop(simState);
        if (a != null && b != null && a != b)
            return $"PLC-Ki reads a CPU in {a} at {plc}, but the PLCSIM instance \"{instance}\" is in {b}, so they may not be the same CPU";
        return null;
    }

    /// <summary>The start of every refusal that comes from an unconfirmed PLCSIM.</summary>
    public static string CannotConfirm(string notConfirmed) => $"PLC-Ki can't confirm this is PLCSIM (TIA helper: {notConfirmed.TrimEnd('.')}).";

    private static string? RunStop(string? state) => (state ?? "").Trim().ToUpperInvariant() switch
    {
        "RUN" => "RUN",
        "STOP" => "STOP",
        _ => null,   // starting, unknown, not read: no comparison
    };

    /// <summary>
    /// May this value be changed (or held), and how? <paramref name="hw"/> is the hardware export (null when none is
    /// loaded); <paramref name="notConfirmed"/> is PlcsimNotConfirmed's answer when the target is not PLCSIM.
    /// </summary>
    public static ChangeDecision Decide(ExportPlc? plc, HardwareConfig? hw, string? symbol, ChangeKind kind, ChangeTarget target, string? notConfirmed = null)
    {
        var s = (symbol ?? "").Trim();
        if (s.Length == 0) return ChangeDecision.No("No tag given.");
        if (plc == null) return ChangeDecision.No("No program is loaded, so PLC-Ki cannot tell what this value is or whether the safety program uses it.");
        if (WriteRules.SafetyBlockReason(plc, s) is { } block) return ChangeDecision.No(block);

        var text = WriteRules.Address(plc, s);
        var a = HoldRules.ParseAddress(text);
        var area = a?.Area ?? AreaOf(text);   // 'I', 'Q', 'M' (also timers, counters), or '\0' for a data block value
        var datatype = plc.PlcTags.FirstOrDefault(t => t.Name.Equals(WriteRules.Root(s), StringComparison.OrdinalIgnoreCase))?.Datatype ?? "";
        var user = WriteRules.SafetyUser(plc, s);

        if (target != ChangeTarget.Plcsim)
        {
            var real = Real(plc, s, text, area, kind, target, user);
            if (real.Allowed || notConfirmed == null) return real;
            // Would PLCSIM have allowed it? Then the real refusal starts with why PLCSIM was not confirmed.
            var sim = Decide(plc, hw, s, kind, ChangeTarget.Plcsim);
            return sim.Allowed ? real with { Refused = CannotConfirm(notConfirmed) + " " + real.Refused } : real;
        }

        if (kind == ChangeKind.Hold && HoldRefusal(plc, s, text, a, area, datatype) is { } no) return ChangeDecision.No(no, a?.Text ?? text, datatype);

        if (area is 'I' or 'Q')
        {
            var fModule = hw != null && a != null ? HoldRules.FModule(hw, a) : null;
            var hwUnknown = hw == null && plc.Blocks.Any(b => b.IsSafety);
            var (warning, rule) = IoWarning(area, fModule, user, hwUnknown);
            return new(null, rule, warning, a?.Text ?? text, datatype);
        }
        if (user != null) return new(null, "plcsim-safety-read", SafetyReads, text, datatype);
        return new(null, area == 'M' ? "plcsim-memory" : "plcsim-data", null, text, datatype);
    }

    /// <summary>
    /// The warning for an input or output on PLCSIM, and the rule that goes into the change log.
    ///   fModule: the F-module whose address range covers it ("F-DI 8x24VDC HF in ET200SP_1"), or null.
    ///   safetyUser: the safety block that uses the tag, or null.
    ///   hwUnknown: no hardware export is loaded and the program has a safety program, so fModule cannot be known.
    /// Rules: plcsim-input, plcsim-output, plcsim-safety-input, plcsim-safety-output, plcsim-input-unchecked,
    /// plcsim-output-unchecked, plcsim-safety-read. Warnings: FInput, FOutput, SafetyReads, Unchecked("input"/"output").
    /// </summary>
    private static (string? Warning, string Rule) IoWarning(char area, string? fModule, string? safetyUser, bool hwUnknown)
    {
        var input = area == 'I';
        // One warning only, the strongest: on an F-module, then "cannot tell" (it may be on one), then read by safety
        if (fModule != null) return input ? (FInput, "plcsim-safety-input") : (FOutput, "plcsim-safety-output");
        if (hwUnknown) return input ? (Unchecked("input"), "plcsim-input-unchecked") : (Unchecked("output"), "plcsim-output-unchecked");
        if (safetyUser != null) return (SafetyReads, "plcsim-safety-read");
        return (null, input ? "plcsim-input" : "plcsim-output");
    }

    /// <summary>A real PLC: only standard memory and data block values, and only when it is on the allowed list.</summary>
    private static ChangeDecision Real(ExportPlc plc, string s, string? text, char area, ChangeKind kind, ChangeTarget target, string? user)
    {
        if (kind == ChangeKind.Hold)
            return ChangeDecision.No("Hold works on PLCSIM only. On a real PLC only a force from TIA Portal holds an input or output.", text);
        if (target == ChangeTarget.RealNotAllowed)
            return ChangeDecision.No("This is a real PLC (or PLC-Ki cannot tell that it is PLCSIM), and it is not on the list of real PLCs whose values may be changed. " +
                "An administrator can add it under Setup, Connections, More PLC options.", text);
        if (user != null) return ChangeDecision.No(SafetyReadsReal, text);
        if (area is 'I' or 'Q') return ChangeDecision.No(WriteRules.RealIoReason(text)!, text);
        return new(null, area == 'M' ? "real-memory" : "real-data", null, text, "");
    }

    /// <summary>Why this cannot be held on PLCSIM (only inputs of a plain type), or null.</summary>
    private static string? HoldRefusal(ExportPlc plc, string s, string? text, IoAddress? a, char area, string datatype)
    {
        if (text == null)
        {
            var root = WriteRules.Root(s);
            var known = plc.Blocks.Any(b => b.Name.Equals(root, StringComparison.OrdinalIgnoreCase))
                        || plc.PlcTags.Any(t => t.Name.Equals(root, StringComparison.OrdinalIgnoreCase));
            return known ? $"{s} is a data block value, not an input. Only inputs can be held on PLCSIM; change it once instead (the program may set it again)."
                         : $"{s} is not in the loaded program, so PLC-Ki cannot tell what it is.";
        }
        if (area == 'Q')
            return $"{text} is an output. The program rewrites outputs every cycle, and PLCSIM only keeps inputs, so an output cannot be held. Change it once instead.";
        if (area != 'I')
            return $"{text} is a memory value, not an input. Only inputs can be held on PLCSIM; change it once instead (the program may set it again).";
        if (a == null) return $"{s} is at {text}, which is not a plain input address.";
        var size = HoldRules.SizeOf(datatype);
        if (size < 0) return $"PLC-Ki holds only Bool, Byte, Word, Int, DWord, DInt and Real inputs, not {datatype}.";
        if ((size == 0) != (a.Bit != null) || (size > 0 && size != a.Size))
            return $"The address {text} does not fit the type {datatype}.";
        return null;
    }

    /// <summary>The area of an address HoldRules.ParseAddress does not take (%T5, %C2, %I4 without a size), or '\0' for none.</summary>
    private static char AreaOf(string? address)
    {
        if (string.IsNullOrWhiteSpace(address)) return '\0';
        return char.ToUpperInvariant(address.TrimStart('%').TrimStart()[0]) switch
        {
            'I' or 'E' => 'I',
            'Q' or 'A' => 'Q',
            _ => 'M',
        };
    }
}
