using PlcKi.Core.Status;
using Xunit;

/// <summary>The event list and value history the assistant uses for "what happened?" questions.</summary>
public class HistoryTests
{
    [Fact]
    public void First_read_records_where_the_machine_was()
    {
        var h = new MachineHistory();
        var t0 = DateTimeOffset.Now;
        var empty = new Snapshot { Time = t0 };
        h.Add(empty, TestData.Evaluate(empty, "connecting"));
        var snap = TestData.Snapshot("step10-xv101-not-open", t0.AddSeconds(1));
        h.Add(snap, TestData.Evaluate(snap));

        var events = h.Events(TimeSpan.FromMinutes(5), t0.AddSeconds(2));
        Assert.Contains(events, e => e.Kind == "monitor");
        Assert.Contains(events, e => e.Kind == "link" && e.Text.Contains("Connected"));
        Assert.Contains(events, e => e.Kind == "step" && e.Text.Contains("step 10") && e.Text.Contains("halted"));
        Assert.Contains(events, e => e.Kind == "alarm" && e.Text.StartsWith("A02 was already active"));
    }

    [Fact]
    public void Changes_between_reads_become_events()
    {
        var h = new MachineHistory();
        var t0 = DateTimeOffset.Now;
        var a = TestData.Snapshot("step10-xv101-not-open", t0);
        var b = TestData.Snapshot("step30-agitator-blocked", t0.AddSeconds(5));
        h.Add(a, TestData.Evaluate(a));
        h.Add(b, TestData.Evaluate(b));

        var events = h.Events(TimeSpan.FromMinutes(5), t0.AddSeconds(6));
        Assert.Contains(events, e => e.Kind == "step" && e.Text.Contains("step 10") && e.Text.Contains("-> step 30"));
        Assert.Contains(events, e => e.Kind == "alarm" && e.Text.StartsWith("A02 went"));
        Assert.Contains(events, e => e.Kind == "halt" && e.Text.Contains("no longer halted"));
        Assert.Contains(events, e => e.Kind == "blocked" && e.Text.Contains("M201"));
    }

    [Fact]
    public void Series_keeps_change_points_and_drops_old_values()
    {
        var h = new MachineHistory(TimeSpan.FromMinutes(1));
        var level = TestData.Model.Tags.Single(t => t.Id == "Level");
        var t0 = DateTimeOffset.Now.AddMinutes(-3);
        var values = new[] { 10.0, 10.0, 12.5, 12.5, 15.0 };
        for (int i = 0; i < 200; i++)
        {
            var s = new Snapshot { Time = t0.AddSeconds(i) };
            s.Values["Level"] = values[Math.Min(i / 40, 4)];
            h.Add(s, TestData.Evaluate(s));
        }
        var now = t0.AddSeconds(200);
        Assert.True(h.OldestValueTime >= now.AddMinutes(-1).AddSeconds(-1));

        var points = h.Series(level, TimeSpan.FromMinutes(10), now);
        // Window start (12.5 %) and the change to 15 %
        Assert.Equal(new[] { "12.5 %", "15.0 %" }, points.Select(p => p.Text));
        var stats = h.Stats(level, TimeSpan.FromMinutes(10), now);
        Assert.Equal(12.5, stats!.Value.Min);
        Assert.Equal(15.0, stats.Value.Max);
    }
}

/// <summary>The charts' data: values over time, the step each sequence was in, alarm marks.</summary>
public class TrendTests
{
    [Fact]
    public void Trend_has_values_steps_and_alarm_marks()
    {
        var h = new MachineHistory(TimeSpan.FromMinutes(5));
        var t0 = DateTimeOffset.Now.AddSeconds(-30);
        var a = TestData.Snapshot("step10-xv101-not-open", t0);
        var b = TestData.Snapshot("step30-agitator-blocked", t0.AddSeconds(10));
        var c = TestData.Snapshot("step30-agitator-blocked", t0.AddSeconds(20));
        foreach (var s in new[] { a, b, c }) h.Add(s, TestData.Evaluate(s));

        var tr = Trend.Build(TestData.Model, h, TimeSpan.FromMinutes(5), t0.AddSeconds(25));
        Assert.Contains(tr.Series, x => x.Id == "Level" && x.Points.Count == 3);
        Assert.DoesNotContain(tr.Series, x => x.Id == "Step");
        var spans = tr.Steps.Single(x => x.Id == "Batch").Spans;
        Assert.Equal(new[] { 10, 30 }, spans.Select(x => x.Step));
        Assert.Equal(c.Time.ToUnixTimeMilliseconds(), spans[^1].To);
        Assert.Contains(tr.Alarms, x => x.Id == "A02" && !x.Came);
    }

    [Fact]
    public void Long_history_is_thinned_but_keeps_spikes()
    {
        var level = TestData.Model.Tags.Single(t => t.Id == "Level");
        var h = new MachineHistory(TimeSpan.FromMinutes(60));
        var t0 = DateTimeOffset.Now.AddMinutes(-50);
        var baseSnap = TestData.Snapshot("step30-agitator-blocked", t0);
        for (int i = 0; i < 3000; i++)
        {
            var v = new Dictionary<string, object?>(baseSnap.Values) { ["Level"] = i == 1234 ? 99.0 : 20.0 };
            var s = new Snapshot { Time = t0.AddSeconds(i), Values = v };
            h.Add(s, TestData.Evaluate(s));
        }
        var tr = Trend.Build(TestData.Model, h, TimeSpan.FromMinutes(60), t0.AddSeconds(3000), new[] { "Level" }, buckets: 100);
        var pts = tr.Series.Single().Points;
        Assert.True(pts.Count <= 200);
        Assert.Contains(pts, p => p[1] == 99.0);
    }
}
