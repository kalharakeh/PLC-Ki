using System.Text.Json;
using PlcKi.Server.Jobs;
using Xunit;

namespace PlcKi.Tests;

/// <summary>"Show where it is set" finds the block that writes an alarm bit from the program search.</summary>
public class ProgramSearchTests
{
    [Theory]
    [InlineData("\"DB_Alarms\".A02 := #In OR (\"DB_Alarms\".A02 AND NOT #clr);", "\"DB_Alarms\".A02", true)]
    [InlineData("\"DB_Alarms\".A02 := #In OR (\"DB_Alarms\".A02 AND NOT #clr);", "A02", true)]
    [InlineData("IF #a THEN #FaultNotOpen := TRUE; END_IF;   // set", "#FaultNotOpen", true)]
    [InlineData("#FaultNotOpen := #tOpen.Q;", "FaultNotOpen", true)]
    [InlineData("\"DB_Alarms\".AnyFault := \"DB_Alarms\".A01 OR \"DB_Alarms\".A02", "\"DB_Alarms\".A02", false)]
    [InlineData("\"DB_Alarms\".A02_Old := TRUE;", "\"DB_Alarms\".A02", false)]
    [InlineData("XV101_NotOpen := #XV101.FaultNotOpen,", "#XV101.FaultNotOpen", false)]
    [InlineData("XV101_NotOpen := #XV101.FaultNotOpen,", "XV101_NotOpen", false)]
    public void A_line_writes_a_signal_only_when_it_is_assigned(string line, string symbol, bool writes)
        => Assert.Equal(writes, ProgramService.Writes(line, symbol));

    [Fact]
    public void Search_marks_the_line_that_latches_an_alarm()
    {
        var data = Path.Combine(AppContext.BaseDirectory, "data");
        var program = new ProgramService(new PlcKi.Server.Live.PlcKiOptions { Export = Path.Combine(data, "demo-export.zip") }, TestData.Model, p => p);
        var json = JsonSerializer.SerializeToElement(program.Search("\"DB_Alarms\".A02_XV101_NotOpen"));
        var writers = json.GetProperty("results").EnumerateArray()
            .Where(r => r.GetProperty("lines").EnumerateArray().Any(l => l.GetProperty("writes").GetBoolean()))
            .Select(r => r.GetProperty("block").GetString()).ToList();
        Assert.Equal(new[] { "FB_AlarmHandler" }, writers);
    }
}
