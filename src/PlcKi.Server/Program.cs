using PlcKi.Core.Ask;
using PlcKi.Core.Memory;
using PlcKi.Core.Model;
using PlcKi.Core.Status;
using PlcKi.Plant;
using PlcKi.Server;
using PlcKi.Server.Ask;
using PlcKi.Server.Generate;
using PlcKi.Server.Import;
using PlcKi.Server.Jobs;
using PlcKi.Server.Link;
using PlcKi.Server.Live;
using PlcKi.Server.Memory;

// PLC-Ki server.
//   PlcKi.Server                 start the web app (default http://localhost:5080)
//   PlcKi.Server probe           connect once, read every tag, print the values and the status, exit
//   PlcKi.Server import ...      write a machine model from a TIA program export with Claude (see Import/ImportCommand.cs)
//   PlcKi.Server generate ...    write PLC blocks from a plain request with Claude (see Generate/GenerateCommand.cs)
//   PlcKi.Server develop ...     generate, import, compile, fix, PLCSIM download and watch, with TiaImporter serve
//   PlcKi.Server export ...      send a .zap20 to TiaImporter serve and fetch the program export
//   PlcKi.Server link ...        PLC-Ki Link: on a laptop, carry PLC values and TIA work to a PLC-Ki server (see Link/)
// The pages under Setup run the same commands as jobs (see Jobs/), so everything here also works with buttons.
// Settings come from appsettings.json and can be overridden on the command line, e.g.
//   PlcKi.Server --PlcKi:Plc:Address=192.168.10.10
//   PlcKi.Server --PlcKi:Source=snapshot --PlcKi:SnapshotFile=models/snapshots/step50-drain.json

if (args.Length > 0 && args[0].Equals("import", StringComparison.OrdinalIgnoreCase))
    return await ImportCommand.RunAsync(args[1..]);
if (args.Length > 0 && args[0].Equals("generate", StringComparison.OrdinalIgnoreCase))
    return await GenerateCommand.RunAsync(args[1..]);
if (args.Length > 0 && args[0].Equals("develop", StringComparison.OrdinalIgnoreCase))
{
    // A failure talking to the TIA PC ends the job with one plain line, not a .NET stack trace
    try { return await DevelopCommand.RunAsync(args[1..]); }
    catch (Exception ex) when (ex is InvalidOperationException or HttpRequestException or IOException or System.Text.Json.JsonException)
    {
        Console.Error.WriteLine("ERROR: " + ex.Message);
        return 2;
    }
}
if (args.Length > 0 && args[0].Equals("export", StringComparison.OrdinalIgnoreCase))
    return await ExportCommand.RunAsync(args[1..]);
if (args.Length > 0 && args[0].Equals("link", StringComparison.OrdinalIgnoreCase))
    return await LinkCommand.RunAsync(args[1..]);

bool probe = args.Length > 0 && args[0].Equals("probe", StringComparison.OrdinalIgnoreCase);
var appArgs = probe ? args[1..] : args;

// Jobs outlive an in-page restart, so applying new settings never kills an import that is running
using var jobLogs = LoggerFactory.Create(b => b.AddSimpleConsole(o => o.SingleLine = true));
using var jobs = new JobService(jobLogs.CreateLogger<JobService>());
var linkHub = new LinkHub();     // Link's connection and TIA calls also outlive an in-page restart
while (true)
{
    var restart = false;
    var code = await RunAppAsync(appArgs, probe, jobs, linkHub, () => restart = true);
    if (!restart)
    {
        TiaConnection.StopLocalHelper();
        return code;
    }
    Console.WriteLine();
    Console.WriteLine("Restarting PLC-Ki with the new settings...");
}

static async Task<int> RunAppAsync(string[] appArgs, bool probe, JobService jobs, LinkHub linkHub, Action requestRestart)
{
    var builder = WebApplication.CreateBuilder(new WebApplicationOptions
    {
        Args = appArgs,
        ContentRootPath = AppContext.BaseDirectory,
        WebRootPath = Path.Combine(AppContext.BaseDirectory, "wwwroot"),
    });

    // Plant PC features (src/PlcKi.Plant): Windows service, settings page values, login
    var plant = builder.AddPlant();

    var opt = new PlcKiOptions();
    builder.Configuration.GetSection("PlcKi").Bind(opt);

    string Resolve(string path) => Path.IsPathRooted(path) ? path
        : File.Exists(Path.GetFullPath(path)) ? Path.GetFullPath(path)
        : Path.Combine(AppContext.BaseDirectory, path);

    MachineModel model;
    try
    {
        model = MachineModel.Load(Resolve(opt.Model));
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"Cannot load the machine model '{opt.Model}': {ex.Message}");
        return 2;
    }

    // Fast capture: every change the PLC sends, the last 90 s, cut into a capture around each alarm
    var fast = new FastRecorder();
    IPlcSource CreateSource(ILogger log) => opt.Source.Equals("snapshot", StringComparison.OrdinalIgnoreCase)
        ? new SnapshotFileSource(Resolve(opt.SnapshotFile))
        : opt.Plc.Via == "link" ? new LinkPlcSource(linkHub, opt.Plc)
        : new S7CommPlusSource(opt.Plc, log, fast);

    if (probe)
    {
        // Probe is a diagnostic: show the driver's detailed connection log too
        using var lf = LoggerFactory.Create(b => b.AddSimpleConsole(o => o.SingleLine = true).SetMinimumLevel(LogLevel.Debug));
        return Probe.Run(model, CreateSource(lf.CreateLogger("probe")), fast);
    }

    builder.Services.AddSingleton(opt);
    builder.Services.AddSingleton(model);
    builder.Services.AddSingleton(jobs);
    linkHub.Load(plant.DataDir);
    builder.Services.AddSingleton(linkHub);
    builder.Services.AddSingleton<TiaConnection>();
    builder.Services.AddSingleton(sp =>
    {
        var p = new ProgramService(opt, model, Resolve);
        // A new program (Refresh from TIA): the names the PLC did not know may exist now, so they are looked up again
        if (sp.GetRequiredService<IPlcSource>() is S7CommPlusSource s7) p.Reloaded += s7.ForgetProgram;
        return p;
    });
    builder.Services.AddSingleton(sp => new PlcKi.Server.Hardware.HardwareService(opt, sp.GetRequiredService<ProgramService>(), Resolve));
    builder.Services.AddSingleton(new StatusEngine(model));
    // Events: the last 1,000 in memory for the live pages, and every one on disk, a file per day (data folder\events\<machine>)
    var machineHistory = new MachineHistory(TimeSpan.FromMinutes(Math.Clamp(opt.HistoryMinutes, 1, 240)));
    var eventLog = new EventLog(EventLog.FolderFor(plant.DataDir, PlcKi.Server.Reports.ReportService.Slug(model.Name)));
    machineHistory.Recorded = eventLog.Write;
    builder.Services.AddSingleton(machineHistory);
    builder.Services.AddSingleton(eventLog);
    // Values: the numeric ones on disk too, at the full rate for a few days and a minute at a time for longer (data folder\values\<machine>)
    var valueLog = new ValueLog(ValueLog.FolderFor(plant.DataDir, PlcKi.Server.Reports.ReportService.Slug(model.Name)), Trend.NumericIds(model));
    machineHistory.ValuesRecorded = valueLog.Add;
    builder.Services.AddSingleton(valueLog);
    builder.Services.AddSingleton(new LiveModelCheck(model));
    // Machine memory: every alarm occurrence and the technicians' notes, one file per machine in the data folder
    // Storage limits (Setup › Settings › Storage): read live from settings.json, applied at start and once a day
    var startLimits = SettingsStore.RetentionFrom(builder.Configuration);
    var memory = new AlarmMemory(AlarmMemory.FileFor(plant.DataDir, model.Name), model.Name);
    // The saved limit, before the first alarm is counted
    if (memory.Prune(startLimits.AlarmOccurrences) is var trimmed and > 0)
        Console.WriteLine($"Storage limits: removed {trimmed} alarm occurrences without a note (limit {startLimits.AlarmOccurrences}).");
    builder.Services.AddSingleton(memory);
    builder.Services.AddSingleton(new ModelFile(Resolve(opt.Model)));
    builder.Services.AddSingleton<ModelFixService>();
    builder.Services.AddSingleton(sp =>
    {
        var live = new LiveState(sp.GetRequiredService<MachineHistory>(), sp.GetRequiredService<LiveModelCheck>(), memory);
        // Shift, OEE and fault reports: every status goes into the production log (data folder\reports\<machine>)
        var reports = sp.GetRequiredService<PlcKi.Server.Reports.ReportService>();
        live.Listeners.Add(reports.Log.Add);
        return live;
    });
    builder.Services.AddSingleton(new PlcKi.Server.Reports.ReportService(model, plant.DataDir, memory));
    builder.Services.AddSingleton(fast);
    // Kept per machine model, so a capture never shows next to another machine's tags
    builder.Services.AddSingleton(new AlarmCaptureBook(model, fast, max: startLimits.Recordings,
        folder: Path.Combine(plant.DataDir, "captures", CaptureFolder(opt.Model))));
    builder.Services.AddSingleton(sp => new PlcKi.Server.Storage.StorageService(plant.DataDir, () => sp.GetRequiredService<SettingsStore>().Retention(),
        memory, sp.GetRequiredService<AlarmCaptureBook>(), sp.GetRequiredService<PlcKi.Server.Reports.ReportService>().Log,
        sp.GetRequiredService<ILoggerFactory>().CreateLogger("storage"), valueLog));
    builder.Services.AddHostedService<PlcKi.Server.Storage.StoragePruning>();
    builder.Services.AddSingleton<WatchList>();
    builder.Services.AddSingleton<DeviceWatch>();
    builder.Services.AddSingleton<PlantNoticeService>();
    builder.Services.AddSingleton<PlcKi.Server.Hardware.DiagnosticsService>();
    builder.Services.AddSingleton<PlcWriter>();
    builder.Services.AddSingleton<PlcsimInputs>();
    builder.Services.AddSingleton(opt.Ask);
    // Manuals, drawings and fault lists (Documents page): per machine and shared, under the data folder
    var docs = new PlcKi.Core.Knowledge.DocumentLibrary(Path.Combine(plant.DataDir, "documents"), model.Name);
    _ = docs.Scan();     // picks up files copied into the folders by hand; reads new ones in the background
    builder.Services.AddSingleton(docs);
    builder.Services.AddSingleton(sp =>
    {
        var live = sp.GetRequiredService<LiveState>();
        var captures = sp.GetRequiredService<AlarmCaptureBook>();
        var diag = sp.GetRequiredService<PlcKi.Server.Hardware.DiagnosticsService>();
        return new MachineTools(model, () => new LiveView(live.Status, live.Snapshot, live.History, DateTimeOffset.Now, captures, fast, eventLog, valueLog), live.Memory, diag.View, docs);
    });
    // What Ask used per month, and the monthly limit (read for every question: a new limit works without a restart)
    var askUsage = new AskUsage(Path.Combine(plant.DataDir, "ask-usage.json"));
    builder.Services.AddSingleton(askUsage);
    var askLimit = () => builder.Configuration.GetValue("PlcKi:Ask:MonthlyLimitUsd", 0.0);
    builder.Services.AddSingleton(sp => new AskService(sp.GetRequiredService<AskOptions>(), sp.GetRequiredService<MachineTools>(),
        sp.GetRequiredService<LiveState>(), sp.GetRequiredService<ILogger<AskService>>(), sp.GetRequiredService<PlcKi.Core.Knowledge.DocumentLibrary>(),
        askUsage, askLimit));
    builder.Services.AddSingleton<IPlcSource>(sp => CreateSource(sp.GetRequiredService<ILoggerFactory>().CreateLogger("plc")));
    builder.Services.AddHostedService<PollingService>();
    builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.WriteIndented = false);

    if (string.IsNullOrEmpty(builder.Configuration["urls"]) && string.IsNullOrEmpty(builder.Configuration["Urls"]))
        builder.WebHost.UseUrls("http://localhost:5080");

    var app = builder.Build();
    app.Lifetime.ApplicationStopping.Register(valueLog.Flush);     // the minute being collected goes to disk
    // Work out the drives once at startup, so the polling loop reads their values before anyone opens a page
    _ = Task.Run(() => { try { app.Services.GetRequiredService<PlcKi.Server.Hardware.DiagnosticsService>().Drives(); } catch (Exception) { /* shown on the page */ } });
    if (!plant.IsService)
        plant.RestartInProcess = () => { requestRestart(); app.Lifetime.StopApplication(); };
    app.UsePlant();     // login check before any page or API
    app.UseDefaultFiles();
    // "no-cache": the browser asks each time whether a page file changed (cheap, ETag), so after an update
    // it never keeps showing an old stylesheet or script
    app.UseStaticFiles(new StaticFileOptions
    {
        OnPrepareResponse = ctx => ctx.Context.Response.Headers.CacheControl = "no-cache",
    });

    // Each start of PLC-Ki gets its own id: an open page that sees a new one reloads, so it never shows the
    // program or model of the previous run next to the new one.
    var runId = Guid.NewGuid().ToString("N")[..10];
    app.MapGet("/api/status", (LiveState s, PlantNoticeService notices, HttpContext http) =>
    {
        http.Response.Headers["X-PlcKi-Run"] = runId;
        return s.Status is { } st ? Results.Ok(st with { Notices = notices.Current() }) : Results.Json(new { state = "starting" }, statusCode: 503);
    });

    app.MapGet("/api/model", (MachineModel m) => Results.Json(m, MachineModel.JsonOptions));

    // Raw values with their symbols, for checking against a TIA watch table
    app.MapGet("/api/values", (LiveState s, MachineModel m) =>
    {
        var snap = s.Snapshot;
        return Results.Ok(new
        {
            time = snap?.Time,
            tags = m.Tags.Select(t => new
            {
                t.Id,
                t.Symbol,
                t.Type,
                value = snap?.Values.GetValueOrDefault(t.Id),
                text = ValueFormat.Format(t, snap?.Values.GetValueOrDefault(t.Id)),
                error = snap?.Errors.GetValueOrDefault(t.Id),
            }),
        });
    });

    app.MapEvents();    // /api/events and /api/events.csv: what happened, from the event day files

    // Fast capture: how values are recorded right now, and the seconds around each alarm (read-only)
    app.MapGet("/api/fast", (FastRecorder f, AlarmCaptureBook book) => Results.Ok(new
    {
        mode = f.Mode, cycleMs = f.CycleMs, note = f.Note, fastTags = f.FastTags, readMs = opt.Plc.PollMs,
        changes = f.Count, messages = f.Notifications, lastChange = f.LastChange, oldest = f.Oldest,
        keepSeconds = f.Keep.TotalSeconds, beforeSeconds = book.Before.TotalSeconds, afterSeconds = book.After.TotalSeconds,
        recording = book.Recording(),
    }));
    app.MapGet("/api/captures", (AlarmCaptureBook book, string? alarm, int? max) =>
        Results.Ok(book.List().Where(c => string.IsNullOrEmpty(alarm) || c.AlarmId.Equals(alarm, StringComparison.OrdinalIgnoreCase))
            .Take(Math.Clamp(max ?? 50, 1, 200))));
    app.MapGet("/api/captures/{id}", (string id, AlarmCaptureBook book) =>
        book.Get(id) is { } c ? Results.Json(c, MachineModel.JsonOptions) : Results.NotFound(new { error = "This capture is no longer kept." }));

    // How well the model fits the live machine: what the machine did that the model does not explain (read-only)
    app.MapGet("/api/model-check", (LiveModelCheck check) => Results.Ok(check.Report()));

    // Values over time for the charts: the live buffer, or the values saved on disk for a longer window (read-only)
    TrendEndpoints.Map(app);
    TrendEndpoints.MapCycles(app);

    // "Ask a question": Claude answers from the live values and the machine model (read-only lookups)
    app.MapGet("/api/ask", (AskService ask) =>
    {
        var (enabled, reason) = ask.Availability();
        var code = enabled ? null : !opt.Ask.Enabled ? "off" : askUsage.LimitReached(askLimit()) != null ? "limit" : "nokey";
        return Results.Ok(new { enabled, reason, code, model = opt.Ask.Model });
    });

    // For the settings page: what Ask used this month and before, the limit, and the models it can choose from
    app.MapGet("/api/admin/ask/usage", () => Results.Ok(new
    {
        month = askUsage.ThisMonth(),
        months = askUsage.Months(),
        limit = askLimit(),
        models = AskUsage.Models,
        running = opt.Ask.Model,
    }));

    app.MapPost("/api/ask", async (AskRequest req, AskService ask, CancellationToken ct) =>
    {
        try
        {
            return Results.Ok(await ask.AskAsync(req, ct));
        }
        catch (AskException ex)
        {
            return Results.Json(new { error = ex.Message }, statusCode: ex.Status);
        }
    });

    app.MapDelete("/api/ask/{id}", (string id, AskService ask) =>
    {
        ask.Forget(id);
        return Results.NoContent();
    });

    // What the pages show around every view: machine, where the values come from, the program
    app.MapGet("/api/info", (MachineModel m, PlcKiOptions o, ProgramService program) => Results.Ok(new
    {
        machine = m.Name,
        description = m.Description,
        source = o.Source,
        plc = new { address = o.Plc.Address, via = o.Plc.Via },
        model = o.Model,
        export = o.Export,
        tiaMode = o.Tia.Mode,
        readOnly = false,   // administrators can change values (WriteEndpoints); everything else reads
    }));

    // The program behind the model, for the Program page (read-only, from the export file)
    app.MapGet("/api/program", (ProgramService p) => Results.Ok(p.Overview()));
    // Kestrel keeps an encoded slash as %2F in route values: plant names like "0.2. Inputs/Outputs Images" have one
    app.MapGet("/api/program/blocks/{name}", (string name, ProgramService p) =>
        p.Block(name = name.Replace("%2F", "/", StringComparison.OrdinalIgnoreCase)) is { } b ? Results.Ok(b) : Results.NotFound(new { error = $"Block {name} is not in the program export." }));

    app.MapGet("/api/program/tags", (string? db, ProgramService p) => Results.Ok(p.Tags(db)));
    app.MapGet("/api/program/tags/find", (string? q, ProgramService p) => Results.Ok(p.FindTags(q)));   // the search box
    app.MapGet("/api/program/hmi", (ProgramService p) => Results.Ok(p.Hmi()));

    // Live values for the Tags page: the page names the symbols it shows; the polling loop reads them next to the
    // model tags while the page keeps asking (read-only, at most WatchList.Max symbols)
    app.MapPost("/api/watch", (WatchRequest req, WatchList watch, LiveState live) =>
    {
        var defs = (req.Symbols ?? new()).Where(s => !string.IsNullOrWhiteSpace(s.Symbol)).Take(WatchList.Max)
            .Select(s => new TagDef { Id = s.Symbol!, Symbol = s.Symbol!, Type = string.IsNullOrEmpty(s.Type) ? "Text" : s.Type! }).ToList();
        watch.Ask(defs);
        var values = watch.Values(defs.Select(d => d.Symbol));
        return Results.Ok(new
        {
            time = watch.Time,
            connection = live.Status?.Connection.State,
            values = values.ToDictionary(v => v.Key, v => new { value = v.Value.Value, error = v.Value.Error }),
        });
    });

    app.MapGet("/api/program/search", (string? q, ProgramService p) => Results.Ok(p.Search(q ?? "")));

    // Correct one model entry (Setup > Program > model check): read it, let Ki propose a fix, save it. Admins only.
    app.MapGet("/api/admin/model/entry", (string? target, ModelFixService fix) => ModelFixCall(() => fix.Get(target)));
    app.MapPost("/api/admin/model/entry/ask", async (ModelEntryRequest req, ModelFixService fix, CancellationToken ct) =>
    {
        try { return Results.Ok(await fix.AskAsync(req, ct)); }
        catch (ModelFixException ex) { return Results.Json(new { error = ex.Message }, statusCode: ex.Status); }
    });
    app.MapPost("/api/admin/model/entry", (ModelEntryRequest req, ModelFixService fix, LiveModelCheck check) => ModelFixCall(() =>
    {
        var r = fix.Save(req.Target, req.Json, req.NewTags is { Length: > 2 } nt ? System.Text.Json.Nodes.JsonNode.Parse(nt) as System.Text.Json.Nodes.JsonArray : null);
        check.Reset();
        return r;
    }));
    // "Make a simulator" on the Block generator page: the request, made from the model and the program (nothing runs yet)
    app.MapGet("/api/admin/generator/simulator", (MachineModel m, ProgramService p) => Results.Ok(SimulatorRequest.Build(m, p.Loaded.Plc)));
    app.MapPost("/api/admin/model/check/reset", (LiveModelCheck check) => { check.Reset(); return Results.NoContent(); });

    app.MapPlant();     // /api/auth/... and /api/admin/... (settings, users)
    app.MapSetup();     // /api/admin/... jobs, TIA connection, program import, block generator
    app.MapLink();      // /link/... for PLC-Ki Link on the laptop, /api/admin/link for the Connections page
    PlcKi.Server.Hardware.HardwareEndpoints.MapHardware(app);   // /api/hardware, /api/cpu (PLC page), PLCSIM Start/Stop
    app.MapWrites();    // /api/admin/write: change a value (PLCSIM freely, a real PLC only when allowed and confirmed, never safety)
    app.MapHolds();     // /api/admin/hold: hold a simulated input on PLCSIM (inputs only, never safety I/O); /api/held for the banner
    PlcKi.Server.Mobile.PhoneEndpoints.MapPhone(app);   // /api/admin/phone-address for the QR labels
    app.MapMemory();    // /api/memory/... machine memory: alarm history and notes (History page)
    PlcKi.Server.Storage.StorageEndpoints.MapStorage(app);   // /api/admin/storage: what is kept, limits, export, clear (Settings)
    app.Lifetime.ApplicationStopped.Register(() => memory.Save());
    PlcKi.Server.Reports.ReportEndpoints.MapReports(app);   // /api/reports/... shift report, OEE, fault summary
    PlcKi.Server.Docs.DocumentEndpoints.MapDocuments(app);      // /api/documents (Documents page, alarm page), uploads under /api/admin

    app.Lifetime.ApplicationStarted.Register(() =>
    {
        var urls = string.Join(", ", app.Urls);
        if (app.Urls.Select(u => Uri.TryCreate(u.Replace("://*", "://localhost").Replace("://+", "://localhost"), UriKind.Absolute, out var x) ? x : null)
                .FirstOrDefault(x => x?.Scheme == "http") is { } local)
            linkHub.LocalBase = $"http://localhost:{local.Port}";
        Console.WriteLine();
        Console.WriteLine($"PLC-Ki is running. Open {urls} in your browser.");
        Console.WriteLine($"Model: {model.Name} ({model.Tags.Count} tags). Source: {opt.Source} {(opt.Source != "s7" ? opt.SnapshotFile : opt.Plc.Via == "link" ? opt.Plc.Address + " through PLC-Ki Link" : opt.Plc.Address)}. Reads only; administrators can change values on PLCSIM (and on real PLCs they allow).");
        var (askOn, askWhy) = app.Services.GetRequiredService<AskService>().Availability();
        Console.WriteLine(askOn ? $"Ask a question: on ({opt.Ask.Model})." : $"Ask a question: off. {askWhy}");
        if (opt.Tia.Mode == "local")
        {
            var why = app.Services.GetRequiredService<TiaConnection>().EnsureLocalHelper();
            Console.WriteLine(why == null ? "TIA helper: started on this PC (its own window)." : "TIA helper: " + why);
        }
        Console.WriteLine("Press Ctrl+C to stop.");
    });

    await app.RunAsync();
    return 0;
}

/// <summary>A folder name for a model file ("models/helium.json" -> "helium").</summary>
static string CaptureFolder(string model)
{
    var name = Path.GetFileNameWithoutExtension(model);
    name = new string(name.Select(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_' ? ch : '_').ToArray());
    return string.IsNullOrEmpty(name) ? "model" : name;
}

static IResult ModelFixCall(Func<object> f)
{
    try { return Results.Ok(f()); }
    catch (ModelFixException ex) { return Results.Json(new { error = ex.Message }, statusCode: ex.Status); }
}

/// <summary>POST /api/watch: the symbols a page shows, with their model type (Bool, Int, DInt, Real, Time) when known.</summary>
public sealed record WatchRequest(List<WatchSymbol>? Symbols);
public sealed record WatchSymbol(string? Symbol, string? Type);
