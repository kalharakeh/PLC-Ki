using PlcKi.Core.Generate;
using PlcKi.Server.Generate;
using Xunit;

/// <summary>
/// Block generator: answer parsing, offline checks (the demo blocks that compile in TIA V20 must pass, typical mistakes
/// must not), versions, and the generate command against a fake Claude API, including a fix round from a TIA report.
/// </summary>
public class GenerateTests
{
    private static readonly string ExportFile = Path.Combine(AppContext.BaseDirectory, "data", "demo-export-scl.zip");

    private static string RepoRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null && !File.Exists(Path.Combine(d.FullName, "PlcKi.sln"))) d = d.Parent;
        return d?.FullName ?? throw new InvalidOperationException("repo root not found");
    }

    private const string Valve = """
FUNCTION_BLOCK "FB_GenValve"
TITLE = 'Valve'
{ S7_Optimized_Access := 'TRUE' }
AUTHOR : PLC_Ki
VERSION : 0.1
   VAR_INPUT
      Open : Bool;   // open request
   END_VAR
   VAR_OUTPUT
      Out : Bool;   // solenoid
   END_VAR
BEGIN
	#Out := #Open;
END_FUNCTION_BLOCK
""";

    private const string Tags = """
{"tables":[{"name":"Valves","tags":[
  {"name":"XV201_Open","dataType":"Bool","address":"%Q4.0","comment":"Solenoid XV201"},
  {"name":"PT201_Raw","dataType":"Int","address":"%IW80","comment":"Pressure raw"}]}]}
""";

    private static string Answer(string valve = Valve, string tags = Tags, string review = "Call FB_GenValve from the unit FB.") =>
        $"<file name=\"10_FB_GenValve.scl\">\n```scl\n{valve}\n```\n</file>\n<file name=\"90_Tags.json\">\n{tags}\n</file>\n<review>\n{review}\n</review>";

    [Fact]
    public void Parses_files_and_review_and_strips_code_fences()
    {
        var p = BlockPackage.Parse("Here you go.\n" + Answer());
        Assert.Equal(new[] { "10_FB_GenValve.scl", "90_Tags.json" }, p.Files.Select(f => f.Name));
        Assert.StartsWith("FUNCTION_BLOCK \"FB_GenValve\"", p.Files[0].Text);
        Assert.DoesNotContain("```", p.Files[0].Text);
        Assert.Equal("Call FB_GenValve from the unit FB.", p.Review);
        Assert.Empty(PackageCheck.Check(p));
        var blocks = PackageCheck.Blocks(p);
        Assert.Equal("FB", Assert.Single(blocks).Kind);
        Assert.Contains("XV201_Open", PackageCheck.Names(p));
    }

    [Fact]
    public void The_demo_blocks_that_compile_in_TIA_pass_the_offline_check()
    {
        var demo = Path.Combine(RepoRoot(), "plc-demo");
        var p = new BlockPackage { Review = "demo" };
        foreach (var f in Directory.GetFiles(Path.Combine(demo, "scl")).Concat(Directory.GetFiles(Path.Combine(demo, "lad")))
                     .Concat(Directory.GetFiles(Path.Combine(demo, "graph"))))
        {
            var name = Path.GetFileName(f);
            if (name.StartsWith("1") && name.EndsWith(".scl") && (name.Contains("Valve") || name.Contains("Motor") || name.Contains("VFD") || name.Contains("BatchSequence")))
                continue;   // replaced by the LAD/GRAPH versions, as setup-demo does
            p.Files.Add(new PackageFile(name, File.ReadAllText(f)));
        }
        p.Files.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
        var problems = PackageCheck.Check(p);
        Assert.True(problems.Count == 0, string.Join("\n", problems));
        var blocks = PackageCheck.Blocks(p);
        Assert.Contains(blocks, b => b.Name == "FB_BatchSequence" && b.Language == "GRAPH");
        Assert.Contains(blocks, b => b.Name == "DB_BatchSequence" && b.InstanceOf == "FB_BatchSequence");
        Assert.Contains(blocks, b => b.Name == "FB_Valve" && b.Language == "LAD");
    }

    [Fact]
    public void Finds_typical_mistakes()
    {
        var graph = File.ReadAllText(Path.Combine(RepoRoot(), "plc-demo", "graph", "16_FB_BatchSequence.xml"));
        var lad = File.ReadAllText(Path.Combine(RepoRoot(), "plc-demo", "lad", "13_FB_Valve.xml"));
        var p = new BlockPackage { Review = "x" };
        p.Files.Add(new PackageFile("16_FB_BatchSequence.xml", graph));
        // A GRAPH FB as multi-instance, "dt" as a name and a self-resetting timer
        p.Files.Add(new PackageFile("20_FB_Line.scl", """
FUNCTION_BLOCK "FB_Line"
   VAR
      Seq : "FB_BatchSequence";
      dt : Time;
      tPulse {InstructionName := 'TON_TIME'; LibVersion := '1.0'} : TON_TIME;
   END_VAR
BEGIN
	#tPulse(IN := NOT #tPulse.Q, PT := T#1S);
END_FUNCTION_BLOCK
"""));
        // An instance DB before its FB
        p.Files.Add(new PackageFile("05_DB_Line.db", "DATA_BLOCK \"DB_Line\"\n{ S7_Optimized_Access := 'TRUE' }\nVERSION : 0.1\nNON_RETAIN\n\"FB_Line\"\n\nBEGIN\n\nEND_DATA_BLOCK\n"));
        // A LAD wire to a part that does not exist
        p.Files.Add(new PackageFile("13_FB_Valve.xml", lad.Replace("<NameCon UId=\"25\" Name=\"in\"/>", "<NameCon UId=\"99\" Name=\"in\"/>")));
        // Bad tag addresses
        p.Files.Add(new PackageFile("90_Tags.json", """{"tables":[{"name":"T","tags":[{"name":"A","dataType":"Int","address":"%I0.0"},{"name":"B","dataType":"Bool","address":"I0.1"}]}]}"""));
        p.Files.Add(new PackageFile("bad name.scl", Valve));

        var problems = PackageCheck.Check(p, existing: new HashSet<string> { "FB_GenValve" });
        string Find(string s) => Assert.Single(problems, x => x.Contains(s));
        Find("multi-instance of the GRAPH block \"FB_BatchSequence\"");
        Find("\"dt\" is a reserved word");
        Find("#tPulse(IN := NOT #tPulse.Q");
        Find("instance DB of \"FB_Line\" must come after 20_FB_Line.scl");
        Assert.Contains(problems, x => x.Contains("points to missing UId 99"));
        Find("a Int needs a word address");
        Find("address \"I0.1\" must look like");
        Find("bad name.scl: file names must look like");
        Find("\"FB_GenValve\" (bad name.scl) already exists in the PLC program");
    }

    [Fact]
    public void An_edited_block_keeps_its_plant_name()
    {
        const string name = "0.2. Inputs/Outputs Images";
        var src = "FUNCTION \"" + name + "\" : Void\n{ S7_Optimized_Access := 'TRUE' }\nVERSION : 0.2\nBEGIN\n\nEND_FUNCTION\n";
        var replaces = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { name };
        var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { name };

        var kept = new BlockPackage { Review = "r" };
        kept.Files.Add(new PackageFile("30_FC_InputsOutputsImages.scl", src));
        Assert.Empty(PackageCheck.Check(kept, existing, replaces, new[] { name }));

        // Renamed: the import would add a copy that nothing calls
        var renamed = new BlockPackage { Review = "r" };
        renamed.Files.Add(new PackageFile("30_FC_MapInputsOutputs.scl", src.Replace(name, "FC_MapInputsOutputs")));
        Assert.Single(PackageCheck.Check(renamed, existing, replaces, new[] { name }), x => x.Contains("do not rename it"));

        // A new block still needs a plain name
        Assert.Single(PackageCheck.Check(kept), x => x.Contains("should only use letters, digits and _"));
    }

    [Fact]
    public void A_tag_asked_to_change_must_come_back()
    {
        var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Sim_Door" };
        var p = new BlockPackage { Review = "r" };
        p.Files.Add(new PackageFile("90_Tags.json", """{"tables":[{"name":"IO","tags":[{"name":"Sim_Door","dataType":"Bool","address":"%M900.1"}]}]}"""));
        Assert.Empty(PackageCheck.Check(p, existing, existing, new[] { "Sim_Door" }));
        Assert.Single(PackageCheck.Check(p, existing, existing, new[] { "Sim_ES1" }), x => x.Contains("\"Sim_ES1\""));
    }

    [Fact]
    public void Tags_of_the_safety_program_are_known()
    {
        var plc = new PlcKi.Core.Import.ExportPlc();
        plc.PlcTags.Add(new("FDI_ES1", "Bool", "%I8.2", null, "Safety"));
        plc.PlcTags.Add(new("DI_Start", "Bool", "%I0.0", null, "IO"));
        var f = new PlcKi.Core.Import.ExportBlock { Name = "Main_Safety", Kind = "FB", Language = "F_LAD" };
        f.GlobalNames.Add("FDI_ES1");
        var std = new PlcKi.Core.Import.ExportBlock { Name = "Main", Kind = "OB", Language = "LAD" };
        std.GlobalNames.Add("DI_Start"); std.GlobalNames.Add("FDI_ES1");
        plc.Blocks.Add(f); plc.Blocks.Add(std);
        Assert.Equal(new[] { "FDI_ES1" }, plc.SafetyTags());
    }

    [Fact]
    public void Versions_record_what_changed()
    {
        var dir = Directory.CreateTempSubdirectory("plcki-pkg-").FullName;
        try
        {
            var h = new PackageHistory();
            var v1 = BlockPackage.Parse(Answer());
            h.Add(dir, v1, new PackageVersion { Request = "valve" }, null);
            var v2 = BlockPackage.Parse(Answer(valve: Valve.Replace("#Out := #Open;", "#Out := #Open AND TRUE;"), review: "changed"));
            v2.Files.RemoveAll(f => f.Name == "90_Tags.json");
            v2.Files.Add(new PackageFile("20_FC_Scale.scl", "FUNCTION \"FC_Scale\" : Void\nBEGIN\nEND_FUNCTION\n"));
            var info = h.Add(dir, v2, new PackageVersion { Kind = "change", Request = "and" }, v1);

            Assert.Equal("v002", info.Folder);
            Assert.Equal(new[] { "20_FC_Scale.scl" }, info.Added);
            Assert.Equal(new[] { "10_FB_GenValve.scl" }, info.Changed);
            Assert.Equal(new[] { "90_Tags.json" }, info.Removed);

            var loaded = PackageHistory.Load(dir);
            Assert.Equal(2, loaded.Versions.Count);
            var back = BlockPackage.Load(Path.Combine(dir, "v001"));
            Assert.Equal(2, back.Files.Count);
            Assert.Equal("Call FB_GenValve from the unit FB.", back.Review.Trim());
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Summary_says_what_a_version_made_and_changed()
    {
        var v1 = BlockPackage.Parse(Answer());
        var first = PackageSummary.Compare(null, v1);
        Assert.Equal("new", Assert.Single(first.Blocks).Change);
        Assert.Contains(first.Text, t => t.StartsWith("Wrote function block FB_GenValve (SCL)"));
        Assert.Contains("2 new tags: XV201_Open (%Q4.0), PT201_Raw (%IW80).", first.Text);

        // v2: one line of the valve changed, one tag moved, one tag gone, one new tag and a new FC
        const string tags2 = """
{"tables":[{"name":"Valves","tags":[
  {"name":"XV201_Open","dataType":"Bool","address":"%Q4.1","comment":"Solenoid XV201"},
  {"name":"XV201_Fault","dataType":"Bool","address":"%I4.2","comment":"Fault"}]}]}
""";
        var v2 = BlockPackage.Parse(Answer(valve: Valve.Replace("#Out := #Open;", "#Out := #Open AND NOT #Fault;\n\t#Busy := #Open;"), tags: tags2));
        v2.Files.Add(new PackageFile("20_FC_Scale.scl", "FUNCTION \"FC_Scale\" : Void\nBEGIN\nEND_FUNCTION\n"));
        var s = PackageSummary.Compare(v1, v2);

        var valve = s.Blocks.Single(b => b.Name == "FB_GenValve");
        Assert.Equal(("changed", 2, 1), (valve.Change, valve.LinesAdded, valve.LinesRemoved));
        Assert.Equal("new", s.Blocks.Single(b => b.Name == "FC_Scale").Change);
        Assert.Contains("Changed function block FB_GenValve (SCL): 2 lines added, 1 removed.", s.Text);
        Assert.Contains("Added function FC_Scale (SCL): 3 lines.", s.Text);
        Assert.Contains("Added 1 tag: XV201_Fault (%I4.2).", s.Text);
        Assert.Contains("Removed 1 tag: PT201_Raw (%IW80).", s.Text);
        Assert.Contains("Changed 1 tag: XV201_Open (address %Q4.0 to %Q4.1).", s.Text);

        // the same package again: nothing changed
        Assert.Equal(new[] { "Unchanged: FB_GenValve, FC_Scale." }, PackageSummary.Compare(v2, v2).Text);
        Assert.Equal((0, 0), PackageSummary.Diff(new() { "a", "b" }, new() { "a", "b" }));
        Assert.Equal((1, 1), PackageSummary.Diff(new() { "a", "b", "c" }, new() { "a", "x", "c" }));
    }

    [Fact]
    public void Reads_existing_blocks_from_an_export_for_editing()
    {
        var got = ExportSources.Read(ExportFile, null, new[] { "FB_Valve", "Station", "Nope" });
        Assert.Equal(2, got.Count);
        Assert.Equal(".scl", got[0].Extension);
        Assert.Contains("FUNCTION_BLOCK \"FB_Valve\"", got[0].Text);
        Assert.Equal(".xml", got[1].Extension);
        Assert.Contains("<Document>", got[1].Text);
    }

    [Fact]
    public async Task Generate_then_fix_from_a_TIA_report()
    {
        var dir = Directory.CreateTempSubdirectory("plcki-gen-").FullName;
        try
        {
            // Round 1: the first answer misses the review, the correction is fine
            var api = new ModelBuilderTests.FakeStreamingApi(Answer(review: "").Replace("<review>\n\n</review>", ""), Answer());
            var code = await GenerateCommand.RunAsync(["--out", dir, "--request", "Make me a valve FB in SCL", "--context", ExportFile, "--scl-only"],
                ModelBuilderTests.Client(api));
            Assert.Equal(0, code);
            Assert.Equal(2, api.Requests.Count);
            var first = api.Requests[0].GetProperty("messages")[0].GetProperty("content");
            var fixedPart = first[0].GetProperty("text").GetString()!;
            Assert.Contains("<standard>", fixedPart);
            Assert.Contains("FUNCTION_BLOCK \"FB_Valve\"", fixedPart);      // SCL reference
            Assert.DoesNotContain("<FlgNet", fixedPart);                     // --scl-only
            Assert.Contains("<program>", fixedPart);                          // --context
            Assert.True(first[0].TryGetProperty("cache_control", out _));
            Assert.Contains("Make me a valve FB in SCL", first[1].GetProperty("text").GetString());
            Assert.Contains("The <review> section is missing", api.Requests[1].GetProperty("messages")[0].GetProperty("content")[2].GetProperty("text").GetString());

            var h = PackageHistory.Load(dir);
            Assert.Equal("v001", h.Latest!.Folder);
            Assert.Equal("PLC_1", h.Plc);
            Assert.True(File.Exists(Path.Combine(dir, "v001", "10_FB_GenValve.scl")));
            Assert.True(File.Exists(Path.Combine(dir, "v001", "REVIEW.md")));

            // TIA reports a compile error; --fix sends it with the package and writes v002
            File.WriteAllText(Path.Combine(dir, "v001", "import-report.json"), """
{"project":"C:\\p\\Demo.ap20","plc":"PLC_1","version":"v001","saved":false,"compileState":"Error","errorCount":1,"warningCount":0,
 "fileErrors":[],"messages":[{"state":"Error","path":"FB_GenValve","description":"Tag \"XV201_Open\" not defined"}]}
""");
            var api2 = new ModelBuilderTests.FakeStreamingApi(Answer(review: "Fixed the tag."));
            code = await GenerateCommand.RunAsync(["--out", dir, "--fix", "--scl-only"], ModelBuilderTests.Client(api2));
            Assert.Equal(0, code);
            var task = api2.Requests[0].GetProperty("messages")[0].GetProperty("content")[1].GetProperty("text").GetString()!;
            Assert.Contains("Compile error in FB_GenValve: Tag \"XV201_Open\" not defined", task);
            Assert.Contains("<package>", task);
            h = PackageHistory.Load(dir);
            Assert.Equal("v002", h.Latest!.Folder);
            Assert.Equal("fix", h.Latest.Kind);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task Edit_sends_the_existing_block_and_allows_replacing_it()
    {
        var dir = Directory.CreateTempSubdirectory("plcki-edit-").FullName;
        try
        {
            var edited = File.ReadAllText(Path.Combine(RepoRoot(), "plc-demo", "scl", "13_FB_Valve.scl")).Replace("VERSION : 0.1", "VERSION : 0.2");
            var api = new ModelBuilderTests.FakeStreamingApi($"<file name=\"10_FB_Valve.scl\">\n{edited}\n</file>\n<review>Raised the version.</review>");
            var code = await GenerateCommand.RunAsync(["--out", dir, "--request", "Add a manual mode", "--context", ExportFile, "--edit", "FB_Valve", "--scl-only"],
                ModelBuilderTests.Client(api));
            Assert.Equal(0, code);   // FB_Valve exists in the program but is being edited, so no conflict
            var task = api.Requests[0].GetProperty("messages")[0].GetProperty("content")[1].GetProperty("text").GetString()!;
            Assert.Contains("<existing name=\"FB_Valve.scl\">", task);
            var v = PackageHistory.Load(dir).Latest!;
            Assert.Equal("edit", v.Kind);
            Assert.Equal(new[] { "FB_Valve" }, v.Replaces);
        }
        finally { Directory.Delete(dir, true); }
    }
}

/// <summary>"develop" against a fake TIA PC: a compile error goes back to Claude, the fixed version compiles and downloads.</summary>
public class DevelopTests
{
    private sealed class FakeTiaPc : HttpMessageHandler
    {
        public readonly List<string> Calls = new();
        public readonly List<string> Uploaded = new();
        public readonly List<string> DownloadBodies = new();
        public int Imports => _imports;
        public int Polls, Drops;
        public bool DropEveryFirstPoll = true;   // as when the download resets the TIA PC's adapter
        private int _imports;
        private readonly Dictionary<string, string> _jobs = new();
        private readonly HashSet<string> _polledOnce = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Assert.Equal("secret-token", request.Headers.GetValues("X-PlcKi-Token").Single());
            var path = request.RequestUri!.AbsolutePath;
            string body = "{\"ok\":true}";
            if (path.StartsWith("/jobs/"))
            {
                Polls++;
                var id = path["/jobs/".Length..];
                if (_polledOnce.Add(id))
                {
                    if (DropEveryFirstPoll) { Drops++; throw new HttpRequestException("The specified network name is no longer available"); }
                    body = "{\"job\":\"" + id + "\",\"done\":false,\"log\":\"Starting TIA Portal...\\n\"}";
                }
                else body = _jobs[id];
                return new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") };
            }
            Calls.Add(request.Method + " " + path);
            if (request.Method == HttpMethod.Put)
            {
                using var zip = new System.IO.Compression.ZipArchive(await request.Content!.ReadAsStreamAsync(ct));
                Uploaded.AddRange(zip.Entries.Select(e => e.FullName.Replace('\\', '/')));
            }
            else if (path == "/import")
            {
                var req = System.Text.Json.JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct)).RootElement;
                Assert.Equal(@"C:\Users\Software\Documents\Automation\Test\Test.ap20", req.GetProperty("project").GetString());
                var version = req.GetProperty("version").GetString();
                body = ++_imports == 1
                    ? "{\"exitCode\":3,\"log\":\"Compile: Error\\n\",\"report\":{\"version\":\"" + version + "\",\"saved\":false,\"compileState\":\"Error\",\"errorCount\":1,\"warningCount\":0,\"fileErrors\":[],\"messages\":[{\"state\":\"Error\",\"path\":\"FB_GenValve\",\"description\":\"Tag XV201_Open not defined\"}]}}"
                    : "{\"exitCode\":0,\"log\":\"Compile: Success\\n\",\"report\":{\"version\":\"" + version + "\",\"saved\":true,\"compileState\":\"Success\",\"errorCount\":0,\"warningCount\":0,\"fileErrors\":[],\"messages\":[]}}";
            }
            else if (path == "/download")
            {
                DownloadBodies.Add(await request.Content!.ReadAsStringAsync(ct));
                body = "{\"exitCode\":0,\"log\":\"Download: Success\\n\"}";
            }
            if (request.Method == HttpMethod.Post)
            {
                // The TIA PC answers with a job; the result comes from polling it
                var id = "j" + (_jobs.Count + 1);
                _jobs[id] = body.Insert(1, "\"job\":\"" + id + "\",\"done\":true,");
                body = "{\"job\":\"" + id + "\"}";
                return new HttpResponseMessage(System.Net.HttpStatusCode.Accepted) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") };
            }
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") };
        }
    }

    [Fact]
    public async Task Fixes_a_compile_error_then_downloads()
    {
        var dir = Path.Combine(Directory.CreateTempSubdirectory("plcki-dev-").FullName, "valve");
        try
        {
            var tia = new FakeTiaPc();
            var vm = new VmClient("http://tia-pc:5090", "secret-token", tia) { PollInterval = TimeSpan.Zero };
            var answer = "<file name=\"10_FB_GenValve.scl\">\nFUNCTION_BLOCK \"FB_GenValve\"\nVAR_INPUT\n Open : Bool;\nEND_VAR\nBEGIN\nEND_FUNCTION_BLOCK\n</file>\n<review>ok</review>";
            var api = new ModelBuilderTests.FakeStreamingApi(answer, answer.Replace("<review>ok", "<review>fixed"));
            var code = await DevelopCommand.RunAsync(["--vm", "http://tia-pc:5090", "--project", @"C:\Users\Software\Documents\Automation\Test\Test.ap20",
                "--out", dir, "--request", "valve", "--download", "--scl-only"], ModelBuilderTests.Client(api), vm);

            Assert.Equal(0, code);
            Assert.Equal(new[] { "GET /status", "PUT /packages/valve", "POST /import", "PUT /packages/valve", "POST /import", "POST /download" }, tia.Calls);
            Assert.Contains("generated.json", tia.Uploaded);
            Assert.Equal(3, tia.Drops);   // every job lost its first poll, and the loop carried on
            Assert.Contains("v002/10_FB_GenValve.scl", tia.Uploaded);
            // The fix round told Claude what TIA said
            var fixTask = api.Requests[1].GetProperty("messages")[0].GetProperty("content")[1].GetProperty("text").GetString()!;
            Assert.Contains("Compile error in FB_GenValve: Tag XV201_Open not defined", fixTask);
            var report = ImportReport.Load(Path.Combine(dir, "v002", "import-report.json"));
            Assert.True(report.Clean);
            Assert.Equal("OK", report.Download);
            Assert.Equal("fix", PackageHistory.Load(dir).Latest!.Kind);
        }
        finally { Directory.Delete(Path.GetDirectoryName(dir)!, true); }
    }

    private static async Task<(int code, FakeTiaPc tia, string dir)> RealRun(string? typed, params string[] extra)
    {
        var dir = Path.Combine(Directory.CreateTempSubdirectory("plcki-dev-").FullName, "valve");
        var tia = new FakeTiaPc();
        var vm = new VmClient("http://tia-pc:5090", "secret-token", tia) { PollInterval = TimeSpan.Zero };
        var answer = "<file name=\"10_FB_GenValve.scl\">\nFUNCTION_BLOCK \"FB_GenValve\"\nVAR_INPUT\n Open : Bool;\nEND_VAR\nBEGIN\nEND_FUNCTION_BLOCK\n</file>\n<review>ok</review>";
        var api = new ModelBuilderTests.FakeStreamingApi(answer, answer.Replace("<review>ok", "<review>fixed"));
        var code = await DevelopCommand.RunAsync(["--vm", "http://tia-pc:5090", "--project", @"C:\Users\Software\Documents\Automation\Test\Test.ap20",
            "--out", dir, "--request", "valve", "--scl-only", .. extra], ModelBuilderTests.Client(api), vm, readLine: () => typed);
        return (code, tia, dir);
    }

    [Fact]
    public async Task Real_download_needs_the_version_typed_and_runs_once()
    {
        var (code, tia, dir) = await RealRun("v002", "--download-real");
        try
        {
            Assert.Equal(0, code);
            // compile error fixed first, then exactly one real download, no PLCSIM download
            Assert.Equal(new[] { "GET /status", "PUT /packages/valve", "POST /import", "PUT /packages/valve", "POST /import", "POST /download" }, tia.Calls);
            Assert.Contains("\"real\":true", Assert.Single(tia.DownloadBodies));
            Assert.Equal("OK (real PLC, left in STOP)", ImportReport.Load(Path.Combine(dir, "v002", "import-report.json")).Download);
        }
        finally { Directory.Delete(Path.GetDirectoryName(dir)!, true); }
    }

    [Fact]
    public async Task Real_download_stops_without_confirmation()
    {
        var (code, tia, dir) = await RealRun("yes", "--download-real");
        try
        {
            Assert.Equal(1, code);
            Assert.DoesNotContain("POST /download", tia.Calls);
            Assert.Equal("not confirmed", ImportReport.Load(Path.Combine(dir, "v002", "import-report.json")).Download);
        }
        finally { Directory.Delete(Path.GetDirectoryName(dir)!, true); }
    }

    [Fact]
    public async Task Plcsim_and_real_download_together_are_refused()
    {
        var (code, tia, dir) = await RealRun("v001", "--download", "--download-real");
        try
        {
            Assert.Equal(2, code);
            Assert.Empty(tia.Calls);
        }
        finally { if (Directory.Exists(Path.GetDirectoryName(dir)!)) Directory.Delete(Path.GetDirectoryName(dir)!, true); }
    }

    [Fact]
    public void Runtime_problems_go_back_as_problems()
    {
        var r = new ImportReport { CompileState = "Success", Download = "OK", Runtime = { "The CPU is in Stop instead of RUN." } };
        Assert.True(r.Compiled);
        Assert.False(r.Clean);
        Assert.Contains("While running on PLCSIM: The CPU is in Stop instead of RUN.", r.AsProblems());
    }
}
