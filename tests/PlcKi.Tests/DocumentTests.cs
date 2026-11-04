using System.Text;
using System.Text.Json;
using PlcKi.Core.Ask;
using PlcKi.Core.Knowledge;
using PlcKi.Core.Status;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;
using Xunit;

/// <summary>Documents page: manuals (PDF), fault lists (CSV) and drawings are read, searched and used by Ask.</summary>
public class DocumentTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "plcki-docs-" + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose() { try { Directory.Delete(_root, true); } catch (IOException) { } }

    /// <summary>A small PDF with one text line per entry, pages split by null.</summary>
    private static byte[] Pdf(params string?[] lines)
    {
        var b = new PdfDocumentBuilder();
        var font = b.AddStandard14Font(Standard14Font.Helvetica);
        PdfPageBuilder? page = null;
        double y = 0;
        foreach (var l in lines.Prepend(null))
        {
            if (l == null) { page = b.AddPage(PageSize.A4); y = 800; continue; }
            page!.AddText(l, 10, new PdfPoint(40, y), font);
            y -= 14;
        }
        return b.Build();
    }

    private static string[] ListManual()
    {
        var lines = new List<string?> { "SINAMICS G120 List Manual", "Contents", "F07801 Motor overcurrent ........ 812", null };
        foreach (var (code, title) in new[] { ("F07800", "Drive: No power unit present"), ("F07801", "Motor overcurrent"), ("F07802", "Infeed or power unit not ready"),
                     ("F07806", "Regenerative power limit exceeded"), ("F07807", "Short circuit detected") })
        {
            lines.Add($"{code} {title}");
            lines.Add("Reaction: OFF2");
            lines.Add("Cause: The permissible motor limit current was exceeded. Ramp-up time too short.");
            lines.Add("Remedy: Check the motor data and increase the ramp-up time.");
            lines.Add(null);
        }
        lines.RemoveAt(lines.Count - 1);
        return lines.ToArray()!;
    }

    private async Task<DocumentLibrary> Library(params (string Scope, string Name, byte[] Data, string? Kind, string? Device)[] files)
    {
        var lib = new DocumentLibrary(_root, "Helium leak test");
        foreach (var f in files) await lib.AddAsync(new MemoryStream(f.Data), f.Name, f.Scope, f.Kind, f.Device);
        await Task.Delay(50);
        await lib.ReadPendingAsync();
        return lib;
    }

    [Fact]
    public async Task A_list_manual_PDF_is_read_and_its_fault_codes_found()
    {
        var lib = await Library(("shared", "G120 list manual.pdf", Pdf(ListManual()), null, "SINAMICS G120"));
        var d = Assert.Single(lib.List());
        Assert.Equal("ready", d.State);
        Assert.Equal("shared", d.Scope);
        Assert.Equal("SINAMICS G120", d.Device);
        Assert.Equal(6, d.Pages);
        Assert.Equal(5, d.FaultCodes);

        // F7801 = F07801, and the page with the text wins over the contents line
        var hit = Assert.Single(lib.FindFaults("drive shows F7801"));
        Assert.Equal("F07801", hit.Fault.Code);
        Assert.Equal("Motor overcurrent", hit.Fault.Title);
        Assert.Equal(3, hit.Fault.Page);
        Assert.Contains("ramp-up time", hit.Fault.Fix);
        Assert.Contains("limit current", hit.Fault.Cause);

        var pages = lib.Search("F07801 overcurrent");
        Assert.Equal(3, pages[0].Page);
        Assert.Contains("F07801", pages[0].Snippet);
    }

    [Fact]
    public async Task A_CSV_fault_list_is_read_with_its_columns()
    {
        var csv = "Code;Message;Cause;Remedy\n41;Helium background too high;Leak in test chamber;Vent and pump down again\n" +
                  "E12;\"Filament defective; switched to 2\";Filament burnt;Replace the ion source\n";
        var lib = await Library(("machine", "LDS3000 errors.csv", Encoding.UTF8.GetBytes(csv), null, "Inficon LDS3000"));
        var d = Assert.Single(lib.List());
        Assert.Equal("faults", d.Kind);
        Assert.Equal(2, d.FaultCodes);
        var hit = Assert.Single(lib.FindFaults("E12"));
        Assert.Equal("Filament defective; switched to 2", hit.Fault.Title);
        Assert.Equal("Replace the ion source", hit.Fault.Fix);
        // Words find it too, when no code is given
        Assert.Equal("41", lib.FindFaults("helium background high").First().Fault.Code);
    }

    [Fact]
    public async Task Machine_documents_are_only_for_their_machine_and_files_copied_in_are_found()
    {
        var lib = await Library(("machine", "EPLAN Helium.pdf", Pdf("Sheet 12", "-B23 door switch station 2", "%I12.3 terminal X2:14"), null, null));
        Assert.Equal("drawing", lib.List()[0].Kind);
        Assert.Contains(Path.Combine("machines", "Helium-leak-test"), lib.MachineDir);

        // Another machine does not see it; a file copied into the shared folder by hand is picked up by a scan
        var other = new DocumentLibrary(_root, "Mixing station");
        File.WriteAllText(Path.Combine(other.SharedDir, "notes.txt"), "Grease the agitator bearing every 500 hours.");
        await other.Scan();
        await other.ReadPendingAsync();
        var d = Assert.Single(other.List());
        Assert.Equal("notes.txt", d.File);

        // Terminal and address search
        var hit = lib.Search("X2:14").First();
        Assert.Contains("%I12.3", hit.Snippet);
        Assert.Equal(1, hit.Page);
        Assert.NotEmpty(lib.Search("I12.3"));

        // Change and delete
        lib.Update(lib.List()[0].Id, "manual", "Cabinet");
        Assert.Equal("Cabinet", lib.List()[0].Device);
        lib.Delete(lib.List()[0].Id);
        Assert.Empty(lib.List());
        Assert.Empty(Directory.GetFiles(lib.MachineDir));
    }

    [Fact]
    public async Task A_stored_index_is_used_again_after_a_restart()
    {
        var lib = await Library(("shared", "G120.pdf", Pdf(ListManual()), "manual", "G120"));
        var again = new DocumentLibrary(_root, "Helium leak test");
        await again.Scan();
        var d = Assert.Single(again.List());
        Assert.Equal("ready", d.State);     // no second read
        Assert.Equal("G120", d.Device);
        Assert.Single(again.FindFaults("F07801"));
    }

    [Fact]
    public async Task Scanned_PDF_and_wrong_files_are_explained()
    {
        var lib = await Library(("machine", "scan.pdf", Pdf(" "), null, null));
        Assert.Equal("notext", lib.List()[0].State);
        Assert.Contains("no text", lib.List()[0].Note);
        await Assert.ThrowsAsync<ArgumentException>(() => lib.AddAsync(new MemoryStream(new byte[3]), "photo.jpg", "machine", null, null));
        Assert.Equal("x_.pdf", DocumentLibrary.SafeFileName("../x?.pdf"));
    }

    [Theory]
    [InlineData("SRVO-062", "fanuc-srvo-062-065-battery")]
    [InlineData("robot shows SRVO-050 collision", "fanuc-srvo-050-collision")]
    [InlineData("IO-Link event 0x8C10", "iolink-process")]
    [InlineData("F07900", "drive-f07900-motor-blocked")]
    public void Built_in_fault_codes_are_found_without_documents(string query, string id)
    {
        var hit = FaultLookup.Find(null, query).First();
        Assert.Equal(id, hit.BuiltIn!.Id);
    }

    [Fact]
    public async Task Ask_tools_use_the_manuals_and_name_the_page()
    {
        var lib = await Library(("shared", "G120 list manual.pdf", Pdf(ListManual()), null, "SINAMICS G120"));
        var model = TestData.Model;
        var tools = new MachineTools(model, () => new LiveView(null, null, new MachineHistory(TimeSpan.FromMinutes(5)), DateTimeOffset.Now), lib);

        var fault = JsonDocument.Parse(tools.Run("lookup_device_fault", JsonDocument.Parse("""{"code":"F07801","device":"G120"}""").RootElement)).RootElement;
        var first = fault.GetProperty("matches")[0];
        Assert.Equal("F07801", first.GetProperty("code").GetString());
        Assert.Equal(3, first.GetProperty("page").GetInt32());
        Assert.False(first.GetProperty("generalKnowledge").GetBoolean());
        // The built-in Siemens entry for the same code comes after the manual
        Assert.True(fault.GetProperty("matches").GetArrayLength() >= 2);

        var pages = JsonDocument.Parse(tools.Run("search_documents", JsonDocument.Parse("""{"query":"ramp-up time overcurrent"}""").RootElement)).RootElement;
        Assert.Equal("G120 list manual.pdf", pages.GetProperty("matches")[0].GetProperty("document").GetString());

        // Without documents the tool says what to do
        var none = new MachineTools(model, () => new LiveView(null, null, new MachineHistory(TimeSpan.FromMinutes(5)), DateTimeOffset.Now));
        var r = none.Run("lookup_device_fault", JsonDocument.Parse("""{"code":"Error 41"}""").RootElement);
        Assert.Contains("Documents page", r);
        Assert.Contains(MachineTools.Specs, s => s.Name == "search_documents");
    }
}
