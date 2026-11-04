using System.Text.Json;
using PlcKi.Core.Ask;
using PlcKi.Core.Model;
using PlcKi.Core.Status;
using Xunit;

/// <summary>Fast capture: the recording of every change and the seconds cut around each alarm.</summary>
public class FastCaptureTests
{
    private static MachineModel Model() => new()
    {
        Name = "Test",
        Tags =
        {
            new TagDef { Id = "AlmLeak", Symbol = "\"DB_Alarms\".Leak", Type = "Bool" },
            new TagDef { Id = "PartPresent", Symbol = "\"DB_IO\".B23", Type = "Bool", Label = "Part present" },
            new TagDef { Id = "Pressure", Symbol = "\"DB_IO\".PT1", Type = "Real", Unit = "bar", Decimals = 1 },
            new TagDef { Id = "Quiet", Symbol = "\"DB_IO\".Quiet", Type = "Bool" },
            new TagDef { Id = "Limit", Symbol = "\"DB_Par\".Limit", Type = "Real" },
        },
        Alarms =
        {
            new AlarmDef
            {
                Id = "A1", Tag = "AlmLeak", Text = "Leak test failed",
                Checks = { new CondDef { Expr = "Pressure < Limit", Text = "Pressure below the limit" } },
            },
        },
    };

    [Fact]
    public void Recorder_keeps_only_changes_and_forgets_after_a_break()
    {
        var r = new FastRecorder();
        var t = DateTimeOffset.Now;
        Assert.NotNull(r.Add(t, "X", true));
        Assert.Null(r.Add(t.AddMilliseconds(100), "X", true));
        var off = r.Add(t.AddMilliseconds(200), "X", false);
        Assert.True(off!.HadPrev);
        Assert.Equal(true, off.Prev);

        r.Break(t.AddMilliseconds(300));
        var again = r.Add(t.AddMilliseconds(900), "X", false);
        Assert.NotNull(again);
        Assert.False(again!.HadPrev);       // after a gap the first value is not an edge
        Assert.Single(r.Gaps(t, t.AddSeconds(1)));
    }

    [Fact]
    public void Recorder_drops_old_points_but_remembers_the_value_before()
    {
        var r = new FastRecorder(TimeSpan.FromSeconds(10));
        var t = DateTimeOffset.Now;
        r.Add(t, "X", 1.0);
        r.Add(t.AddSeconds(5), "X", 2.0);
        r.Add(t.AddSeconds(20), "Y", true);      // pushes X's points out
        Assert.Equal(1, r.Count);
        Assert.Equal(2.0, r.ValuesBefore(t.AddSeconds(19))["X"]);
    }

    [Fact]
    public void Snapshot_feed_skips_tags_the_subscription_covers()
    {
        var r = new FastRecorder();
        var snap = new Snapshot { Time = DateTimeOffset.Now, Values = { ["A"] = 1.0, ["B"] = 2.0 } };
        r.AddSnapshot(snap, new HashSet<string> { "A" });
        Assert.Equal(new[] { "B" }, r.Window(snap.Time.AddSeconds(-1), snap.Time.AddSeconds(1)).Select(p => p.TagId));
    }

    /// <summary>A short part-present pulse and a pressure drop before a leak alarm that only blips for 300 ms.</summary>
    private static (AlarmCaptureBook Book, DateTimeOffset Alarm) Scenario(FastRecorder r, string? folder = null)
    {
        var book = new AlarmCaptureBook(Model(), r, folder: folder);
        var t = DateTimeOffset.Now.AddSeconds(-30);
        r.SetMode("subscription", 100);
        r.Add(t, "AlmLeak", false);
        r.Add(t, "PartPresent", false);
        r.Add(t, "Pressure", 6.0);
        r.Add(t, "Quiet", true);
        r.Add(t, "Limit", 5.0);
        r.Add(t.AddSeconds(20), "Pressure", 5.5);
        r.Add(t.AddSeconds(23), "PartPresent", true);
        r.Add(t.AddSeconds(23.08), "PartPresent", false);     // 80 ms pulse
        r.Add(t.AddSeconds(24.9), "Pressure", 4.2);
        var alarm = t.AddSeconds(25);
        r.Add(alarm, "AlmLeak", true);
        r.Add(alarm.AddMilliseconds(300), "AlmLeak", false);
        return (book, alarm);
    }

    [Fact]
    public void Capture_waits_for_the_after_time_then_explains_the_seconds_before()
    {
        var r = new FastRecorder();
        var (book, alarm) = Scenario(r);

        Assert.Empty(book.Check(alarm.AddSeconds(1), 1000));          // still recording the 2 s after
        Assert.Equal(new[] { "A1" }, book.Recording());
        var c = Assert.Single(book.Check(alarm.AddSeconds(2.5), 1000));

        Assert.Equal("A1", c.AlarmId);
        Assert.Equal(300, c.OnMs);
        Assert.True(c.Blip);
        Assert.Equal("AlmLeak", c.Tracks[0].Tag);                      // the alarm bit first
        Assert.True(c.Tracks.Single(k => k.Tag == "Pressure").Related); // in the alarm's check
        Assert.Contains(c.Tracks, k => k.Tag == "Limit" && k.Related && k.Changes.Count == 0);   // related, did not change
        Assert.DoesNotContain(c.Tracks, k => k.Tag == "Quiet");          // did not change, not related
        var pulse = Assert.Single(c.Pulses);
        Assert.Equal("PartPresent", pulse.Tag);
        Assert.Equal(80, pulse.Ms);
        Assert.Equal(-2.0, pulse.T, 3);
        Assert.Contains(c.Summary, s => s.Contains("only 300 ms"));
        Assert.Contains(c.Summary, s => s.StartsWith("Right before it:") && s.Contains("Pressure became 4.2"));
        Assert.Contains(c.Summary, s => s.Contains("short pulse") && s.Contains("80 ms"));
        Assert.Equal(-0.1, c.Tracks.Single(k => k.Tag == "Pressure").Changes[^1].T, 3);
        Assert.Same(c, book.Latest("a1"));
    }

    [Fact]
    public void An_alarm_already_on_after_connecting_is_not_a_new_capture()
    {
        var r = new FastRecorder();
        var book = new AlarmCaptureBook(Model(), r);
        var t = DateTimeOffset.Now;
        r.Add(t, "AlmLeak", true);           // first value: no edge
        Assert.Empty(book.Check(t.AddSeconds(5), 1000));
        Assert.Empty(book.Recording());
    }

    [Fact]
    public void Captures_are_kept_on_disk_for_the_next_start()
    {
        var dir = Path.Combine(Path.GetTempPath(), "plcki-captures-" + Guid.NewGuid().ToString("N"));
        try
        {
            var (book, alarm) = Scenario(new FastRecorder(), dir);
            var c = Assert.Single(book.Check(alarm.AddSeconds(3), 1000));
            var reloaded = new AlarmCaptureBook(Model(), new FastRecorder(), folder: dir);
            var again = reloaded.Get(c.Id);
            Assert.NotNull(again);
            Assert.Equal(c.Summary, again!.Summary);
            Assert.Equal(c.Tracks.Count, again.Tracks.Count);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void Reads_only_capture_says_short_changes_can_be_missing()
    {
        var r = new FastRecorder();
        var (book, alarm) = Scenario(r);
        r.SetMode("reads", 1000, "the PLC did not accept a subscription.");
        var c = Assert.Single(book.Check(alarm.AddSeconds(3), 1000));
        Assert.Equal("reads", c.Mode);
        Assert.StartsWith("Only from the normal reads (every 1 s)", c.Summary[0]);
        Assert.Contains("did not accept", c.Summary[0]);
    }

    [Fact]
    public void Blips_go_into_the_event_list_in_time_order()
    {
        var h = new MachineHistory();
        var t = DateTimeOffset.Now;
        h.Note(new MachineEvent(t, "alarm", "late"));
        h.Note(new MachineEvent(t.AddSeconds(-5), "alarm", "early"));
        Assert.Equal(new[] { "early", "late" }, h.Events(TimeSpan.FromMinutes(1), t.AddSeconds(1)).Select(e => e.Text));
    }

    [Fact]
    public void Ask_tool_returns_the_capture()
    {
        var r = new FastRecorder();
        var (book, alarm) = Scenario(r);
        book.Check(alarm.AddSeconds(3), 1000);
        var tools = new MachineTools(Model(), () => new LiveView(null, null, new MachineHistory(), DateTimeOffset.Now, book, r));

        using var one = JsonDocument.Parse(tools.Run("get_alarm_capture", JsonDocument.Parse("{\"alarm\":\"A1\"}").RootElement));
        Assert.True(one.RootElement.GetProperty("blip").GetBoolean());
        Assert.Contains(one.RootElement.GetProperty("tracks").EnumerateArray(), k => k.GetProperty("tag").GetString() == "PartPresent");

        using var list = JsonDocument.Parse(tools.Run("get_alarm_capture", JsonDocument.Parse("{}").RootElement));
        Assert.Equal(1, list.RootElement.GetProperty("captures").GetArrayLength());

        using var none = JsonDocument.Parse(tools.Run("get_alarm_capture", JsonDocument.Parse("{\"alarm\":\"A9\"}").RootElement));
        Assert.True(none.RootElement.TryGetProperty("error", out _));
    }
}

public class FastCaptureStartTests
{
    [Fact]
    public void First_values_after_start_are_not_counted_as_changes()
    {
        var model = new MachineModel
        {
            Tags = { new TagDef { Id = "Alm", Symbol = "a", Type = "Bool" }, new TagDef { Id = "X", Symbol = "x", Type = "Real" } },
            Alarms = { new AlarmDef { Id = "A1", Tag = "Alm", Checks = { new CondDef { Expr = "X < 5", Text = "x" } } } },
        };
        var r = new FastRecorder();
        var book = new AlarmCaptureBook(model, r);
        var t = DateTimeOffset.Now.AddSeconds(-10);
        r.Add(t, "Alm", false);          // PLC-Ki just started: first values, 3 s before the alarm
        r.Add(t, "X", 7.0);
        r.Add(t.AddSeconds(3), "Alm", true);
        var c = Assert.Single(book.Check(t.AddSeconds(6), 1000));
        var x = c.Tracks.Single(k => k.Tag == "X");
        Assert.Empty(x.Changes);          // shown (it is in the check), but it did not change
        Assert.Equal(7.0, x.Start);
        Assert.Contains(c.Summary, s => s.StartsWith("No other model tag changed"));
    }
}
