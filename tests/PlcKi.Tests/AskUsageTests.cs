using PlcKi.Server.Ask;
using Xunit;

/// <summary>The monthly Ask counter (review 54): sums per month, prices per model, keeps going after a broken file.</summary>
public class AskUsageTests : IDisposable
{
    private readonly string _file = Path.Combine(Path.GetTempPath(), "plcki-usage-" + Guid.NewGuid().ToString("N") + ".json");
    public void Dispose() { try { File.Delete(_file); } catch { /* ignore */ } }

    [Fact]
    public void Prices_follow_the_model_that_answered()
    {
        // Opus 5.5: $4 in, $20 out, $5 cache write, $0.20 cache read per million tokens
        Assert.Equal(4 + 20 + 5 + 0.2, AskUsage.Cost("claude-opus-5-5", 1_000_000, 1_000_000, 1_000_000, 1_000_000), 9);
        Assert.Equal(2.0, AskUsage.Cost("claude-sonnet-5-5", 1_000_000, 0, 0, 0), 9);
        Assert.Equal(40.0, AskUsage.Cost("\"claude-fable-5-2\"", 0, 1_000_000, 0, 0), 9);     // quoted as the SDK prints it
        // An unknown model is priced like the dearest one, so the estimate is never low
        Assert.Equal(AskUsage.PriceOf("claude-fable-5-1").Output, AskUsage.PriceOf("claude-something-new").Output);
        Assert.Equal("claude-opus-5", AskUsage.PriceOf("claude-opus-5").Id);           // not mixed up with Opus 5.5
        Assert.All(AskUsage.Models, m => Assert.Equal(m.Input * 1.25, m.CacheWrite, 9));
    }

    [Fact]
    public void Counts_per_month_and_starts_again_in_the_next()
    {
        var now = new DateTimeOffset(2026, 11, 30, 23, 0, 0, TimeSpan.FromHours(1));
        var u = new AskUsage(_file, () => now);
        u.Question();
        u.Add("claude-opus-5-5", 1000, 100, 0, 0);
        u.Add("claude-opus-5-5", 1000, 100, 0, 0);
        Assert.Equal("2026-11", u.ThisMonth().Month);
        Assert.Equal(2000, u.ThisMonth().Input);
        Assert.Null(u.LimitReached(0));                           // 0 = no limit
        Assert.Null(u.LimitReached(1));
        var reached = u.LimitReached(u.ThisMonth().Cost);
        Assert.NotNull(reached);
        Assert.Matches(@"about \$\d+\.\d\d this month", reached);     // a decimal point whatever the PC's language

        now = now.AddHours(2);                                     // December
        Assert.Equal(0, u.ThisMonth().Questions);
        Assert.Null(u.LimitReached(0.001));
        u.Question();
        var again = new AskUsage(_file, () => now);
        Assert.Equal(new[] { "2026-12", "2026-11" }, again.Months().Select(m => m.Month));
        Assert.Equal(1, again.ThisMonth().Questions);
    }

    [Fact]
    public void A_broken_file_does_not_stop_ask()
    {
        File.WriteAllText(_file, "{ not json");
        var u = new AskUsage(_file);
        Assert.Equal(0, u.ThisMonth().Questions);
        u.Question();
        Assert.Equal(1, new AskUsage(_file).ThisMonth().Questions);
    }
}
