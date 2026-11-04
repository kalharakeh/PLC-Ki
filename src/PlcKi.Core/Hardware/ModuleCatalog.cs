using System.Text.RegularExpressions;

namespace PlcKi.Core.Hardware;

/// <summary>What PLC-Ki knows about a module from its order number (MLFB): the product family, what kind of
/// module it is, its width on the rail and how many channels it has. The Hardware and PLC pages draw each
/// module from this, so a CPU 1515F looks like a wide S7-1500 CPU with a display and a DI 32 has 32 LEDs.</summary>
public sealed record ModuleSpec(
    string Family,       // s7-1500, s7-1200, et200sp, et200mp, other
    string Kind,         // cpu, ps, pm, di, dq, dio, ai, aq, aio, f-di, f-dq, f-pm, cp, cm, tm, im, bus, other
    string? Title,       // the catalog name ("DI 32x24VDC HF"), when known
    double WidthMm,
    int Inputs,          // channels, when known from the catalog
    int Outputs,
    bool Safety,
    bool Display);       // an S7-1500 CPU with a front display

public static class ModuleCatalog
{
    // Exact entries for the modules seen most often. Everything else is decoded from the number pattern below.
    private static readonly Dictionary<string, ModuleSpec> Known = new(StringComparer.OrdinalIgnoreCase)
    {
        // S7-1500 CPUs
        ["6ES7 511-1AK02-0AB0"] = Cpu1500("CPU 1511-1 PN", 35),
        ["6ES7 511-1FK02-0AB0"] = Cpu1500("CPU 1511F-1 PN", 35, true),
        ["6ES7 511-1CK01-0AB0"] = Cpu1500("CPU 1511C-1 PN", 85),
        ["6ES7 512-1CK01-0AB0"] = Cpu1500("CPU 1512C-1 PN", 110),
        ["6ES7 513-1AL02-0AB0"] = Cpu1500("CPU 1513-1 PN", 35),
        ["6ES7 513-1FL02-0AB0"] = Cpu1500("CPU 1513F-1 PN", 35, true),
        ["6ES7 515-2AM02-0AB0"] = Cpu1500("CPU 1515-2 PN", 70),
        ["6ES7 515-2FM02-0AB0"] = Cpu1500("CPU 1515F-2 PN", 70, true),
        ["6ES7 516-3AN02-0AB0"] = Cpu1500("CPU 1516-3 PN/DP", 70),
        ["6ES7 516-3FN02-0AB0"] = Cpu1500("CPU 1516F-3 PN/DP", 70, true),
        ["6ES7 517-3AP00-0AB0"] = Cpu1500("CPU 1517-3 PN/DP", 175),
        ["6ES7 517-3FP00-0AB0"] = Cpu1500("CPU 1517F-3 PN/DP", 175, true),
        ["6ES7 518-4AP00-0AB0"] = Cpu1500("CPU 1518-4 PN/DP", 175),
        ["6ES7 518-4FP00-0AB0"] = Cpu1500("CPU 1518F-4 PN/DP", 175, true),
        // S7-1500 power
        ["6ES7 505-0KA00-0AB0"] = new("s7-1500", "ps", "PS 25W 24VDC", 35, 0, 0, false, false),
        ["6ES7 505-0RA00-0AB0"] = new("s7-1500", "ps", "PS 60W 24/48/60VDC", 70, 0, 0, false, false),
        ["6ES7 507-0RA00-0AB0"] = new("s7-1500", "ps", "PS 60W 120/230VAC/DC", 70, 0, 0, false, false),
        ["6EP1 332-4BA00"] = new("s7-1500", "pm", "PM 70W 120/230VAC", 50, 0, 0, false, false),
        ["6EP1 333-4BA00"] = new("s7-1500", "pm", "PM 190W 120/230VAC", 75, 0, 0, false, false),
        // S7-1500 digital
        ["6ES7 521-1BL00-0AB0"] = Io1500("di", "DI 32x24VDC HF", 35, 32, 0),
        ["6ES7 521-1BL10-0AA0"] = Io1500("di", "DI 32x24VDC BA", 25, 32, 0),
        ["6ES7 521-1BH00-0AB0"] = Io1500("di", "DI 16x24VDC HF", 35, 16, 0),
        ["6ES7 521-1BH10-0AA0"] = Io1500("di", "DI 16x24VDC BA", 25, 16, 0),
        ["6ES7 521-1BH50-0AA0"] = Io1500("di", "DI 16x24VDC SRC BA", 35, 16, 0),
        ["6ES7 521-1FH00-0AA0"] = Io1500("di", "DI 16x230VAC BA", 35, 16, 0),
        ["6ES7 522-1BL01-0AB0"] = Io1500("dq", "DQ 32x24VDC/0.5A HF", 35, 0, 32),
        ["6ES7 522-1BL10-0AA0"] = Io1500("dq", "DQ 32x24VDC/0.5A BA", 25, 0, 32),
        ["6ES7 522-1BH01-0AB0"] = Io1500("dq", "DQ 16x24VDC/0.5A HF", 35, 0, 16),
        ["6ES7 522-1BH10-0AA0"] = Io1500("dq", "DQ 16x24VDC/0.5A BA", 25, 0, 16),
        ["6ES7 522-1BF00-0AB0"] = Io1500("dq", "DQ 8x24VDC/2A HF", 35, 0, 8),
        ["6ES7 522-5HF00-0AB0"] = Io1500("dq", "DQ 8x230VAC/5A ST Relay", 35, 0, 8),
        ["6ES7 522-5HH00-0AB0"] = Io1500("dq", "DQ 16x230VAC/2A ST Relay", 35, 0, 16),
        ["6ES7 523-1BL00-0AA0"] = Io1500("dio", "DI 16x24VDC/DQ 16x24VDC/0.5A BA", 25, 16, 16),
        // S7-1500 analog
        ["6ES7 531-7KF00-0AB0"] = Io1500("ai", "AI 8xU/I/RTD/TC ST", 35, 8, 0),
        ["6ES7 531-7NF10-0AB0"] = Io1500("ai", "AI 8xU/I HS", 35, 8, 0),
        ["6ES7 531-7NF00-0AB0"] = Io1500("ai", "AI 8xU/I HF", 35, 8, 0),
        ["6ES7 531-7PF00-0AB0"] = Io1500("ai", "AI 8xU/R/RTD/TC HF", 35, 8, 0),
        ["6ES7 531-7QD00-0AB0"] = Io1500("ai", "AI 4xU/I/RTD/TC ST", 25, 4, 0),
        ["6ES7 532-5HD00-0AB0"] = Io1500("aq", "AQ 4xU/I ST", 35, 0, 4),
        ["6ES7 532-5HF00-0AB0"] = Io1500("aq", "AQ 8xU/I HS", 35, 0, 8),
        ["6ES7 532-5ND00-0AB0"] = Io1500("aq", "AQ 4xU/I HF", 35, 0, 4),
        ["6ES7 534-7QE00-0AB0"] = Io1500("aio", "AI 4xU/I/RTD/TC/AQ 2xU/I ST", 25, 4, 2),
        // S7-1500 fail-safe
        ["6ES7 526-1BH00-0AB0"] = new("s7-1500", "f-di", "F-DI 16x24VDC", 35, 16, 0, true, false),
        ["6ES7 526-2BF00-0AB0"] = new("s7-1500", "f-dq", "F-DQ 8x24VDC/2A PPM", 35, 0, 8, true, false),
        // S7-1500 communication and technology
        ["6ES7 540-1AD00-0AA0"] = new("s7-1500", "cm", "CM PtP RS232 BA", 35, 0, 0, false, false),
        ["6ES7 541-1AB00-0AB0"] = new("s7-1500", "cm", "CM PtP RS422/485 HF", 35, 0, 0, false, false),
        ["6ES7 541-1AD00-0AB0"] = new("s7-1500", "cm", "CM PtP RS422/485 HF", 35, 0, 0, false, false),
        ["6ES7 542-5DX00-0AB0"] = new("s7-1500", "cm", "CM 1542-5", 35, 0, 0, false, false),
        ["6GK7 543-1AX00-0XE0"] = new("s7-1500", "cp", "CP 1543-1", 35, 0, 0, false, false),
        ["6ES7 550-1AA00-0AB0"] = new("s7-1500", "tm", "TM Count 2x24V", 35, 0, 0, false, false),
        ["6ES7 551-1AB00-0AB0"] = new("s7-1500", "tm", "TM PosInput 2", 35, 0, 0, false, false),
        // ET 200MP / ET 200SP heads
        ["6ES7 155-5AA01-0AB0"] = new("et200mp", "im", "IM 155-5 PN ST", 35, 0, 0, false, false),
        ["6ES7 155-6AU01-0BN0"] = new("et200sp", "im", "IM 155-6 PN ST", 50, 0, 0, false, false),
        ["6ES7 155-6AU00-0CN0"] = new("et200sp", "im", "IM 155-6 PN HF", 50, 0, 0, false, false),
        ["6ES7 155-6AU30-0CN0"] = new("et200sp", "im", "IM 155-6 PN/2 HF", 50, 0, 0, false, false),
        // ET 200SP modules
        ["6ES7 131-6BF01-0BA0"] = Sp("di", "DI 8x24VDC ST", 8, 0),
        ["6ES7 131-6BF00-0BA0"] = Sp("di", "DI 8x24VDC ST", 8, 0),
        ["6ES7 131-6BH01-0BA0"] = Sp("di", "DI 16x24VDC ST", 16, 0),
        ["6ES7 131-6BF01-0CA0"] = Sp("di", "DI 8x24VDC HF", 8, 0),
        ["6ES7 132-6BF01-0BA0"] = Sp("dq", "DQ 8x24VDC/0.5A ST", 0, 8),
        ["6ES7 132-6BF00-0BA0"] = Sp("dq", "DQ 8x24VDC/0.5A ST", 0, 8),
        ["6ES7 132-6BH01-0BA0"] = Sp("dq", "DQ 16x24VDC/0.5A ST", 0, 16),
        ["6ES7 132-6BD20-0BA0"] = Sp("dq", "DQ 4x24VDC/2A ST", 0, 4),
        ["6ES7 132-6HD01-0BB1"] = Sp("dq", "RQ 4x24VUC/2A CO ST", 0, 4),
        ["6ES7 134-6HD01-0BA1"] = Sp("ai", "AI 4xI 2-/4-wire ST", 4, 0),
        ["6ES7 134-6GD01-0BA1"] = Sp("ai", "AI 4xI 2-wire ST", 4, 0),
        ["6ES7 134-6HB00-0DA1"] = Sp("ai", "AI 2xU/I 2-/4-wire HS", 2, 0),
        ["6ES7 134-6JD00-0CA1"] = Sp("ai", "AI 4xRTD/TC 2-/3-/4-wire HF", 4, 0),
        ["6ES7 134-6FF00-0AA1"] = Sp("ai", "AI 8xU BA", 8, 0),
        ["6ES7 135-6HD00-0BA1"] = Sp("aq", "AQ 4xU/I ST", 0, 4),
        ["6ES7 135-6HB00-0DA1"] = Sp("aq", "AQ 2xU/I HS", 0, 2),
        ["6ES7 136-6BA00-0CA0"] = new("et200sp", "f-di", "F-DI 8x24VDC HF", 15, 8, 0, true, false),
        ["6ES7 136-6DB00-0CA0"] = new("et200sp", "f-dq", "F-DQ 4x24VDC/2A PM HF", 15, 0, 4, true, false),
        ["6ES7 136-6DC00-0CA0"] = new("et200sp", "f-dq", "F-DQ 8x24VDC/0.5A PP HF", 15, 0, 8, true, false),
        ["6ES7 136-6PA00-0BC0"] = new("et200sp", "f-pm", "F-PM-E 24VDC/8A PPM ST", 20, 0, 0, true, false),
        ["6ES7 138-6AA01-0BA0"] = new("et200sp", "tm", "TM Count 1x24V", 15, 0, 0, false, false),
        ["6ES7 137-6AA00-0BA0"] = new("et200sp", "cm", "CM PtP", 15, 0, 0, false, false),
        ["6ES7 193-6PA00-0AA0"] = new("et200sp", "bus", "Server module", 15, 0, 0, false, false),
        // S7-1200
        ["6ES7 211-1AE40-0XB0"] = Cpu1200("CPU 1211C DC/DC/DC", 90, 6, 4),
        ["6ES7 212-1AE40-0XB0"] = Cpu1200("CPU 1212C DC/DC/DC", 90, 8, 6),
        ["6ES7 214-1AG40-0XB0"] = Cpu1200("CPU 1214C DC/DC/DC", 110, 14, 10),
        ["6ES7 214-1HG40-0XB0"] = Cpu1200("CPU 1214C DC/DC/Rly", 110, 14, 10),
        ["6ES7 214-1AF40-0XB0"] = Cpu1200("CPU 1214FC DC/DC/DC", 110, 14, 10, true),
        ["6ES7 215-1AG40-0XB0"] = Cpu1200("CPU 1215C DC/DC/DC", 130, 14, 10),
        ["6ES7 215-1AF40-0XB0"] = Cpu1200("CPU 1215FC DC/DC/DC", 130, 14, 10, true),
        ["6ES7 217-1AG40-0XB0"] = Cpu1200("CPU 1217C DC/DC/DC", 150, 14, 10),
        ["6ES7 221-1BF32-0XB0"] = Sm1200("di", "SM 1221 DI 8x24VDC", 45, 8, 0),
        ["6ES7 221-1BH32-0XB0"] = Sm1200("di", "SM 1221 DI 16x24VDC", 45, 16, 0),
        ["6ES7 222-1BF32-0XB0"] = Sm1200("dq", "SM 1222 DQ 8x24VDC", 45, 0, 8),
        ["6ES7 222-1BH32-0XB0"] = Sm1200("dq", "SM 1222 DQ 16x24VDC", 45, 0, 16),
        ["6ES7 222-1HF32-0XB0"] = Sm1200("dq", "SM 1222 DQ 8xRelay", 45, 0, 8),
        ["6ES7 223-1BH32-0XB0"] = Sm1200("dio", "SM 1223 DI 8/DQ 8x24VDC", 45, 8, 8),
        ["6ES7 223-1PL32-0XB0"] = Sm1200("dio", "SM 1223 DI 16/DQ 16xRelay", 70, 16, 16),
        ["6ES7 231-4HD32-0XB0"] = Sm1200("ai", "SM 1231 AI 4x13 bit", 45, 4, 0),
        ["6ES7 231-4HF32-0XB0"] = Sm1200("ai", "SM 1231 AI 8x13 bit", 45, 8, 0),
        ["6ES7 232-4HD32-0XB0"] = Sm1200("aq", "SM 1232 AQ 4x14 bit", 45, 0, 4),
        ["6ES7 234-4HE32-0XB0"] = Sm1200("aio", "SM 1234 AI 4/AQ 2", 45, 4, 2),
        ["6ES7 226-6BA32-0XB0"] = new("s7-1200", "f-di", "SM 1226 F-DI 16x24VDC", 70, 16, 0, true, false),
        ["6ES7 226-6DA32-0XB0"] = new("s7-1200", "f-dq", "SM 1226 F-DQ 4x24VDC", 70, 0, 4, true, false),
        ["6ES7 241-1CH32-0XB0"] = new("s7-1200", "cm", "CM 1241 RS422/485", 30, 0, 0, false, false),
        ["6ES7 241-1AH32-0XB0"] = new("s7-1200", "cm", "CM 1241 RS232", 30, 0, 0, false, false),
    };

    private static ModuleSpec Cpu1500(string title, double w, bool f = false) => new("s7-1500", "cpu", title, w, 0, 0, f, true);
    private static ModuleSpec Io1500(string kind, string title, double w, int i, int o) => new("s7-1500", kind, title, w, i, o, false, false);
    private static ModuleSpec Sp(string kind, string title, int i, int o) => new("et200sp", kind, title, 15, i, o, false, false);
    private static ModuleSpec Cpu1200(string title, double w, int i, int o, bool f = false) => new("s7-1200", "cpu", title, w, i, o, f, false);
    private static ModuleSpec Sm1200(string kind, string title, double w, int i, int o) => new("s7-1200", kind, title, w, i, o, false, false);

    /// <summary>"OrderNumber:6ES7 515-2FM02-0AB0/V2.8" or "6ES7515-2FM02-0AB0" -> "6ES7 515-2FM02-0AB0".</summary>
    public static string? Normalize(string? orderNumber)
    {
        if (string.IsNullOrWhiteSpace(orderNumber)) return null;
        var s = orderNumber.Trim();
        if (s.StartsWith("OrderNumber:", StringComparison.OrdinalIgnoreCase)) s = s["OrderNumber:".Length..];
        var slash = s.IndexOf('/');
        if (slash > 0) s = s[..slash];
        s = s.Trim().ToUpperInvariant();
        var m = Regex.Match(s, @"^(6[A-Z]{2}\d)\s*(\d{3}-.*)$");
        return m.Success ? $"{m.Groups[1].Value} {m.Groups[2].Value}" : s;
    }

    /// <summary>"OrderNumber:6ES7 515-2FM02-0AB0/V2.8" -> "V2.8".</summary>
    public static string? FirmwareOf(string? typeIdentifier)
    {
        if (typeIdentifier == null) return null;
        var m = Regex.Match(typeIdentifier, @"/(V\d+(\.\d+)*)\s*$", RegexOptions.IgnoreCase);
        return m.Success ? m.Groups[1].Value.ToUpperInvariant() : null;
    }

    /// <summary>The spec for an order number; the TIA type name helps for unknown numbers ("DI 16x24VDC ...").</summary>
    public static ModuleSpec Lookup(string? orderNumber, string? typeName = null)
    {
        var mlfb = Normalize(orderNumber);
        if (mlfb != null)
        {
            if (Known.TryGetValue(mlfb, out var k)) return k;
            // Same module, other version letter (…-0AB0 vs …-0AA0): match on the first 14 characters
            var near = Known.FirstOrDefault(e => mlfb.Length >= 14 && e.Key.Length >= 14 && e.Key[..14].Equals(mlfb[..14], StringComparison.OrdinalIgnoreCase));
            if (near.Value != null) return near.Value;
        }
        return Decode(mlfb, typeName);
    }

    private static ModuleSpec Decode(string? mlfb, string? typeName)
    {
        var t = typeName ?? "";
        var (fromNameKind, ins, outs) = FromName(t);
        bool safety = t.StartsWith("F-", StringComparison.OrdinalIgnoreCase) || Regex.IsMatch(t, @"\bCPU \d{4}F", RegexOptions.IgnoreCase);
        if (mlfb != null)
        {
            var m = Regex.Match(mlfb, @"^6ES7 (\d)(\d)(\d)-(\d)");
            if (m.Success)
            {
                int a = m.Groups[1].Value[0] - '0', b = m.Groups[2].Value[0] - '0', c = m.Groups[3].Value[0] - '0', d = m.Groups[4].Value[0] - '0';
                if (a == 5)   // S7-1500
                {
                    if (b == 1) return new("s7-1500", "cpu", t.Length > 0 ? t : null, c switch { 5 or 6 => 70, 7 or 8 => 175, _ => 35 }, 0, 0, safety || mlfb.Contains("-1F") || mlfb.Contains("-2F") || mlfb.Contains("-3F") || mlfb.Contains("-4F"), true);
                    if (b == 0) return new("s7-1500", c == 7 ? "pm" : "ps", Null(t), 70, 0, 0, false, false);
                    if (b == 2 && c == 6) return new("s7-1500", d == 1 ? "f-di" : "f-dq", Null(t), 35, ins, outs, true, false);
                    if (b == 2) return new("s7-1500", fromNameKind ?? (c switch { 1 => "di", 2 => "dq", _ => "dio" }), Null(t), 35, ins, outs, false, false);
                    if (b == 3) return new("s7-1500", fromNameKind ?? (c switch { 1 => "ai", 2 => "aq", _ => "aio" }), Null(t), 35, ins, outs, false, false);
                    if (b == 4) return new("s7-1500", "cm", Null(t), 35, 0, 0, false, false);
                    if (b == 5) return new("s7-1500", "tm", Null(t), 35, 0, 0, false, false);
                    if (b == 9) return new("s7-1500", "bus", Null(t), 0, 0, 0, false, false);   // mounting rail
                }
                if (a == 2)   // S7-1200
                {
                    if (b == 1) return new("s7-1200", "cpu", Null(t), 110, ins, outs, safety, false);
                    if (b == 2 && c == 6) return new("s7-1200", ins > 0 ? "f-di" : "f-dq", Null(t), 70, ins, outs, true, false);
                    if (b == 2) return new("s7-1200", fromNameKind ?? (c switch { 1 => "di", 2 => "dq", _ => "dio" }), Null(t), 45, ins, outs, false, false);
                    if (b == 3) return new("s7-1200", fromNameKind ?? (c switch { 1 => "ai", 2 => "aq", _ => "aio" }), Null(t), 45, ins, outs, false, false);
                    if (b == 4) return new("s7-1200", "cm", Null(t), 30, 0, 0, false, false);
                }
                if (a == 1 && b == 3 && d == 6)   // ET 200SP I/O
                {
                    var kind = fromNameKind ?? (c switch { 1 => "di", 2 => "dq", 4 => "ai", 5 => "aq", 6 => ins > 0 ? "f-di" : "f-dq", 7 => "cm", 8 => "tm", _ => "other" });
                    return new("et200sp", kind, Null(t), 15, ins, outs, c == 6 || safety, false);
                }
                if (a == 1 && b == 5 && c == 5) return new(d == 6 ? "et200sp" : "et200mp", "im", Null(t), d == 6 ? 50 : 35, 0, 0, false, false);
                if (a == 1 && b == 9 && c == 3) return new("et200sp", "bus", Null(t), 15, 0, 0, false, false);
                if (a == 5 && b == 1) return new("et200sp", "cpu", Null(t), 100, 0, 0, safety, false);
            }
            if (mlfb.StartsWith("6GK7 54", StringComparison.Ordinal)) return new("s7-1500", "cp", Null(t), 35, 0, 0, false, false);
            if (mlfb.StartsWith("6GK7 24", StringComparison.Ordinal)) return new("s7-1200", "cp", Null(t), 30, 0, 0, false, false);
        }
        return new("other", fromNameKind ?? "other", Null(t), 40, ins, outs, safety, false);
    }

    private static string? Null(string s) => s.Length == 0 ? null : s;

    /// <summary>Kind and channel counts from a type name: "DI 16x24VDC HF" -> (di, 16, 0), "AQ 4xU/I ST" -> (aq, 0, 4).</summary>
    public static (string? Kind, int Inputs, int Outputs) FromName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return (null, 0, 0);
        int Count(string what)
        {
            var m = Regex.Match(name, $@"\b(?:F-)?{what}\s*(\d+)\s*[x/]", RegexOptions.IgnoreCase);
            return m.Success ? int.Parse(m.Groups[1].Value) : 0;
        }
        int di = Count("DI"), dq = Math.Max(Count("DQ"), Count("RQ")), ai = Count("AI"), aq = Count("AQ");
        var f = name.TrimStart().StartsWith("F-", StringComparison.OrdinalIgnoreCase);
        if (di > 0 && dq > 0) return ("dio", di, dq);
        if (ai > 0 && aq > 0) return ("aio", ai, aq);
        if (di > 0) return (f ? "f-di" : "di", di, 0);
        if (dq > 0) return (f ? "f-dq" : "dq", 0, dq);
        if (ai > 0) return ("ai", ai, 0);
        if (aq > 0) return ("aq", 0, aq);
        if (Regex.IsMatch(name, @"^\s*CPU\b", RegexOptions.IgnoreCase)) return ("cpu", 0, 0);
        if (Regex.IsMatch(name, @"^\s*PS\b", RegexOptions.IgnoreCase)) return ("ps", 0, 0);
        if (Regex.IsMatch(name, @"^\s*PM\b", RegexOptions.IgnoreCase)) return ("pm", 0, 0);
        if (Regex.IsMatch(name, @"^\s*IM\b", RegexOptions.IgnoreCase)) return ("im", 0, 0);
        if (Regex.IsMatch(name, @"^\s*(CM|CP)\b", RegexOptions.IgnoreCase)) return ("cm", 0, 0);
        if (Regex.IsMatch(name, @"^\s*TM\b", RegexOptions.IgnoreCase)) return ("tm", 0, 0);
        return (null, 0, 0);
    }

    public static bool IsAnalog(string kind) => kind is "ai" or "aq" or "aio";
}
