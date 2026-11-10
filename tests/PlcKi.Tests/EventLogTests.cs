using System.Text.Json;
using PlcKi.Core.Ask;
using PlcKi.Core.Status;
using PlcKi.Core.Storage;
using PlcKi.Server.Live;
using PlcKi.Server.Storage;
using Xunit;

/// <summary>Machine events on disk: a file per day, steps kept apart, late events filed by their own time, the limits.</summary>
public class EventLogTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "plcki-events-" + Guid.NewGuid().ToString("N"));
    public EventLogTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private static DateTimeOffset L(int day, int h, int m = 0, int s = 0)
    {
        var d = new DateTime(2026, 11, day, h, m, s);
        return new DateTimeOffset(d, TimeZoneInfo.Local.GetUtcOffset(d));
    }

    private string Machine => EventLog.FolderFor(_dir, "helium");
    private static MachineEvent Ev(DateTimeOffset t, string kind, string text = "x") => new(t, kind, text);

    [Fact]
    public void Steps_and_outputs_go_to_their_own_folder_and_each_event_to_its_own_day()
    {
        var log = new EventLog(Machine);
        log.Write(new[]
        {
            Ev(L(9, 23, 50), "alarm") with { Alarm = "A02" },
            Ev(L(9, 23, 55), "step") with { Seq = "S1", Step = 10 },
            Ev(L(10, 0, 5), "output"),
            Ev(L(10, 0, 6), "halt"),
        });
        Assert.True(File.Exists(Path.Combine(Machine, "2026-11-09.jsonl")));
        Assert.True(File.Exists(Path.Combine(Machine, "2026-11-10.jsonl")));
        Assert.True(File.Exists(Path.Combine(Machine, "steps", "2026-11-09.jsonl")));
        Assert.True(File.Exists(Path.Combine(Machine, "steps", "2026-11-10.jsonl")));

        // A night shift reads two days, in time order, with what each event is about
        var all = new EventLog(Machine).Read(L(9, 22), L(10, 6));
        Assert.Equal(new[] { "alarm", "step", "output", "halt" }, all.Select(e => e.Kind));
        Assert.Equal("A02", all[0].Alarm);
        Assert.Equal(("S1", 10), (all[1].Seq, all[1].Step));
    }

    [Fact]
    public void Asking_for_rare_kinds_does_not_read_the_steps()
    {
        var log = new EventLog(Machine);
        log.Write(new[] { Ev(L(10, 8), "step"), Ev(L(10, 8, 1), "cpu"), Ev(L(10, 8, 2), "link") });
        Assert.Equal(new[] { "cpu", "link" }, log.Read(L(10, 0), L(10, 23), new HashSet<string> { "cpu", "link" }).Select(e => e.Kind));
        Assert.Single(log.Read(L(10, 0), L(10, 23), new HashSet<string> { "step" }));
    }

    [Fact]
    public void A_late_event_from_Note_is_filed_by_its_own_time_and_read_in_order()
    {
        var log = new EventLog(Machine);
        var h = new MachineHistory();
        h.Recorded = log.Write;
        h.Note(Ev(L(10, 9, 0, 10), "write"));
        h.Note(Ev(L(9, 23, 59, 59), "alarm"));      // an alarm that came and went between two reads, around midnight
        h.Note(Ev(L(10, 9, 0, 5), "alarm"));
        Assert.True(File.Exists(Path.Combine(Machine, "2026-11-09.jsonl")));
        var read = log.Read(L(9, 0), L(10, 23));
        Assert.Equal(new[] { L(9, 23, 59, 59), L(10, 9, 0, 5), L(10, 9, 0, 10) }, read.Select(e => e.Time));
    }

    [Fact]
    public void A_line_cut_off_by_a_power_loss_is_skipped()
    {
        var log = new EventLog(Machine);
        log.Write(new[] { Ev(L(10, 8), "alarm") });
        File.AppendAllText(Path.Combine(Machine, "2026-11-10.jsonl"), "{\"t\":\"2026-11-10T08:0");
        Assert.Single(log.Read(L(10, 0), L(10, 23)));
    }

    [Fact]
    public void Storage_keeps_events_and_steps_for_their_own_number_of_days()
    {
        var log = new EventLog(Machine);
        log.Write(new[] { Ev(L(1, 8), "alarm"), Ev(L(1, 8), "step"), Ev(L(8, 8), "alarm"), Ev(L(8, 8), "step") });
        var now = L(10, 12);
        var s = new StorageService(_dir, () => new RetentionLimits { EventDays = 7, StepEventDays = 1 });
        var removed = s.Prune(now);
        Assert.Contains(removed, r => r.Kind == "events" && r.Count == 1);
        Assert.Contains(removed, r => r.Kind == "steps" && r.Count == 2);
        Assert.Equal(new[] { "alarm" }, log.Read(L(1, 0), now).Select(e => e.Kind));

        var rows = s.Rows(now);
        Assert.Equal(1, rows.Single(r => r.Kind == "events").Count);
        Assert.Equal(0, rows.Single(r => r.Kind == "steps").Count);
    }

    [Fact]
    public void Clearing_steps_leaves_the_machine_events()
    {
        var log = new EventLog(Machine);
        log.Write(new[] { Ev(L(10, 8), "alarm"), Ev(L(10, 8), "step") });
        var s = new StorageService(_dir, () => RetentionLimits.Default);
        var done = s.Clear("steps", "kassem", L(10, 12));
        Assert.Equal(1, done.Count);
        Assert.Equal(new[] { "alarm" }, log.Read(L(10, 0), L(10, 23)).Select(e => e.Kind));
    }

    [Fact]
    public void The_limits_have_plain_ranges()
    {
        Assert.Null(RetentionLimits.Default.Problem());
        Assert.Equal(400, RetentionLimits.Default.EventDays);
        Assert.Equal(30, RetentionLimits.Default.StepEventDays);
        Assert.NotNull((RetentionLimits.Default with { EventDays = 3 }).Problem());
        Assert.NotNull((RetentionLimits.Default with { StepEventDays = 0 }).Problem());
    }

    [Fact]
    public void Without_a_window_the_api_reads_one_day_of_steps_but_a_month_of_the_rest()
    {
        var now = L(10, 12);
        Assert.Equal(now.AddDays(-1), EventEndpoints.Window(null, null, null, null, now).From);
        Assert.Equal(now.AddDays(-30), EventEndpoints.Window(null, null, null, EventEndpoints.Kinds("cpu,link"), now).From);
        Assert.Equal(now.AddDays(-7), EventEndpoints.Window(null, null, 7, null, now).From);
        var (f, t) = EventEndpoints.Window("2026-11-10T06:00", "2026-11-10T14:00", null, null, now);
        Assert.Equal((L(10, 6), L(10, 14)), (f, t));
    }

    [Fact]
    public void Ki_reads_a_shift_from_disk_and_leaves_out_old_steps_first()
    {
        var log = new EventLog(Machine);
        var list = new List<MachineEvent> { Ev(L(10, 6, 1), "alarm", "A02 came") };
        for (int i = 0; i < 400; i++) list.Add(Ev(L(10, 6, 2).AddSeconds(i * 30), "step", "step " + i));
        list.Add(Ev(L(10, 13, 50), "halt", "halted"));
        log.Write(list);

        // Memory is empty (a restart): the shift still comes from the day files
        var tools = new MachineTools(TestData.Model, () => new LiveView(null, null, new MachineHistory(), L(10, 15), Events: log));
        using var input = JsonDocument.Parse("{\"from\":\"2026-11-10 06:00\",\"to\":\"2026-11-10 14:00\"}");
        using var doc = JsonDocument.Parse(tools.Run("get_recent_events", input.RootElement));
        var events = doc.RootElement.GetProperty("events").EnumerateArray().Select(e => e.GetProperty("text").GetString()).ToList();
        Assert.Equal(150, events.Count);
        Assert.Equal("A02 came", events[0]);           // the rare events stay
        Assert.Equal("halted", events[^1]);
        Assert.Equal("step 399", events[^2]);          // the newest steps stay
        Assert.Equal(400 - 148, doc.RootElement.GetProperty("stepsAndOutputsLeftOut").GetInt32());
    }
}
