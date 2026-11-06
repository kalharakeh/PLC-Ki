using System.Globalization;
using PlcKi.Core.Import;

namespace PlcKi.Core.Status;

/// <summary>
/// The rules for changing a value in the PLC from PLC-Ki, kept apart from the driver so they can be tested:
/// which tags are part of the safety program (never changed), which addresses drive the machine directly,
/// and how a typed value becomes the PLC's type.
/// </summary>
public static class WriteRules
{
    /// <summary>"DB_IO" from "\"DB_IO\".PB_Start", "Motor_On" from "\"Motor_On\"" or "Motor_On".</summary>
    public static string Root(string symbol)
    {
        symbol = symbol.Trim();
        if (symbol.StartsWith('"'))
        {
            var end = symbol.IndexOf('"', 1);
            return end > 1 ? symbol[1..end] : symbol.Trim('"');
        }
        var cut = symbol.IndexOfAny(new[] { '.', '[' });
        return cut > 0 ? symbol[..cut] : symbol;
    }

    /// <summary>
    /// Why this symbol belongs to the safety program, or null when it does not. Safety means: an F-block or F-DB,
    /// the instance DB of an F-FB, or a tag or data block the safety program reads or writes. Without a loaded
    /// program PLC-Ki cannot tell, so it says so (the caller decides what that means for PLCSIM).
    /// </summary>
    public static string? SafetyReason(ExportPlc? plc, string symbol)
    {
        if (plc == null) return "No program is loaded, so PLC-Ki cannot check whether this tag belongs to the safety program.";
        var root = Root(symbol);
        if (root.Length == 0) return "No tag given.";
        if (SafetyBlockReason(plc, symbol) is { } block) return block;
        if (SafetyUser(plc, symbol) is { } user)
            return $"{root} is used by the safety program (in {user}). PLC-Ki never changes values the safety program uses.";
        return null;
    }

    /// <summary>
    /// Why this symbol is inside the safety program itself, or null: an F-block or F-DB, the instance DB of an F-FB,
    /// or an F-runtime / F-I/O system block. These are never changed, on PLCSIM neither.
    /// </summary>
    public static string? SafetyBlockReason(ExportPlc plc, string symbol)
    {
        var root = Root(symbol);
        if (root.Length == 0) return null;
        var safety = plc.Blocks.Where(b => b.IsSafety).ToList();
        var eq = StringComparer.OrdinalIgnoreCase;
        if (safety.FirstOrDefault(b => eq.Equals(b.Name, root)) is { } fb)
            return $"{root} is part of the safety program ({fb.Language}). PLC-Ki never changes safety values.";
        var block = plc.Blocks.FirstOrDefault(b => eq.Equals(b.Name, root));
        if (block?.InstanceOf is { } of && safety.Any(b => eq.Equals(b.Name, of)))
            return $"{root} is the data of the safety block {of}. PLC-Ki never changes safety values.";
        // F-runtime and F-I/O system blocks have names like F00001_..., RTG1SysInfo, F_IO_DB
        if (block != null && (root.StartsWith("F_", StringComparison.OrdinalIgnoreCase) || root.StartsWith("RTG", StringComparison.OrdinalIgnoreCase)
                              || System.Text.RegularExpressions.Regex.IsMatch(root, @"^F\d{4,}")))
            return $"{root} looks like a block of the safety system. PLC-Ki never changes safety values.";
        return null;
    }

    /// <summary>
    /// The safety block that uses this standard tag or data block (reads or writes it), or null. Checked by name:
    /// when the safety program uses one member of a data block, every member of it counts.
    /// </summary>
    public static string? SafetyUser(ExportPlc plc, string symbol)
    {
        var root = Root(symbol);
        return root.Length == 0 ? null : plc.Blocks.FirstOrDefault(b => b.IsSafety && b.GlobalNames.Contains(root))?.Name;
    }

    /// <summary>The tag-table address of a plain PLC tag ("%I0.3"), or null for data block values.</summary>
    public static string? Address(ExportPlc? plc, string symbol)
    {
        if (plc == null) return null;
        var s = symbol.Trim();
        if (s.StartsWith('%')) return s;
        var root = Root(s);
        if (s != root && s != $"\"{root}\"") return null;   // a member, so a data block value
        return plc.PlcTags.FirstOrDefault(t => t.Name.Equals(root, StringComparison.OrdinalIgnoreCase))?.Address;
    }

    /// <summary>How an S7-1500 force behaves (it has no FRCE LED: an active force lights MAINT yellow).</summary>
    public const string ForceStays = "A force stays until it is removed, also after power off; the CPU shows it with the yellow MAINT LED.";

    /// <summary>
    /// On a real PLC, inputs and outputs are not changed: inputs are overwritten from the I/O every cycle,
    /// and an output moves something in the machine. Returns why, or null for memory and data block values.
    /// </summary>
    public static string? RealIoReason(string? address)
    {
        if (string.IsNullOrEmpty(address)) return null;
        var a = address.TrimStart('%').ToUpperInvariant();
        if (a.StartsWith('I') || a.StartsWith('E'))
            return $"{address} is an input. On a real PLC the I/O overwrites it every cycle, so a change would not stay. Forcing inputs needs TIA Portal with the machine made safe. " + ForceStays;
        if (a.StartsWith('Q') || a.StartsWith('A'))
            return $"{address} is an output: it moves something in the machine. PLC-Ki does not switch real outputs. Use TIA Portal with the machine made safe. " + ForceStays;
        return null;
    }

    /// <summary>
    /// Turns typed text into the PLC type (the CLR type of the driver's tag value). Times are in milliseconds,
    /// or written as 5s, 200ms, 2m, T#1M30S. Throws FormatException with a plain message.
    /// </summary>
    public static object Parse(string? text, Type clr, bool isTime = false)
    {
        var t = (text ?? "").Trim();
        if (t.Length == 0) throw new FormatException("Type a value.");
        var inv = CultureInfo.InvariantCulture;
        if (clr == typeof(bool))
        {
            return t.ToLowerInvariant() switch
            {
                "true" or "1" or "on" or "yes" or "an" or "ein" => true,
                "false" or "0" or "off" or "no" or "aus" => false,
                _ => throw new FormatException("Type TRUE or FALSE."),
            };
        }
        if (clr == typeof(string)) return t.Length >= 2 && t[0] == '\'' && t[^1] == '\'' ? t[1..^1] : t;
        if (clr == typeof(float) || clr == typeof(double))
        {
            if (!double.TryParse(t.Replace(',', '.'), NumberStyles.Float, inv, out var d) || double.IsNaN(d) || double.IsInfinity(d))
                throw new FormatException($"{t} is not a number.");
            if (clr == typeof(float))
            {
                if (Math.Abs(d) > float.MaxValue) throw new FormatException($"{t} is too big for a Real.");
                return (float)d;
            }
            return d;
        }
        long n;
        if (isTime) n = ParseMs(t);
        else if (t.StartsWith("16#", StringComparison.OrdinalIgnoreCase) || t.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            if (!long.TryParse(t[(t.StartsWith('0') ? 2 : 3)..].Replace("_", ""), NumberStyles.HexNumber, inv, out n)) throw new FormatException($"{t} is not a hex number.");
        }
        else if (!long.TryParse(t, NumberStyles.Integer, inv, out n))
        {
            if (clr == typeof(ulong) && ulong.TryParse(t, NumberStyles.Integer, inv, out var u)) return u;
            throw new FormatException($"{t} is not a whole number.");
        }
        try
        {
            return Type.GetTypeCode(clr) switch
            {
                TypeCode.Byte => checked((byte)n),
                TypeCode.SByte => checked((sbyte)n),
                TypeCode.Int16 => checked((short)n),
                TypeCode.UInt16 => checked((ushort)n),
                TypeCode.Int32 => checked((int)n),
                TypeCode.UInt32 => checked((uint)n),
                TypeCode.Int64 => n,
                TypeCode.UInt64 => checked((ulong)n),
                _ => throw new FormatException($"PLC-Ki cannot change values of this type ({clr.Name})."),
            };
        }
        catch (OverflowException)
        {
            throw new FormatException($"{t} does not fit: allowed is {Range(clr)}.");
        }
    }

    private static string Range(Type clr) => Type.GetTypeCode(clr) switch
    {
        TypeCode.Byte => "0 to 255",
        TypeCode.SByte => "-128 to 127",
        TypeCode.Int16 => "-32768 to 32767",
        TypeCode.UInt16 => "0 to 65535",
        TypeCode.Int32 => "-2147483648 to 2147483647",
        TypeCode.UInt32 => "0 to 4294967295",
        _ => "a smaller number",
    };

    /// <summary>"500" (ms), "500ms", "5s", "2m", "1h", "T#1M30S", "T#250MS" to milliseconds.</summary>
    public static long ParseMs(string text)
    {
        var t = text.Trim().ToLowerInvariant().Replace("_", "");
        if (t.StartsWith("t#")) t = t[2..];
        else if (t.StartsWith("time#")) t = t[5..];
        if (long.TryParse(t, NumberStyles.Integer, CultureInfo.InvariantCulture, out var plain)) return plain;
        var m = System.Text.RegularExpressions.Regex.Match(t, @"^-?((\d+(\.\d+)?)(d|h|ms|m|s))+$");
        if (!m.Success) throw new FormatException($"{text} is not a time. Type milliseconds, or for example 5s, 2m, T#1M30S.");
        double total = 0;
        foreach (System.Text.RegularExpressions.Match p in System.Text.RegularExpressions.Regex.Matches(t, @"(\d+(?:\.\d+)?)(d|h|ms|m|s)"))
        {
            var v = double.Parse(p.Groups[1].Value, CultureInfo.InvariantCulture);
            total += p.Groups[2].Value switch { "d" => v * 86_400_000, "h" => v * 3_600_000, "m" => v * 60_000, "s" => v * 1000, _ => v };
        }
        return (long)Math.Round(t.StartsWith('-') ? -total : total);
    }

    /// <summary>A value as text for the log and the page (invariant, TRUE/FALSE for Bool).</summary>
    public static string Show(object? v) => v switch
    {
        null => "?",
        bool b => b ? "TRUE" : "FALSE",
        float f => f.ToString("R", CultureInfo.InvariantCulture),
        double d => d.ToString("R", CultureInfo.InvariantCulture),
        IFormattable x => x.ToString(null, CultureInfo.InvariantCulture),
        _ => v.ToString() ?? "?",
    };
}
