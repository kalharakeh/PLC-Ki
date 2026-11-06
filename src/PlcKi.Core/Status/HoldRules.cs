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
///   safety inputs too, but with a warning: an F-module's input bytes are the PROFIsafe frame, and overwriting
///   them makes the F-module fault in PLCSIM. What is allowed is decided in ChangeRules, shared with "Change a value".
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
    /// May this value be held? The shared decision (ChangeRules) for Hold: confirmed PLCSIM, inputs only, of a plain
    /// type; safety inputs with a warning. <paramref name="notConfirmed"/> is ChangeRules.PlcsimNotConfirmed's answer.
    /// </summary>
    public static ChangeDecision Decide(ExportPlc? plc, HardwareConfig? hw, string symbol, string? notConfirmed) =>
        ChangeRules.Decide(plc, hw, symbol, ChangeKind.Hold, notConfirmed == null ? ChangeTarget.Plcsim : ChangeTarget.RealNotAllowed, notConfirmed);

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
