using PlcKi.Core.Expressions;
using Xunit;

public class ExprTests
{
    private static object? Eval(string expr, params (string, object?)[] vars)
    {
        var d = vars.ToDictionary(v => v.Item1, v => v.Item2);
        return ExprParser.Parse(expr).Eval(id => d.GetValueOrDefault(id));
    }

    [Fact]
    public void Precedence_and_not()
    {
        Assert.Equal(true, Eval("A OR B AND C", ("A", true), ("B", false), ("C", false)));
        Assert.Equal(false, Eval("(A OR B) AND C", ("A", true), ("B", false), ("C", false)));
        Assert.Equal(true, Eval("NOT A AND B", ("A", false), ("B", true)));
        Assert.Equal(true, Eval("Level >= LevelA", ("Level", 40.0), ("LevelA", 40.0)));
        Assert.Equal(false, Eval("Level > 20 AND EStop", ("Level", 15.0), ("EStop", true)));
        Assert.Equal(7.0, Eval("1 + 2 * 3"));
    }

    [Fact]
    public void Unknown_values_spread_like_sql()
    {
        Assert.Equal(false, Eval("A AND B", ("A", false)));
        Assert.Null(Eval("A AND B", ("A", true)));
        Assert.Equal(true, Eval("A OR B", ("A", true)));
        Assert.Null(Eval("NOT B"));
        Assert.Null(Eval("Level > 3"));
    }

    [Theory]
    [InlineData("T#120S", 120_000)]
    [InlineData("T#27S_144MS", 27_144)]
    [InlineData("T#1M30S", 90_000)]
    [InlineData("T#100MS", 100)]
    [InlineData("T#2H", 7_200_000)]
    public void Time_literals(string lit, double ms) => Assert.Equal(ms, ExprParser.ParseTimeMs(lit));

    [Fact]
    public void Lists_identifiers()
    {
        var ids = ExprParser.Parse("Temp >= TempSP AND StepTime >= MixTime AND NOT Temp_WireBreak").Identifiers().ToList();
        Assert.Equal(new[] { "Temp", "TempSP", "StepTime", "MixTime", "Temp_WireBreak" }, ids);
    }

    [Theory]
    [InlineData("A AND")]
    [InlineData("(A OR B")]
    [InlineData("A $ B")]
    public void Bad_syntax_is_reported(string expr) => Assert.Throws<ExprException>(() => ExprParser.Parse(expr));
}
