using PlcKi.Core.Model;
using PlcKi.Server.Live;
using Xunit;

/// <summary>Test mode: the Tags page asks for full symbols, test files store values under tag ids.</summary>
public class SnapshotSourceTests
{
    private static string File_(string name) => Path.Combine(AppContext.BaseDirectory, "models", "snapshots", name + ".json");

    private static TagDef Asked(string symbol) => new() { Id = "x", Symbol = symbol, Type = "Bool" };

    [Fact]
    public void Symbols_are_found_through_the_model_tag_id()
    {
        using var src = new SnapshotFileSource(File_("step10-xv101-not-open"));
        src.Connect(TestData.Model.Tags);
        var r = src.ReadSymbols(new[] { Asked("\"DB_IO\".EStop_OK"), Asked("\"db_io\".estop_ok"), Asked("\"DB_IO\".Not_There") });
        Assert.Equal((true, null), (r["\"DB_IO\".EStop_OK"].Value, r["\"DB_IO\".EStop_OK"].Error));
        Assert.Equal(true, r["\"db_io\".estop_ok"].Value);
        Assert.Null(r["\"DB_IO\".Not_There"].Value);
        Assert.Equal("Not in the test file.", r["\"DB_IO\".Not_There"].Error);
    }

    [Fact]
    public void Full_symbol_key_in_the_file_still_works_and_wins()
    {
        var path = Path.Combine(Path.GetTempPath(), $"snap-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, "{ \"\\\"DB_IO\\\".EStop_OK\": false, \"EStop_OK\": true }");
        try
        {
            using var src = new SnapshotFileSource(path);
            src.Connect(TestData.Model.Tags);
            var r = src.ReadSymbols(new[] { Asked("\"DB_IO\".EStop_OK") });
            Assert.Equal(false, r["\"DB_IO\".EStop_OK"].Value);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Block_members_are_read_by_their_full_name()
    {
        // The Program page asks for the open block's members ("Station".XV101.Out); FB_Valve network 4 needs these
        using var src = new SnapshotFileSource(File_("step10-xv101-not-open"));
        src.Connect(TestData.Model.Tags);
        var asked = new[] { "\"Station\".XV101.Out", "\"Station\".XV101.IsOpen", "\"Station\".XV101.tOpen.Q", "\"Station\".XV101.MonitorTime" };
        var r = src.ReadSymbols(asked.Select(Asked).ToList());
        Assert.All(asked, s => Assert.Null(r[s].Error));
        Assert.Equal(false, r["\"Station\".XV101.Out"].Value);
        Assert.Equal(false, r["\"Station\".XV101.tOpen.Q"].Value);
        Assert.Equal(10000.0, r["\"Station\".XV101.MonitorTime"].Value);
    }

    [Fact]
    public void Block_members_in_the_file_are_not_model_values()
    {
        // The polling loop's read keeps to the model's tags: the extra keys only answer the Program page
        using var src = new SnapshotFileSource(File_("step10-xv101-not-open"));
        src.Connect(TestData.Model.Tags);
        var snap = src.Read();
        var ids = TestData.Model.Tags.Select(t => t.Id).ToHashSet();
        Assert.All(snap.Values.Keys, k => Assert.Contains(k, ids));
        Assert.Equal(true, snap.Values["A02"]);
    }

    [Fact]
    public void Estop_and_drive_file_shows_two_alarms()
    {
        var s = TestData.Status("step30-estop-and-drive");
        Assert.Equal(new[] { "A01", "A08" }, s.ActiveAlarms.Select(a => a.Id).OrderBy(x => x).ToArray());
    }
}
