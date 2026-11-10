using System.Text.Json;
using PlcKi.Core.Ask;
using PlcKi.Core.Reports;
using PlcKi.Core.Status;
using Xunit;

/// <summary>"This cycle" and "Compare with the previous cycle": the cycle list from the production log and the saved step values, and Ki reading saved values.</summary>
public class CycleTrendTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "plcki-cycles-" + Guid.NewGuid().ToString("N"));
    public CycleTrendTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private static readonly string[] Ids = { "Temp", "Step" };
    private ValueLog NewLog() => new(ValueLog.FolderFor(_dir, "helium"), Ids);

    private static DateTimeOffset L(int h, int m = 0, int s = 0)
    {
        var d = new DateTime(2026, 11, 10, h, m, s);
        return new DateTimeOffset(d, TimeZoneInfo.Local.GetUtcOffset(d));
    }

    /// <summary>The batch sequence's first step is 0 (Idle). One cycle: 0 until 11:00, 10, 20, 30 until 11:04, then idle; the next left Idle at 11:10.</summary>
    private static int StepAt(DateTimeOffset t) =>
        t < L(11, 0) ? 0 : t < L(11, 1) ? 10 : t < L(11, 3) ? 20 : t < L(11, 4) ? 30 : t < L(11, 10) ? 0 : t < L(11, 12) ? 10 : t < L(11, 14) ? 20 : 30;

    private ValueLog Seed()
    {
        var log = NewLog();
        for (var t = L(10, 55); t < L(11, 15); t = t.AddSeconds(5))
            log.Add(new Snapshot { Time = t, Values = { ["Step"] = (double)StepAt(t), ["Temp"] = 20 + StepAt(t) / 2.0 } });
        return log;
    }

    private static ProdRecord Cycle(DateTimeOffset end, double sec) => new(end, "cycle", Seq: "Batch", Sec: sec);

    [Fact]
    public void Finished_cycles_come_newest_first_with_their_start_and_end()
    {
        var list = Cycles.Finished(new[]
        {
            Cycle(L(9, 4), 240), new ProdRecord(L(9, 5), "alive"), Cycle(L(10, 4), 200),
            new ProdRecord(L(10, 5), "cycle", Seq: "Other", Sec: 30), new ProdRecord(L(10, 6), "cycle", Seq: "Batch", Sec: 0),
        }, "Batch");
        Assert.Equal(2, list.Count);
        Assert.Equal(L(10, 4).ToUnixTimeMilliseconds(), list[0].End);
        Assert.Equal(L(10, 4).ToUnixTimeMilliseconds() - 200_000, list[0].Start);
        Assert.Equal(L(9, 4).ToUnixTimeMilliseconds() - 240_000, list[1].Start);
        Assert.All(list, c => Assert.False(c.Running));
    }

    [Fact]
    public void The_cycle_running_now_is_found_from_when_the_sequence_left_its_first_step()
    {
        var log = Seed();
        var records = new[] { Cycle(L(11, 4), 240) };
        var list = Cycles.List(TestData.Model, new ReportSettings(), records, log, L(11, 15));
        Assert.Equal(2, list.Count);
        Assert.True(list[0].Running);
        Assert.InRange(Math.Abs(list[0].Start - L(11, 10).ToUnixTimeMilliseconds()), 0, 5_000);
        Assert.False(list[1].Running);
        Assert.Equal(L(11, 4).ToUnixTimeMilliseconds(), list[1].End);
    }

    [Fact]
    public void A_sequence_at_its_first_step_has_no_running_cycle_and_nothing_saved_means_no_cycles()
    {
        var log = Seed();
        var records = new[] { Cycle(L(11, 4), 240) };
        var idle = Cycles.List(TestData.Model, new ReportSettings(), records, log, L(11, 8));
        Assert.Single(idle);
        Assert.False(idle[0].Running);
        Assert.Empty(Cycles.List(TestData.Model, new ReportSettings(), records, new ValueLog(Path.Combine(_dir, "none"), Ids), L(11, 8)));
        Assert.Empty(Cycles.List(TestData.Model, new ReportSettings { CycleSequence = "NoSuchSequence" }, records, log, L(11, 8)));
    }

    [Fact]
    public void A_cycle_can_be_read_from_the_trend_api_by_its_start_and_end()
    {
        var log = Seed();
        var data = PlcKi.Server.Live.TrendEndpoints.Get(TestData.Model, new MachineHistory(), log, null, L(11, 15), null, null, null,
            L(11, 0).ToUnixTimeMilliseconds(), L(11, 4).ToUnixTimeMilliseconds(), new[] { "Temp" });
        Assert.Equal("disk", data.Source);
        var spans = data.Steps.First(s => s.Spans.Count > 0).Spans;
        Assert.Equal(new[] { 10, 20, 30 }, spans.Select(s => s.Step).Distinct().Take(3));       // then 0: the return to the first step ends the cycle
        Assert.InRange(spans[0].From, L(11, 0).ToUnixTimeMilliseconds() - 2000, L(11, 0).ToUnixTimeMilliseconds() + 2000);
        Assert.Contains(data.Series.Single().Points, p => p[1] == 35);        // Temp in step 30
    }

    [Fact]
    public void Ki_reads_days_back_from_the_saved_values_and_the_last_minutes_from_memory()
    {
        var log = Seed();
        var now = L(11, 15);
        var tools = new MachineTools(TestData.Model, () => new LiveView(null, null, new MachineHistory(), now, Saved: log));
        using var doc = JsonDocument.Parse(tools.Run("get_tag_history", JsonSerializer.SerializeToElement(new { ids = new[] { "Temp", "Level_High" }, minutes = 60 })));
        var root = doc.RootElement;
        Assert.Equal("saved values on disk", root.GetProperty("source").GetString());
        var temp = root.GetProperty("tags")[0];
        Assert.Equal("Temp", temp.GetProperty("id").GetString());
        Assert.StartsWith("20", temp.GetProperty("min").GetString());
        Assert.StartsWith("35", temp.GetProperty("max").GetString());
        Assert.True(temp.GetProperty("slices").GetArrayLength() > 5);
        Assert.Contains("not a number", root.GetProperty("unknown")[0].GetString());      // a Bool is not saved on disk
    }

    [Fact]
    public void Without_saved_values_Ki_still_answers_from_memory()
    {
        var now = L(11, 15);
        var history = new MachineHistory();
        history.Add(TestData.Snapshot("step30-mix-heat", now.AddMinutes(-1)), TestData.Status("step30-mix-heat"));
        var tools = new MachineTools(TestData.Model, () => new LiveView(null, null, history, now));
        using var doc = JsonDocument.Parse(tools.Run("get_tag_history", JsonSerializer.SerializeToElement(new { ids = new[] { "Temp" }, minutes = 1440 })));
        Assert.False(doc.RootElement.TryGetProperty("source", out _));
        Assert.Equal("Temp", doc.RootElement.GetProperty("tags")[0].GetProperty("id").GetString());
    }
}
