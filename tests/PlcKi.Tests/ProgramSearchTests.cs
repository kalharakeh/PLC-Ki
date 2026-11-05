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

    private static readonly string[] Scl =
    {
        "#Run := #Start AND NOT #Stop;",                      // 0: every time the block runs
        "IF #Reset THEN #Fault := FALSE; END_IF;",            // 1: same line THEN
        "IF #a > 5 THEN",                                     // 2
        "    #Speed := 100;",                                 // 3: inside IF
        "ELSIF #b THEN",                                      // 4
        "    #Speed := 50;",                                  // 5: still inside
        "END_IF;",                                            // 6
        "(* IF in a comment does not open anything *)",      // 7
        "// IF here neither",                                 // 8
        "#Lamp := 'IF' = #Text;",                             // 9: a text 'IF' does not count either
        "CASE #Step OF",                                      // 10
        "  1: #Valve := TRUE;",                               // 11: inside CASE
        "END_CASE;",                                          // 12
        "#Done := #Valve;",                                   // 13: after everything closed
    };

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    [InlineData(3, true)]
    [InlineData(5, true)]
    [InlineData(9, false)]
    [InlineData(11, true)]
    [InlineData(13, false)]
    public void A_write_is_conditional_inside_if_case_or_after_then(int line, bool conditional)
        => Assert.Equal(conditional, ProgramService.Conditional(Scl, line));
}
