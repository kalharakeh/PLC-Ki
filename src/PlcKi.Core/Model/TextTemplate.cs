using System.Globalization;
using System.Text.RegularExpressions;

namespace PlcKi.Core.Model;

/// <summary>Fills "{TagId}" placeholders in model texts with live, formatted values.</summary>
public static class TextTemplate
{
    private static readonly Regex PlaceholderRx = new(@"\{([A-Za-z_][A-Za-z0-9_]*)\}", RegexOptions.Compiled);

    public static IEnumerable<string> Placeholders(string text) =>
        PlaceholderRx.Matches(text).Select(m => m.Groups[1].Value);

    public static string Fill(string? text, Func<string, string> format) =>
        text == null ? "" : PlaceholderRx.Replace(text, m => format(m.Groups[1].Value));
}

/// <summary>Turns raw values into short readable strings: "42.5 %", "1 min 12 s", "pressed".</summary>
public static class ValueFormat
{
    public static string Format(TagDef? tag, object? value)
    {
        if (value == null) return "?";
        switch (value)
        {
            case bool b:
                if (tag != null && (tag.TrueText != null || tag.FalseText != null))
                    return b ? tag.TrueText ?? "TRUE" : tag.FalseText ?? "FALSE";
                return b ? "TRUE" : "FALSE";
            case double d:
                if (tag?.Type == "Time") return Duration(d);
                string num;
                if (tag?.Type is "Int" or "DInt") num = Math.Round(d).ToString("0", CultureInfo.InvariantCulture);
                else
                {
                    var dec = tag?.Decimals ?? 1;
                    num = d.ToString("F" + dec, CultureInfo.InvariantCulture);
                }
                return string.IsNullOrEmpty(tag?.Unit) ? num : $"{num} {tag!.Unit}";
            default:
                return value.ToString() ?? "?";
        }
    }

    /// <summary>Milliseconds as "850 ms", "12.3 s", "2 min 05 s", "1 h 02 min".</summary>
    public static string Duration(double ms)
    {
        var neg = ms < 0;
        ms = Math.Abs(ms);
        string s;
        if (ms < 1000) s = $"{ms:0} ms";
        else if (ms < 60_000) s = (ms / 1000).ToString(ms < 10_000 ? "0.0" : "0", CultureInfo.InvariantCulture) + " s";
        else if (ms < 3_600_000)
        {
            var t = TimeSpan.FromMilliseconds(ms);
            s = $"{(int)t.TotalMinutes} min {t.Seconds:00} s";
        }
        else
        {
            var t = TimeSpan.FromMilliseconds(ms);
            s = $"{(int)t.TotalHours} h {t.Minutes:00} min";
        }
        return neg ? "-" + s : s;
    }
}

/// <summary>Brings raw driver values to the two types the engine uses: bool, or double (times in ms).</summary>
public static class TagValue
{
    public static object? Normalize(TagDef tag, object? raw)
    {
        if (raw == null) return null;
        if (tag.Type == "Bool")
        {
            return raw switch
            {
                bool b => b,
                string s when bool.TryParse(s, out var b2) => b2,
                IConvertible c => System.Convert.ToDouble(c, CultureInfo.InvariantCulture) != 0.0,
                _ => null,
            };
        }
        return raw switch
        {
            bool b => b ? 1.0 : 0.0,
            TimeSpan ts => ts.TotalMilliseconds,
            string s when tag.Type == "Time" && s.StartsWith("T#", StringComparison.OrdinalIgnoreCase) => Expressions.ExprParser.ParseTimeMs(s),
            string s when double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) => d,
            IConvertible c => System.Convert.ToDouble(c, CultureInfo.InvariantCulture),
            _ => null,
        };
    }
}
