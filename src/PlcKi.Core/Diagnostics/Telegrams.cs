namespace PlcKi.Core.Diagnostics;

/// <summary>The words a drive sends back in one PROFIdrive telegram, in order, and how many words the PLC sends.</summary>
public sealed record TelegramLayout(int Number, string[] In, int OutWords, string About);

/// <summary>One status word bit of a SINAMICS drive: what it means when it is 1, and whether 1 or 0 is the problem.</summary>
public sealed record StatusBit(int Bit, string Name, string WhenOn, string WhenOff, bool? ProblemWhen);

/// <summary>
/// PROFIdrive telegrams as SINAMICS drives use them, and the meaning of status word 1 (ZSW1). General PROFIdrive
/// and SINAMICS knowledge, not read from the machine: the words of a telegram are fixed by its number.
/// The second word of a 32-bit value (NIST_B, XIST_A) is marked "+".
/// </summary>
public static class Telegrams
{
    private static readonly Dictionary<int, TelegramLayout> Layouts = new()
    {
        [1] = new(1, new[] { "ZSW1", "NIST_A" }, 2, "Standard telegram 1: speed control, 16-bit speed"),
        [2] = new(2, new[] { "ZSW1", "NIST_B", "NIST_B+", "ZSW2" }, 4, "Standard telegram 2: speed control, 32-bit speed"),
        [3] = new(3, new[] { "ZSW1", "NIST_B", "NIST_B+", "ZSW2", "G1_ZSW", "G1_XIST1", "G1_XIST1+", "G1_XIST2", "G1_XIST2+" }, 5, "Standard telegram 3: speed control with one encoder"),
        [20] = new(20, new[] { "ZSW1", "NIST_A_GLATT", "IAIST_GLATT", "MIST_GLATT", "PIST_GLATT", "MELD_NAMUR" }, 2, "Standard telegram 20: speed control with current, torque and power"),
        [102] = new(102, new[] { "ZSW1", "NIST_B", "NIST_B+", "ZSW2", "MELDW", "G1_ZSW", "G1_XIST1", "G1_XIST1+", "G1_XIST2", "G1_XIST2+" }, 6, "SIEMENS telegram 102: speed control with torque reduction and one encoder"),
        [105] = new(105, new[] { "ZSW1", "NIST_B", "NIST_B+", "ZSW2", "MELDW", "G1_ZSW", "G1_XIST1", "G1_XIST1+", "G1_XIST2", "G1_XIST2+" }, 10, "SIEMENS telegram 105: DSC with torque reduction and one encoder"),
        [111] = new(111, new[] { "ZSW1", "POS_ZSW1", "POS_ZSW2", "ZSW2", "MELDW", "XIST_A", "XIST_A+", "NIST_B", "NIST_B+", "FAULT_CODE", "WARN_CODE", "reserved" }, 12, "SIEMENS telegram 111: basic positioner (EPos) in MDI mode"),
        [352] = new(352, new[] { "ZSW1", "NIST_A_GLATT", "IAIST_GLATT", "MIST_GLATT", "WARN_CODE", "FAULT_CODE" }, 6, "SIEMENS telegram 352: speed control with fault and warning code"),
    };

    public static TelegramLayout? Layout(int number) => Layouts.GetValueOrDefault(number);

    /// <summary>What each word means, for the page.</summary>
    public static string WordText(string word) => word.TrimEnd('+') switch
    {
        "ZSW1" => "Status word 1",
        "ZSW2" => "Status word 2",
        "NIST_A" or "NIST_A_GLATT" => "Actual speed (16 bit, 4000 hex = reference speed)",
        "NIST_B" => "Actual speed (32 bit)",
        "IAIST_GLATT" => "Actual current, smoothed",
        "MIST_GLATT" => "Actual torque, smoothed",
        "PIST_GLATT" => "Actual power, smoothed",
        "MELD_NAMUR" => "NAMUR fault word",
        "MELDW" => "Message word",
        "G1_ZSW" => "Encoder 1 status word",
        "G1_XIST1" => "Encoder 1 position 1",
        "G1_XIST2" => "Encoder 1 position 2",
        "POS_ZSW1" => "Positioning status word 1",
        "POS_ZSW2" => "Positioning status word 2",
        "XIST_A" => "Actual position",
        "FAULT_CODE" => "Number of the active fault",
        "WARN_CODE" => "Number of the active warning (alarm)",
        _ => word,
    };

    /// <summary>Status word 1 (ZSW1) of a SINAMICS drive in speed control (G120, S120, V90, G115D ...).</summary>
    public static readonly IReadOnlyList<StatusBit> Zsw1 = new List<StatusBit>
    {
        new(0, "Ready to switch on", "ready to switch on", "not ready to switch on", null),
        new(1, "Ready", "ready to run", "not ready to run", null),
        new(2, "Operation enabled", "running (operation enabled)", "not running", null),
        new(3, "Fault", "a fault is active", "no fault", true),
        new(4, "No coast stop (OFF2)", "OFF2 not active", "coast stop (OFF2) is active", false),
        new(5, "No quick stop (OFF3)", "OFF3 not active", "quick stop (OFF3) is active", false),
        new(6, "Switching on inhibited", "switching on is inhibited", "switching on allowed", true),
        new(7, "Warning", "a warning (alarm) is active", "no warning", true),
        new(8, "Speed within tolerance", "speed matches the setpoint", "speed differs from the setpoint", null),
        new(9, "Control requested", "the PLC has control", "the PLC does NOT have control (local or commissioning mode)", false),
        new(10, "Speed reached", "comparison speed reached", "comparison speed not reached", null),
        new(11, "No I/M/P limit", "no current, torque or power limit", "running at a current, torque or power limit", false),
        new(12, "Brake open", "holding brake open", "holding brake closed", null),
        new(13, "No motor overtemperature", "motor temperature OK", "motor overtemperature warning", false),
        new(14, "Turning forwards", "turning forwards (or standing)", "turning backwards", null),
        new(15, "No converter overload", "converter thermal load OK", "converter thermal overload warning", false),
    };

    /// <summary>
    /// Status word 1 in plain words: the overall state and every bit that points at a problem.
    /// State: Fault, Inhibited, Running, Ready, Off.
    /// </summary>
    public static (string State, string Text, List<string> Problems) DecodeZsw1(int zsw1)
    {
        bool B(int n) => (zsw1 & (1 << n)) != 0;
        var problems = Zsw1.Where(b => b.ProblemWhen is { } p && B(b.Bit) == p && b.Bit is not (3 or 6))
            .Select(b => B(b.Bit) ? b.WhenOn : b.WhenOff).ToList();
        if (B(3)) return ("Fault", "The drive reports a fault and has stopped (or will not start).", problems);
        if (B(6))
        {
            var why = !B(4) ? "coast stop (OFF2) is active, often STO or an OFF2 input"
                : !B(5) ? "quick stop (OFF3) is active"
                : "it needs ON to go off and on again (OFF1 edge) after power-up, a fault reset or STO";
            return ("Inhibited", $"Switching on is inhibited: {why}.", problems);
        }
        if (B(2)) return ("Running", "The drive is running (operation enabled).", problems);
        if (B(1) || B(0)) return ("Ready", "The drive is ready but not running (no ON command, or the ramp is stopped).", problems);
        return ("Off", "The drive is not ready (no ON command, no supply or still starting).", problems);
    }

    /// <summary>Fault or warning number as SINAMICS shows it: 7900 -> F07900, warning 7910 -> A07910.</summary>
    public static string? Code(double? value, bool warning)
    {
        if (value is not { } v || v <= 0 || v > 99999) return null;
        return (warning ? "A" : "F") + ((int)v).ToString("00000");
    }

    /// <summary>A 16-bit speed word (4000 hex = 100 % of the reference speed p2000) as percent.</summary>
    public static double? SpeedPercent(double? word)
    {
        if (word is not { } w) return null;
        var signed = (short)(ushort)((int)w & 0xFFFF);
        return Math.Round(signed * 100.0 / 0x4000, 1);
    }
}
