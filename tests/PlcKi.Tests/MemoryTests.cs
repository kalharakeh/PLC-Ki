using PlcKi.Core.Memory;
using PlcKi.Core.Status;
using Xunit;

/// <summary>The machine memory: alarm occurrences kept on disk, notes, and the "happened before" line.</summary>
public class MemoryTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "plcki-memory-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private sealed class Feed
    {
        private readonly StatusEngine _engine = new(TestData.Model);
        public MachineStatus At(string snapshot, DateTimeOffset t, string state = "connected")
        {
            var snap = TestData.Snapshot(snapshot, t);
            return _engine.Evaluate(snap, new PlcLinkInfo(state, "192.168.10.10", null, t, TestData.Model.Tags.Count, 0));
        }
    }

    private const string WithA02 = "step10-xv101-not-open";
    private const string NoA02 = "step30-agitator-blocked";

    [Fact]
    public void Records_came_and_went_with_what_the_machine_was_doing()
    {
        var mem = new AlarmMemory(null);
        var f = new Feed();
        var t0 = DateTimeOffset.Now.AddMinutes(-10);
        mem.Observe(f.At(NoA02, t0));
        mem.Observe(f.At(WithA02, t0.AddSeconds(5)));
        mem.Observe(f.At(WithA02, t0.AddSeconds(6)));
        mem.Observe(f.At(NoA02, t0.AddSeconds(65)));

        var o = Assert.Single(mem.Occurrences(t0, t0.AddMinutes(5), "A02"));
        Assert.False(o.AtStart);
        Assert.Equal(TimeSpan.FromSeconds(60), o.Duration);
        Assert.Contains(o.Steps, s => s.Contains("step 10") && s.EndsWith("halted"));
    }

    [Fact]
    public void Quick_retrips_count_as_one_and_a_lost_link_ends_nothing()
    {
        var mem = new AlarmMemory(null);
        var f = new Feed();
        var t0 = DateTimeOffset.Now.AddMinutes(-10);
        mem.Observe(f.At(NoA02, t0));
        mem.Observe(f.At(WithA02, t0.AddSeconds(1)));
        mem.Observe(f.At(NoA02, t0.AddSeconds(2)));
        mem.Observe(f.At(WithA02, t0.AddSeconds(4)));        // back within 10 s: same occurrence
        mem.Observe(f.At(NoA02, t0.AddSeconds(5), "disconnected"));   // link down: ignored
        var o = Assert.Single(mem.Occurrences(t0, t0.AddMinutes(1), "A02"));
        Assert.Equal(1, o.Repeats);
        Assert.Null(o.Went);

        mem.Observe(f.At(NoA02, t0.AddSeconds(20)));
        mem.Observe(f.At(WithA02, t0.AddSeconds(60)));       // back after 40 s: a new one
        Assert.Equal(2, mem.Occurrences(t0, t0.AddMinutes(2), "A02").Count);
    }

    [Fact]
    public void Survives_a_restart_and_closes_what_went_while_not_watching()
    {
        var file = AlarmMemory.FileFor(_dir, "Mixing station 1");
        Assert.EndsWith(Path.Combine("memory", "mixing-station-1.json"), file);
        var t0 = DateTimeOffset.Now.AddHours(-1);

        var first = new AlarmMemory(file);
        var f = new Feed();
        first.Observe(f.At(NoA02, t0));
        first.Observe(f.At(WithA02, t0.AddSeconds(1)));
        first.Save();
        Assert.True(File.Exists(file));

        // Restart with the alarm still on: the same occurrence goes on
        var second = new AlarmMemory(file);
        second.Observe(new Feed().At(WithA02, t0.AddMinutes(5)));
        var o = Assert.Single(second.Occurrences(t0, t0.AddHours(1), "A02"));
        Assert.Null(o.Went);
        Assert.Equal(t0.AddSeconds(1), o.Came);

        // Restart after it went unseen: closed at the last time it was seen, marked as not exactly known
        second.Save();
        var third = new AlarmMemory(file);
        third.Observe(new Feed().At(NoA02, t0.AddMinutes(30)));
        o = Assert.Single(third.Occurrences(t0, t0.AddHours(1), "A02"));
        Assert.True(o.EndUnknown);
        Assert.Equal(t0.AddMinutes(5), o.Went);
    }

    [Fact]
    public void Notes_feed_the_happened_before_line()
    {
        var mem = new AlarmMemory(null);
        var f = new Feed();
        var t0 = DateTimeOffset.Now.AddDays(-3);
        mem.Observe(f.At(NoA02, t0));
        for (int i = 0; i < 3; i++)
        {
            var t = t0.AddHours(i * 10);
            mem.Observe(f.At(WithA02, t.AddMinutes(1)));
            mem.Observe(f.At(NoA02, t.AddMinutes(4)));
        }
        var all = mem.Occurrences(t0, DateTimeOffset.Now, "A02");
        Assert.Equal(3, all.Count);
        Assert.Equal(3, mem.Unnoted(t0, 10).Count);
        mem.SetNote(all[2].Id, "Dirty sensor", "Cleaned the sensor", null, "kassem");
        mem.SetNote(all[0].Id, "dirty sensor.", "Cleaned B23 lens", "Same as before", "ali");
        Assert.Single(mem.Unnoted(t0, 10));

        var h = mem.Summary("A02", DateTimeOffset.Now);
        Assert.Equal(3, h.Last7Days);
        Assert.Equal(2, h.Noted);
        Assert.Equal(TimeSpan.FromMinutes(3), h.AverageDuration);
        var cause = Assert.Single(h.Causes);
        Assert.Equal(2, cause.Count);
        Assert.Equal("Cleaned B23 lens", cause.LastFix);
        Assert.StartsWith("Happened 3 times in the last 30 days.", h.Insight);
        Assert.Contains("the cause was: dirty sensor; fixed by: Cleaned B23 lens", h.Insight);
        Assert.Contains("Most often noted cause", h.Insight);
        Assert.Contains("Usually comes in", h.Insight);

        var top = Assert.Single(mem.Top(t0, DateTimeOffset.Now));
        Assert.Equal(("A02", 3, 2), (top.AlarmId, top.Count, top.Noted));

        // Clearing every field removes the note
        mem.SetNote(all[0].Id, " ", null, "", "ali");
        Assert.Null(mem.Get(all[0].Id)!.Note);
        Assert.Equal("PLC-Ki has not seen this alarm before.", mem.Summary("A01", DateTimeOffset.Now).Insight);
    }

    [Fact]
    public void Ranks_by_count_or_by_down_time()
    {
        // A01 came three times for a minute each; A02 once, for half an hour
        Directory.CreateDirectory(_dir);
        var file = Path.Combine(_dir, "memory.json");
        var t0 = DateTimeOffset.Now.AddHours(-3);
        var occ = new List<AlarmOccurrence>();
        for (int i = 0; i < 3; i++)
            occ.Add(new AlarmOccurrence { Id = "a" + i, AlarmId = "A01", Text = "Short", Severity = "FAULT", Came = t0.AddMinutes(i * 10), Went = t0.AddMinutes(i * 10 + 1) });
        occ.Add(new AlarmOccurrence { Id = "b", AlarmId = "A02", Text = "Long", Severity = "FAULT", Came = t0.AddHours(1), Went = t0.AddHours(1).AddMinutes(30) });
        File.WriteAllText(file, System.Text.Json.JsonSerializer.Serialize(new { version = 1, occurrences = occ }, AlarmMemory.FileJson));
        var mem = new AlarmMemory(file);

        Assert.Equal(new[] { "A01", "A02" }, mem.Top(t0.AddMinutes(-1), DateTimeOffset.Now).Select(r => r.AlarmId));
        Assert.Equal(new[] { "A02", "A01" }, mem.Top(t0.AddMinutes(-1), DateTimeOffset.Now, byDowntime: true).Select(r => r.AlarmId));
    }

    [Fact]
    public void First_occurrence_says_so()
    {
        var mem = new AlarmMemory(null);
        var f = new Feed();
        var t0 = DateTimeOffset.Now.AddMinutes(-5);
        mem.Observe(f.At(NoA02, t0));
        mem.Observe(f.At(WithA02, t0.AddSeconds(1)));
        Assert.Equal("First time PLC-Ki has seen this alarm.", mem.Summary("A02", DateTimeOffset.Now).Insight);
    }
}
