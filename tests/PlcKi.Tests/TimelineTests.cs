using PlcKi.Core.Reports;
using PlcKi.Core.Status;
using PlcKi.Server.Reports;
using Xunit;

/// <summary>The timeline strip (review 3, item 40): which events get a mark, and the windows This shift, 24 h, 7 days.</summary>
public class TimelineTests
{
    private static MachineEvent Ev(string kind, string text) => new(DateTimeOffset.Now, kind, text);

    [Theory]
    [InlineData("alarm", "A02 came: Tank level high (FAULT).", "alarm")]
    [InlineData("alarm", "A05 came: Agitator overload warning (WARNING).", "warn")]
    [InlineData("alarm", "A01 was already active when watching started: Emergency stop pressed.", null)]   // not new: every restart says it
    [InlineData("alarm", "A03 blipped: on for only 40 ms, too short for the normal reads: Door open.", "alarm")]
    [InlineData("alarm", "A02 went: Tank level high.", null)]
    [InlineData("halt", "Batch sequence halted in step 30 because the agitator is not running.", "halt")]
    [InlineData("halt", "Batch sequence is no longer halted.", null)]
    [InlineData("cpu", "The PLC went to STOP: the program stopped running.", "stop")]
    [InlineData("cpu", "The PLC was in STOP when watching started: the program is not running.", null)]
    [InlineData("cpu", "The PLC is back in RUN: the program is running again.", null)]
    [InlineData("link", "Lost the PLC connection (error): timeout", "link")]
    [InlineData("link", "The PLC connection dropped for 4 s and came back.", "link")]
    [InlineData("link", "Connected to the PLC at 192.168.0.1.", null)]
    [InlineData("write", "tester changed XV101 from FALSE to TRUE on PLCSIM.", "write")]
    [InlineData("step", "Batch: step 10 \"Fill\" -> step 20 \"Heat\".", null)]
    [InlineData("output", "Heater H101 switched on.", null)]
    public void Only_what_needs_noticing_gets_a_mark(string kind, string text, string? mark)
    {
        Assert.Equal(mark, EventLog.Mark(Ev(kind, text)));
        if (mark != null) Assert.Contains(kind, EventLog.Marked);   // the strip reads only these kinds
    }

    [Fact]
    public void The_strip_windows_are_this_shift_24_hours_and_7_days()
    {
        var cfg = new ReportSettings();     // Early 06-14, Late 14-22, Night 22-06
        var now = new DateTimeOffset(2026, 11, 10, 13, 5, 0, TimeSpan.FromHours(1));
        var shift = ReportEndpoints.TimelineWindow(cfg, null, now);
        Assert.Equal(("shift", "Early shift"), (shift.Window, shift.Label));
        Assert.Equal((6, 14), (shift.From.Hour, shift.To.Hour));
        Assert.Equal((new DateOnly(2026, 11, 10), 0), (shift.Day!.Value, shift.Index));

        var night = ReportEndpoints.TimelineWindow(cfg, "shift", now.AddHours(-10));   // 03:05: the night shift that began yesterday
        Assert.Equal(("Night shift", new DateOnly(2026, 11, 9)), (night.Label, night.Day!.Value));

        Assert.Equal(now.AddHours(-24), ReportEndpoints.TimelineWindow(cfg, "24h", now).From);
        Assert.Equal(now.AddDays(-7), ReportEndpoints.TimelineWindow(cfg, "7d", now).From);

        var none = ReportEndpoints.TimelineWindow(new ReportSettings { Shifts = new() }, "shift", now);
        Assert.Equal(("Last 8 hours", now.AddHours(-8)), (none.Label, none.From));
    }
}
