using System.Buffers.Binary;
using System.Text.RegularExpressions;
using PlcKi.Core.Hardware;
using PlcKi.Core.Import;

namespace PlcKi.Core.Status;

/// <summary>An I/O address as the PLC writes it: %I0.3 (a bit), %IB2, %IW64, %ID8; :P is the same simulated input.</summary>
public sealed record IoAddress(char Area, int Byte, int? Bit, int Size, bool Peripheral)
{
    /// <summary>"%I0.3", "%IW64" (without :P: on PLCSIM the peripheral input and the input are the same bytes).</summary>
    public string Text => Bit is { } b ? $"%{Area}{Byte}.{b}" : $"%{Area}{Size switch { 1 => "B", 2 => "W", _ => "D" }}{Byte}";

    /// <summary>The bytes this address covers (a bit covers its byte).</summary>
    public int Bytes => Bit != null ? 1 : Size;
}

/// <summary>
/// One input PLC-Ki holds on PLCSIM: what it was before, so Release can put it back (there is no real field behind
/// a simulated input to fall back to). Saved in held-inputs.json in the data folder.
/// </summary>
public sealed record HeldInput(string Symbol, string Address, string Datatype, string Value, string Before,
    string User, DateTimeOffset Time, string Plc, string Instance, string? Reason);

/// <summary>
/// The rules for "Hold on PLCSIM": setting a simulated input through the PLCSIM Advanced API, so that it stays
/// (the CPU copies the simulated inputs into its process image every cycle; a plain change of %I is overwritten
/// at once). Kept apart from the endpoints so they can be tested:
///   PLCSIM only, and the TIA helper must confirm the PLCSIM instance at that address;
///   inputs only (an output is written by the CPU every cycle; memory and data block values are not I/O);
///   never safety: not a value the safety program uses, and not a byte of an F-module (its input bytes are the
///   PROFIsafe frame; overwriting them shuts the F-module down, on PLCSIM too).
/// </summary>
public static class HoldRules
{
    private static readonly Regex AddressRx = new(@"^%?([IEQAM])(B|W|D)?(\d+)(?:\.([0-7]))?(:P)?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>"%I0.3", "%IW64:P", "E 2.1" (German) to an IoAddress; null when it is not a plain byte, word, double word or bit address.</summary>
    public static IoAddress? ParseAddress(string? address)
    {
        if (string.IsNullOrWhiteSpace(address)) return null;
        var m = AddressRx.Match(address.Replace(" ", ""));
        if (!m.Success) return null;
        var area = char.ToUpperInvariant(m.Groups[1].Value[0]) switch { 'E' => 'I', 'A' => 'Q', var c => c };
        var size = m.Groups[2].Value.ToUpperInvariant() switch { "B" => 1, "W" => 2, "D" => 4, _ => 0 };
        var hasBit = m.Groups[4].Success;
        if (size == 0 && !hasBit) return null;     // "%I4" is not an address
        if (size != 0 && hasBit) return null;      // "%IW4.1" neither
        return new IoAddress(area, int.Parse(m.Groups[3].Value), hasBit ? int.Parse(m.Groups[4].Value) : null, hasBit ? 0 : size, m.Groups[5].Success);
    }

    /// <summary>Bytes of a PLC type (0 for Bool, a bit), or -1 when PLC-Ki does not hold this type.</summary>
    public static int SizeOf(string? datatype) => (datatype ?? "").Trim() switch
    {
        "Bool" => 0,
        "Byte" or "USInt" or "SInt" => 1,
        "Word" or "UInt" or "Int" => 2,
        "DWord" or "UDInt" or "DInt" or "Real" => 4,
        _ => -1,
    };

    /// <summary>The CLR type WriteRules.Parse turns typed text into, for a type SizeOf accepts.</summary>
    public static Type ClrType(string datatype) => datatype.Trim() switch
    {
        "Bool" => typeof(bool),
        "Byte" or "USInt" => typeof(byte),
        "SInt" => typeof(sbyte),
        "Word" or "UInt" => typeof(ushort),
        "Int" => typeof(short),
        "DWord" or "UDInt" => typeof(uint),
        "DInt" => typeof(int),
        "Real" => typeof(float),
        _ => throw new FormatException($"PLC-Ki does not hold {datatype} inputs."),
    };

    /// <summary>A typed value as the PLC stores it (big-endian). A Bool is one byte, 1 or 0, for the bit.</summary>
    public static byte[] Encode(object value, string datatype)
    {
        var b = new byte[Math.Max(1, SizeOf(datatype))];
        switch (value)
        {
            case bool x: b[0] = x ? (byte)1 : (byte)0; break;
            case byte x: b[0] = x; break;
            case sbyte x: b[0] = unchecked((byte)x); break;
            case ushort x: BinaryPrimitives.WriteUInt16BigEndian(b, x); break;
            case short x: BinaryPrimitives.WriteInt16BigEndian(b, x); break;
            case uint x: BinaryPrimitives.WriteUInt32BigEndian(b, x); break;
            case int x: BinaryPrimitives.WriteInt32BigEndian(b, x); break;
            case float x: BinaryPrimitives.WriteSingleBigEndian(b, x); break;
            default: throw new FormatException($"PLC-Ki cannot hold a {value.GetType().Name} value.");
        }
        return b;
    }

    /// <summary>Bytes read from the PLCSIM input area, shown as the PLC type (TRUE/FALSE, numbers).</summary>
    public static string Decode(byte[] data, string datatype)
    {
        object v = datatype.Trim() switch
        {
            "Bool" => data.Length > 0 && data[0] != 0,
            "Byte" or "USInt" => data[0],
            "SInt" => unchecked((sbyte)data[0]),
            "Word" or "UInt" => BinaryPrimitives.ReadUInt16BigEndian(data),
            "Int" => BinaryPrimitives.ReadInt16BigEndian(data),
            "DWord" or "UDInt" => BinaryPrimitives.ReadUInt32BigEndian(data),
            "DInt" => BinaryPrimitives.ReadInt32BigEndian(data),
            "Real" => BinaryPrimitives.ReadSingleBigEndian(data),
            _ => throw new FormatException($"PLC-Ki does not hold {datatype} inputs."),
        };
        return WriteRules.Show(v);
    }

    /// <summary>
    /// Is the PLC PLC-Ki reads the TIA helper's PLCSIM instance? Not only the same address: the helper must find a
    /// running PLCSIM instance with that address, and when both RUN/STOP states are known they must agree.
    /// Returns why not, or null.
    /// </summary>
    public static string? PlcsimRefusal(string plc, bool applies, bool found, string? instance, string? why, string? plcState, string? simState)
    {
        if (!applies)
            return "Hold works on PLCSIM only, and PLC-Ki cannot tell that this PLC is PLCSIM. " + (why ?? "") +
                   " On a real PLC only a force from TIA Portal holds an input or output.";
        if (!found || string.IsNullOrWhiteSpace(instance))
            return $"The TIA helper finds no running PLCSIM instance at {plc}, so PLC-Ki cannot confirm it is PLCSIM. Start the instance in PLCSIM Advanced.";
        var a = RunStop(plcState);
        var b = RunStop(simState);
        if (a != null && b != null && a != b)
            return $"PLC-Ki reads a CPU in {a} at {plc}, but the PLCSIM instance \"{instance}\" is in {b}. They may not be the same CPU, so nothing is held.";
        return null;
    }

    private static string? RunStop(string? state) => (state ?? "").Trim().ToUpperInvariant() switch
    {
        "RUN" => "RUN",
        "STOP" => "STOP",
        _ => null,   // starting, unknown, not read: no comparison
    };

    /// <summary>
    /// Why this value cannot be held on PLCSIM, or null when it can. <paramref name="plcsimRefusal"/> comes from
    /// PlcsimRefusal. Without a hardware export, holding is refused when the program has a safety program
    /// (PLC-Ki cannot tell which bytes are on F-modules), and allowed when it has none.
    /// </summary>
    public static string? Refusal(ExportPlc? plc, HardwareConfig? hw, string symbol, string? plcsimRefusal)
    {
        if (plcsimRefusal != null) return plcsimRefusal;
        if (plc == null) return "No program is loaded, so PLC-Ki cannot tell what this value is.";
        var s = (symbol ?? "").Trim();
        if (s.Length == 0) return "No tag given.";
        var text = WriteRules.Address(plc, s);
        if (text == null)
        {
            var root = WriteRules.Root(s);
            var known = plc.Blocks.Any(b => b.Name.Equals(root, StringComparison.OrdinalIgnoreCase))
                        || plc.PlcTags.Any(t => t.Name.Equals(root, StringComparison.OrdinalIgnoreCase));
            return known ? $"{s} is a data block value, not an input. Only inputs can be held: the program decides data block values."
                         : $"{s} is not in the loaded program, so PLC-Ki cannot tell what it is.";
        }
        var a = ParseAddress(text);
        if (a == null) return $"{s} is at {text}, which is not a plain input address.";
        if (a.Area == 'Q')
            return $"{text} is an output. The CPU writes outputs every cycle, so PLCSIM cannot hold one. On a real S7-1500 only a force from TIA Portal holds an output.";
        if (a.Area != 'I')
            return $"{text} is a memory value, not an input. Only inputs and outputs can be forced on an S7-1500, and PLC-Ki holds only inputs.";

        var datatype = plc.PlcTags.FirstOrDefault(t => t.Name.Equals(WriteRules.Root(s), StringComparison.OrdinalIgnoreCase))?.Datatype ?? "";
        var size = SizeOf(datatype);
        if (size < 0) return $"PLC-Ki holds only Bool, Byte, Word, Int, DWord, DInt and Real inputs, not {datatype}.";
        if ((size == 0) != (a.Bit != null) || (size > 0 && size != a.Size))
            return $"The address {text} does not fit the type {datatype}.";

        if (WriteRules.SafetyReason(plc, s) is { } safety) return safety;
        if (hw == null)
            return plc.Blocks.Any(b => b.IsSafety)
                ? $"No hardware export is loaded, so PLC-Ki cannot tell whether {a.Text} is on a safety (F-) module. Import the hardware first."
                : null;
        if (FModule(hw, a) is { } f)
            return $"{a.Text} is on the safety module {f}. Its input bytes carry the safety frame; PLC-Ki never touches safety I/O.";
        return null;
    }

    /// <summary>"F-DI 8x24VDC HF in ET200SP_1" when an F-module's input range covers the address, else null.</summary>
    public static string? FModule(HardwareConfig hw, IoAddress a)
    {
        int first = a.Byte, last = a.Byte + a.Bytes - 1;
        foreach (var st in hw.Stations)
            foreach (var m in st.Modules)
            {
                if (!m.Safety || m.Kind == "cpu") continue;
                foreach (var r in m.Addresses)
                    if (r.Io == (a.Area == 'I' ? "Input" : "Output") && first <= r.Start + r.Bytes - 1 && last >= r.Start)
                        return $"{m.Title ?? m.Name} in {st.Name}";
            }
        return null;
    }
}
