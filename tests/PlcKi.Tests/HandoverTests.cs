using PlcKi.Core.Reports;
using Xunit;

/// <summary>Shift handover notes (review 3, item 47): one per shift, written during it and the next one, shown to the next shift.</summary>
public class HandoverTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "plcki-handover-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private static readonly ReportSettings Cfg = new();     // Early 06-14, Late 14-22, Night 22-06
    private static DateTimeOffset L(int day, int h, int m = 0)
    {
        var d = new DateTime(2026, 11, day, h, m, 0);
        return new DateTimeOffset(d, TimeZoneInfo.Local.GetUtcOffset(d));
    }
    private static readonly DateOnly Nov10 = new(2026, 11, 10);

    [Fact]
    public void A_note_can_be_written_during_its_shift_and_the_next_one_only()
    {
        Assert.True(HandoverNotes.CanWrite(Cfg, Nov10, 0, L(10, 13)));       // Early, during Early
        Assert.True(HandoverNotes.CanWrite(Cfg, Nov10, 0, L(10, 21)));       // Early, during Late
        Assert.False(HandoverNotes.CanWrite(Cfg, Nov10, 0, L(10, 23)));      // Early, during Night: closed
        Assert.False(HandoverNotes.CanWrite(Cfg, Nov10, 1, L(10, 13)));      // Late has not started
        Assert.True(HandoverNotes.CanWrite(Cfg, new DateOnly(2026, 11, 9), 2, L(10, 7)));   // last night, the morning after
    }

    [Fact]
    public void One_note_per_shift_kept_on_disk_changed_and_removed()
    {
        var file = HandoverNotes.FileFor(_dir, "helium");
        var notes = new HandoverNotes(file);
        notes.Set(Nov10, 0, "Early", L(10, 6), L(10, 14), "  XV102 sticks.  ", "anna", L(10, 13, 50));
        var again = new HandoverNotes(file);       // after a restart
        var n = again.Get(Nov10, 0)!;
        Assert.Equal(("XV102 sticks.", "anna", "Early"), (n.Text, n.By, n.Shift));

        again.Set(Nov10, 0, "Morning", L(10, 5, 30), L(10, 12, 30), "XV102 sticks, LS101 cleaned.", "ben", L(10, 14, 5));
        n = again.Get(Nov10, 0)!;
        Assert.Equal(("anna", "ben", "Morning"), (n.By, n.EditedBy, n.Shift));   // the shift as it is now
        Assert.Equal(1, again.Count);

        Assert.Null(again.Set(Nov10, 0, "Early", L(10, 6), L(10, 14), "   ", "ben", L(10, 14, 6)));
        Assert.Null(new HandoverNotes(file).Get(Nov10, 0));
        Assert.Throws<ArgumentException>(() => again.Set(Nov10, 0, "Early", L(10, 6), L(10, 14), new string('x', HandoverNotes.MaxLength + 1), "ben", L(10, 14)));
    }

    [Fact]
    public void The_next_shift_sees_the_note_in_its_first_two_hours()
    {
        var notes = new HandoverNotes(null);
        notes.Set(Nov10, 0, "Early", L(10, 6), L(10, 14), "Watch XV102.", "anna", L(10, 13, 55));
        Assert.Null(notes.ToShow(Cfg, L(10, 13, 58)));                 // still Early
        Assert.Equal("Watch XV102.", notes.ToShow(Cfg, L(10, 14, 1))!.Text);
        Assert.NotNull(notes.ToShow(Cfg, L(10, 15, 59)));
        Assert.Null(notes.ToShow(Cfg, L(10, 16, 1)));                   // after two hours: only on the report
        Assert.Null(notes.ToShow(Cfg, L(10, 22, 5)));                   // Night: Late left no note
    }
}
