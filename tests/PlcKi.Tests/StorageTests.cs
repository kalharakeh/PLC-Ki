using PlcKi.Core.Memory;
using PlcKi.Core.Reports;
using PlcKi.Core.Status;
using PlcKi.Core.Storage;
using PlcKi.Server.Live;
using PlcKi.Server.Storage;
using Xunit;

/// <summary>Storage limits: pruning by days, MB and counts, the change log's rotation, and what is never removed.</summary>
public class StorageTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "plcki-storage-" + Guid.NewGuid().ToString("N"));
    public StorageTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private static readonly DateTimeOffset Now = new(2026, 11, 9, 12, 0, 0, TimeSpan.FromHours(1));

    private string Put(string rel, string text = "x")
    {
        var p = Path.Combine(_dir, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllText(p, text);
        return p;
    }

    private StorageService Service(RetentionLimits limits, AlarmMemory? mem = null) => new(_dir, () => limits, mem);

    private static WriteLogEntry Entry(int i, DateTimeOffset t) =>
        new(t, "kassem", "192.168.10.10", "plcsim", $"Tag{i}", i.ToString(), "0", i.ToString(), true, "test " + i, true, null);

    // ---------- production log ----------

    [Fact]
    public void Production_log_keeps_N_days_and_never_the_report_settings()
    {
        var day = DateOnly.FromDateTime(Now.LocalDateTime.Date);
        foreach (var ago in new[] { 0, 5, 9, 10, 11, 40 })
            Put($"reports/helium/{day.AddDays(-ago):yyyy-MM-dd}.jsonl", "{}");
        Put($"reports/old-model/{day.AddDays(-200):yyyy-MM-dd}.jsonl", "{}");
        var settings = Put("reports/helium/settings.json", "{\"goodCountTag\":\"x\"}");
        var odd = Put("reports/helium/notes-2020.jsonl");     // not a day file: left alone

        var removed = Service(new RetentionLimits { ProductionDays = 10 }).Prune(Now);

        var left = ProductionLog.DayFiles(Path.Combine(_dir, "reports", "helium")).Select(d => d.Day).ToList();
        Assert.Equal(new[] { day.AddDays(-10), day.AddDays(-9), day.AddDays(-5), day }, left);
        Assert.Empty(ProductionLog.DayFiles(Path.Combine(_dir, "reports", "old-model")));
        Assert.True(File.Exists(settings));
        Assert.True(File.Exists(odd));
        var r = Assert.Single(removed, x => x.Kind == "production");
        Assert.Equal((3, 6L), (r.Count, r.Bytes));     // three files of "{}"
    }

    // ---------- change log ----------

    [Fact]
    public void Change_log_rotates_by_size_and_the_reader_still_sees_every_entry_newest_first()
    {
        WriteEndpoints.ChangeLog(_dir).RotateBytes = 600;
        for (int i = 0; i < 30; i++) WriteEndpoints.Append(_dir, Entry(i, DateTimeOffset.Now.AddSeconds(i)));

        var log = WriteEndpoints.ChangeLog(_dir);
        Assert.True(log.Older().Count >= 3, "the live file was put aside several times");
        Assert.True(File.Exists(Path.Combine(_dir, "value-changes.jsonl")), "new lines still go to value-changes.jsonl");

        // GET /api/admin/write/log reads through ReadLog: across every file, newest first
        var read = WriteEndpoints.ReadLog(_dir, 500);
        Assert.Equal(Enumerable.Range(0, 30).Reverse().Select(i => $"Tag{i}"), read.Select(e => e.Symbol));
        Assert.Equal(new[] { "Tag29", "Tag28", "Tag27" }, WriteEndpoints.ReadLog(_dir, 3).Select(e => e.Symbol));
    }

    [Fact]
    public void Change_log_starts_a_new_file_each_month()
    {
        var live = Put("value-changes.jsonl", System.Text.Json.JsonSerializer.Serialize(Entry(1, Now.AddMonths(-1)),
            new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web)) + Environment.NewLine);
        var lastMonth = DateTime.Now.AddMonths(-1);
        File.SetLastWriteTime(live, lastMonth);

        WriteEndpoints.Append(_dir, Entry(2, DateTimeOffset.Now));

        var older = Assert.Single(WriteEndpoints.ChangeLog(_dir).Older());
        Assert.StartsWith($"value-changes-{lastMonth:yyyy-MM}", Path.GetFileName(older.Path));
        Assert.Equal(new[] { "Tag2", "Tag1" }, WriteEndpoints.ReadLog(_dir, 10).Select(e => e.Symbol));
    }

    [Fact]
    public void Change_log_prunes_older_files_by_days_never_the_live_one()
    {
        var old = Put("value-changes-2024-01-31-235959.jsonl");
        var recent = Put($"value-changes-{Now.LocalDateTime.AddDays(-20):yyyy-MM-dd}-120000.jsonl");
        var live = Put("value-changes.jsonl");
        File.SetLastWriteTime(live, Now.LocalDateTime);

        var r = Service(new RetentionLimits { ChangeLogDays = 30, ChangeLogMb = 50 }).Prune(Now);

        Assert.False(File.Exists(old));
        Assert.True(File.Exists(recent));
        Assert.True(File.Exists(live));
        Assert.Equal(1, Assert.Single(r, x => x.Kind == "changes").Count);
    }

    [Fact]
    public void Change_log_prunes_the_oldest_files_while_over_the_MB_limit()
    {
        var big = new string('x', 400 * 1024);
        var files = Enumerable.Range(1, 5).Select(i => Put($"value-changes-2026-11-0{i}-120000.jsonl", big)).ToList();
        var live = Put("value-changes.jsonl", "{}");
        File.SetLastWriteTime(live, Now.LocalDateTime);

        Service(new RetentionLimits { ChangeLogDays = 3650, ChangeLogMb = 1 }).Prune(Now);

        // 1 MB holds two 400 KB files and the live one: the three oldest go
        Assert.Equal(new[] { false, false, false, true, true }, files.Select(File.Exists));
        Assert.True(File.Exists(live));
    }

    // ---------- recordings ----------

    [Fact]
    public void Recordings_keep_the_newest_N_per_machine()
    {
        var a = Enumerable.Range(1, 5).Select(i => Put($"captures/helium/20261101-12000{i}-000-A0{i}.json", "{}")).ToList();
        var b = Enumerable.Range(1, 3).Select(i => Put($"captures/other/20261001-12000{i}-000-B0{i}.json", "{}")).ToList();

        var r = Service(new RetentionLimits { Recordings = 2 }).Prune(Now);

        Assert.Equal(new[] { false, false, false, true, true }, a.Select(File.Exists));
        Assert.Equal(new[] { false, true, true }, b.Select(File.Exists));
        Assert.Equal(4, Assert.Single(r, x => x.Kind == "recordings").Count);
    }

    // ---------- alarm memory ----------

    private static AlarmMemory MemoryWith(int occurrences, int noted)
    {
        var mem = new AlarmMemory(null);
        var engine = new StatusEngine(TestData.Model);
        MachineStatus At(string snap, DateTimeOffset t) =>
            engine.Evaluate(TestData.Snapshot(snap, t), new PlcLinkInfo("connected", "192.168.10.10", null, t, TestData.Model.Tags.Count, 0));
        var t0 = DateTimeOffset.Now.AddHours(-10);
        mem.Observe(At("step30-agitator-blocked", t0));
        for (int i = 0; i < occurrences; i++)
        {
            mem.Observe(At("step10-xv101-not-open", t0.AddMinutes(i * 2 + 1)));
            mem.Observe(At("step30-agitator-blocked", t0.AddMinutes(i * 2 + 2)));
        }
        var all = mem.Occurrences(t0, DateTimeOffset.Now);
        Assert.Equal(occurrences, all.Count);
        foreach (var o in all.TakeLast(noted)) mem.SetNote(o.Id, "air hose loose", "fixed the hose", null, "kassem");   // the oldest ones
        return mem;
    }

    [Fact]
    public void Alarm_memory_limit_removes_the_oldest_without_a_note_and_never_a_note()
    {
        var mem = MemoryWith(occurrences: 10, noted: 3);

        Assert.Equal(5, mem.Prune(5, dryRun: true));
        Assert.Equal(10, mem.Count);                         // a dry run changes nothing
        Assert.Equal(5, mem.Prune(5));
        Assert.Equal(5, mem.Count);
        Assert.Equal(3, mem.Stats().Noted);

        // A limit below the number of notes: every unnoted one goes, the notes stay
        mem.Prune(1);
        Assert.Equal(3, mem.Count);
        Assert.Equal(3, mem.Stats().Noted);
    }

    [Fact]
    public void Clearing_alarm_memory_keeps_the_notes()
    {
        var mem = MemoryWith(occurrences: 6, noted: 2);
        var s = Service(RetentionLimits.Default, mem);

        var r = s.Clear("memory", "kassem", Now);

        Assert.Equal(4, r.Count);
        Assert.Equal(2, mem.Count);
        Assert.Equal(2, mem.Stats().Noted);
    }

    // ---------- never removed ----------

    [Fact]
    public void Pruning_and_clearing_never_touch_notes_documents_logins_settings_or_keys()
    {
        var keep = new[]
        {
            Put("users.json"), Put("settings.json"), Put("keys/key-1.xml"), Put("model-exports.json"), Put("held-inputs.json"),
            Put("documents/helium/manual.pdf"), Put("documents/shared/drawing.pdf"), Put("models/helium.json"), Put("work/export.zip"),
            Put("reports/helium/settings.json"), Put("memory/helium.json.bad"),
        };
        // Old enough to go under any limit
        Put("reports/helium/2020-01-01.jsonl");
        Put("captures/helium/20200101-000000-000-A01.json");
        Put("value-changes-2020-01-31-235959.jsonl");
        var tightest = new RetentionLimits { ProductionDays = 7, ChangeLogDays = 30, ChangeLogMb = 1, Recordings = 1, AlarmOccurrences = 100 };
        var mem = MemoryWith(occurrences: 4, noted: 2);
        var s = Service(tightest, mem);

        s.Prune(Now);
        foreach (var kind in StorageService.Clearable) s.Clear(kind, "kassem", Now);

        foreach (var f in keep) Assert.True(File.Exists(f), $"{Path.GetRelativePath(_dir, f)} must never be removed");
        Assert.Equal(2, mem.Stats().Noted);
        Assert.Empty(ProductionLog.DayFiles(Path.Combine(_dir, "reports", "helium")));
        Assert.Empty(Directory.GetFiles(Path.Combine(_dir, "captures", "helium")));
    }

    [Fact]
    public void A_dry_run_removes_nothing()
    {
        var day = Put("reports/helium/2020-01-01.jsonl");
        var rec = Enumerable.Range(1, 3).Select(i => Put($"captures/helium/2026110{i}-120000-000-A01.json")).ToList();
        var old = Put("value-changes-2020-01-31-235959.jsonl");

        var rotate = WriteEndpoints.ChangeLog(_dir).RotateBytes;

        var would = Service(new RetentionLimits { ProductionDays = 7, Recordings = 1, ChangeLogMb = 1 }).Prune(Now, dryRun: true);

        Assert.Equal(new[] { "production", "changes", "recordings" }, would.Select(r => r.Kind));
        Assert.True(File.Exists(day) && File.Exists(old) && rec.All(File.Exists));
        Assert.Equal(rotate, WriteEndpoints.ChangeLog(_dir).RotateBytes);     // a preview does not change how the log rotates
    }

    [Fact]
    public void Every_clear_goes_into_the_change_log_with_the_admins_name()
    {
        Put("reports/helium/2026-11-01.jsonl");
        WriteEndpoints.Append(_dir, Entry(1, Now));
        var s = Service(RetentionLimits.Default);

        s.Clear("production", "anna", Now);
        s.Clear("changes", "kassem", Now.AddMinutes(1));

        // Clearing the change log removed the old entries and then recorded itself
        var log = WriteEndpoints.ReadLog(_dir, 10);
        var e = Assert.Single(log);
        Assert.Equal(("clear", "kassem", "Change log"), (e.Action, e.User, e.Symbol));
        Assert.Throws<ArgumentException>(() => s.Clear("documents", "kassem", Now));
    }

    [Fact]
    public void Disk_is_low_under_2_GB_or_under_10_percent_of_a_small_disk()
    {
        const long GB = 1L << 30;
        Assert.True(StorageService.IsLow(1 * GB, 1000 * GB));      // under 2 GB, whatever the disk
        Assert.False(StorageService.IsLow(75 * GB, 1000 * GB));    // 7 % of a big disk is still plenty
        Assert.True(StorageService.IsLow(5 * GB, 64 * GB));        // 8 % of a small plant PC disk
        Assert.False(StorageService.IsLow(20 * GB, 64 * GB));
    }

    [Fact]
    public void Limits_out_of_range_are_refused()
    {
        Assert.Null(RetentionLimits.Default.Problem());
        Assert.NotNull((RetentionLimits.Default with { ProductionDays = 0 }).Problem());
        Assert.NotNull((RetentionLimits.Default with { ChangeLogMb = 0 }).Problem());
        Assert.NotNull((RetentionLimits.Default with { AlarmOccurrences = 5 }).Problem());
    }
}
