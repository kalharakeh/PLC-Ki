using System.Globalization;
using System.Text.RegularExpressions;

namespace PlcKi.Core.Expressions;

/// <summary>
/// A small condition language for the machine model, close to SCL:
///   NOT, AND, OR, XOR, = &lt;&gt; &lt; &lt;= &gt; &gt;=, + - * /, ( ), TRUE, FALSE, numbers, T#.. time literals, tag ids.
/// Values are bool or double (times are milliseconds). An unknown tag value gives null,
/// and null spreads like SQL's unknown: FALSE AND unknown = FALSE, TRUE AND unknown = unknown.
/// </summary>
public abstract class Expr
{
    public abstract object? Eval(Func<string, object?> lookup);

    public IEnumerable<string> Identifiers()
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var stack = new Stack<Expr>();
        stack.Push(this);
        while (stack.Count > 0)
        {
            var e = stack.Pop();
            switch (e)
            {
                case IdentExpr id: if (seen.Add(id.Name)) yield return id.Name; break;
                case UnaryExpr u: stack.Push(u.Operand); break;
                case BinaryExpr b: stack.Push(b.Right); stack.Push(b.Left); break;
            }
        }
    }

    /// <summary>Evaluates to bool; null when it cannot be decided.</summary>
    public bool? EvalBool(Func<string, object?> lookup) => Convert.ToBool(Eval(lookup));
}

public sealed class ExprException : Exception
{
    public ExprException(string message) : base(message) { }
}

internal static class Convert
{
    public static bool? ToBool(object? v) => v switch
    {
        null => null,
        bool b => b,
        double d => d != 0.0,
        _ => throw new ExprException($"Cannot use {v} as TRUE/FALSE."),
    };

    public static double? ToNumber(object? v) => v switch
    {
        null => null,
        double d => d,
        bool b => b ? 1.0 : 0.0,
        _ => throw new ExprException($"Cannot use {v} as a number."),
    };
}

public sealed class ConstExpr : Expr
{
    public object Value { get; }
    public ConstExpr(object value) => Value = value;
    public override object? Eval(Func<string, object?> lookup) => Value;
}

public sealed class IdentExpr : Expr
{
    public string Name { get; }
    public IdentExpr(string name) => Name = name;
    public override object? Eval(Func<string, object?> lookup) => lookup(Name);
}

public sealed class UnaryExpr : Expr
{
    public string Op { get; }
    public Expr Operand { get; }
    public UnaryExpr(string op, Expr operand) { Op = op; Operand = operand; }

    public override object? Eval(Func<string, object?> lookup)
    {
        var v = Operand.Eval(lookup);
        return Op switch
        {
            "NOT" => Convert.ToBool(v) is bool b ? !b : null,
            "-" => Convert.ToNumber(v) is double d ? -d : null,
            _ => throw new ExprException($"Unknown operator {Op}."),
        };
    }
}

public sealed class BinaryExpr : Expr
{
    public string Op { get; }
    public Expr Left { get; }
    public Expr Right { get; }
    public BinaryExpr(string op, Expr left, Expr right) { Op = op; Left = left; Right = right; }

    public override object? Eval(Func<string, object?> lookup)
    {
        switch (Op)
        {
            case "AND":
            {
                var l = Convert.ToBool(Left.Eval(lookup));
                if (l == false) return false;
                var r = Convert.ToBool(Right.Eval(lookup));
                if (r == false) return false;
                return l == null || r == null ? null : true;
            }
            case "OR":
            {
                var l = Convert.ToBool(Left.Eval(lookup));
                if (l == true) return true;
                var r = Convert.ToBool(Right.Eval(lookup));
                if (r == true) return true;
                return l == null || r == null ? null : false;
            }
            case "XOR":
            {
                var l = Convert.ToBool(Left.Eval(lookup));
                var r = Convert.ToBool(Right.Eval(lookup));
                return l == null || r == null ? null : l != r;
            }
        }

        var lv = Left.Eval(lookup);
        var rv = Right.Eval(lookup);
        if (lv == null || rv == null) return null;

        if ((Op == "=" || Op == "<>") && lv is bool lb && rv is bool rb)
            return Op == "=" ? lb == rb : lb != rb;

        var a = Convert.ToNumber(lv)!.Value;
        var c = Convert.ToNumber(rv)!.Value;
        const double eps = 1e-9;
        return Op switch
        {
            "=" => Math.Abs(a - c) < eps,
            "<>" => Math.Abs(a - c) >= eps,
            "<" => a < c,
            "<=" => a <= c + eps,
            ">" => a > c,
            ">=" => a >= c - eps,
            "+" => a + c,
            "-" => a - c,
            "*" => a * c,
            "/" => c == 0 ? null : a / c,
            _ => throw new ExprException($"Unknown operator {Op}."),
        };
    }
}

/// <summary>Recursive-descent parser. Precedence (low to high): OR, XOR, AND, NOT, comparison, + -, * /, unary minus.</summary>
public sealed class ExprParser
{
    private static readonly Regex TokenRx = new(
        @"\s*(?:(?<time>T#[0-9A-Za-z_.]+)|(?<num>\d+(?:\.\d+)?(?:[eE][+-]?\d+)?)|(?<id>[A-Za-z_][A-Za-z0-9_]*)|(?<op><=|>=|<>|[=<>()+\-*/]))",
        RegexOptions.Compiled);

    private static readonly Regex TimePartRx = new(@"(\d+(?:\.\d+)?)(MS|D|H|M|S)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private readonly List<string> _tokens;
    private int _pos;

    private ExprParser(List<string> tokens) => _tokens = tokens;

    public static Expr Parse(string text)
    {
        var tokens = new List<string>();
        int i = 0;
        while (i < text.Length)
        {
            if (char.IsWhiteSpace(text[i])) { i++; continue; }
            var m = TokenRx.Match(text, i);
            if (!m.Success || m.Index != i && text[i..m.Index].Trim().Length > 0)
                throw new ExprException($"Cannot read \"{text}\" at position {i + 1}.");
            if (m.Length == 0) throw new ExprException($"Cannot read \"{text}\" at position {i + 1}.");
            tokens.Add(m.Value.Trim());
            i = m.Index + m.Length;
        }
        if (tokens.Count == 0) throw new ExprException("Empty condition.");
        var p = new ExprParser(tokens);
        var e = p.ParseOr();
        if (p._pos < tokens.Count) throw new ExprException($"Unexpected '{tokens[p._pos]}' in \"{text}\".");
        return e;
    }

    public static double ParseTimeMs(string literal)
    {
        var body = literal.Substring(2).Replace("_", "");
        var negative = body.StartsWith('-');
        if (negative) body = body[1..];
        double ms = 0;
        int consumed = 0;
        foreach (Match m in TimePartRx.Matches(body))
        {
            if (m.Index != consumed) throw new ExprException($"Bad time literal {literal}.");
            var n = double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
            ms += m.Groups[2].Value.ToUpperInvariant() switch
            {
                "D" => n * 86_400_000,
                "H" => n * 3_600_000,
                "M" => n * 60_000,
                "S" => n * 1000,
                _ => n,
            };
            consumed = m.Index + m.Length;
        }
        if (consumed != body.Length || consumed == 0) throw new ExprException($"Bad time literal {literal}.");
        return negative ? -ms : ms;
    }

    private string? Peek => _pos < _tokens.Count ? _tokens[_pos] : null;
    private bool PeekKeyword(string kw) => Peek != null && Peek.Equals(kw, StringComparison.OrdinalIgnoreCase);
    private string Next() => _tokens[_pos++];

    private Expr ParseOr()
    {
        var left = ParseXor();
        while (PeekKeyword("OR")) { Next(); left = new BinaryExpr("OR", left, ParseXor()); }
        return left;
    }

    private Expr ParseXor()
    {
        var left = ParseAnd();
        while (PeekKeyword("XOR")) { Next(); left = new BinaryExpr("XOR", left, ParseAnd()); }
        return left;
    }

    private Expr ParseAnd()
    {
        var left = ParseNot();
        while (PeekKeyword("AND")) { Next(); left = new BinaryExpr("AND", left, ParseNot()); }
        return left;
    }

    private Expr ParseNot()
    {
        if (PeekKeyword("NOT")) { Next(); return new UnaryExpr("NOT", ParseNot()); }
        return ParseCompare();
    }

    private Expr ParseCompare()
    {
        var left = ParseAdd();
        if (Peek is "=" or "<>" or "<" or "<=" or ">" or ">=")
        {
            var op = Next();
            return new BinaryExpr(op, left, ParseAdd());
        }
        return left;
    }

    private Expr ParseAdd()
    {
        var left = ParseMul();
        while (Peek is "+" or "-") { var op = Next(); left = new BinaryExpr(op, left, ParseMul()); }
        return left;
    }

    private Expr ParseMul()
    {
        var left = ParseUnary();
        while (Peek is "*" or "/") { var op = Next(); left = new BinaryExpr(op, left, ParseUnary()); }
        return left;
    }

    private Expr ParseUnary()
    {
        if (Peek == "-") { Next(); return new UnaryExpr("-", ParseUnary()); }
        return ParsePrimary();
    }

    private Expr ParsePrimary()
    {
        var t = Peek ?? throw new ExprException("Condition ends too early.");
        Next();
        if (t == "(")
        {
            var e = ParseOr();
            if (Peek != ")") throw new ExprException("Missing ')'.");
            Next();
            return e;
        }
        if (t.StartsWith("T#", StringComparison.OrdinalIgnoreCase)) return new ConstExpr(ParseTimeMs(t));
        if (char.IsDigit(t[0])) return new ConstExpr(double.Parse(t, CultureInfo.InvariantCulture));
        if (t.Equals("TRUE", StringComparison.OrdinalIgnoreCase)) return new ConstExpr(true);
        if (t.Equals("FALSE", StringComparison.OrdinalIgnoreCase)) return new ConstExpr(false);
        if (t is "AND" or "OR" or "XOR" or "NOT" || !(char.IsLetter(t[0]) || t[0] == '_'))
            throw new ExprException($"Unexpected '{t}'.");
        return new IdentExpr(t);
    }
}
