using PlcKi.Core.Ask;
using PlcKi.Core.Model;
using PlcKi.Core.Status;
using PlcKi.Server;
using PlcKi.Server.Ask;
using PlcKi.Server.Import;
using PlcKi.Server.Live;

// PLC-Ki server.
//   PlcKi.Server                 start the web app (default http://localhost:5080)
//   PlcKi.Server probe           connect once, read every tag, print the values and the status, exit
//   PlcKi.Server import ...      write a machine model from a TIA program export with Claude (see Import/ImportCommand.cs)
// Settings come from appsettings.json and can be overridden on the command line, e.g.
//   PlcKi.Server --PlcKi:Plc:Address=192.168.10.10
//   PlcKi.Server --PlcKi:Source=snapshot --PlcKi:SnapshotFile=models/snapshots/step50-drain.json

if (args.Length > 0 && args[0].Equals("import", StringComparison.OrdinalIgnoreCase))
    return await ImportCommand.RunAsync(args[1..]);

bool probe = args.Length > 0 && args[0].Equals("probe", StringComparison.OrdinalIgnoreCase);
var appArgs = probe ? args[1..] : args;

var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = appArgs,
    ContentRootPath = AppContext.BaseDirectory,
    WebRootPath = Path.Combine(AppContext.BaseDirectory, "wwwroot"),
});

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

IPlcSource CreateSource(ILogger log) => opt.Source.Equals("snapshot", StringComparison.OrdinalIgnoreCase)
    ? new SnapshotFileSource(Resolve(opt.SnapshotFile))
    : new S7CommPlusSource(opt.Plc, log);

if (probe)
{
    // Probe is a diagnostic: show the driver's detailed connection log too
    using var lf = LoggerFactory.Create(b => b.AddSimpleConsole(o => o.SingleLine = true).SetMinimumLevel(LogLevel.Debug));
    return Probe.Run(model, CreateSource(lf.CreateLogger("probe")));
}

builder.Services.AddSingleton(opt);
builder.Services.AddSingleton(model);
builder.Services.AddSingleton(new StatusEngine(model));
builder.Services.AddSingleton(new MachineHistory(TimeSpan.FromMinutes(Math.Clamp(opt.HistoryMinutes, 1, 240))));
builder.Services.AddSingleton<LiveState>();
builder.Services.AddSingleton(opt.Ask);
builder.Services.AddSingleton(sp =>
{
    var live = sp.GetRequiredService<LiveState>();
    return new MachineTools(model, () => new LiveView(live.Status, live.Snapshot, live.History, DateTimeOffset.Now));
});
builder.Services.AddSingleton<AskService>();
builder.Services.AddSingleton<IPlcSource>(sp => CreateSource(sp.GetRequiredService<ILoggerFactory>().CreateLogger("plc")));
builder.Services.AddHostedService<PollingService>();
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.WriteIndented = false);

if (string.IsNullOrEmpty(builder.Configuration["urls"]) && string.IsNullOrEmpty(builder.Configuration["Urls"]))
    builder.WebHost.UseUrls("http://localhost:5080");

var app = builder.Build();
app.UseDefaultFiles();
// "no-cache": the browser asks each time whether a page file changed (cheap, ETag), so after an update
// it never keeps showing an old stylesheet or script
app.UseStaticFiles(new StaticFileOptions
{
    OnPrepareResponse = ctx => ctx.Context.Response.Headers.CacheControl = "no-cache",
});

app.MapGet("/api/status", (LiveState s) =>
    s.Status is { } st ? Results.Ok(st) : Results.Json(new { state = "starting" }, statusCode: 503));

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

// "Ask a question": Claude answers from the live values and the machine model (read-only lookups)
app.MapGet("/api/ask", (AskService ask) =>
{
    var (enabled, reason) = ask.Availability();
    return Results.Ok(new { enabled, reason, model = opt.Ask.Model });
});

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

app.Lifetime.ApplicationStarted.Register(() =>
{
    var urls = string.Join(", ", app.Urls);
    Console.WriteLine();
    Console.WriteLine($"PLC-Ki is running. Open {urls} in your browser.");
    Console.WriteLine($"Model: {model.Name} ({model.Tags.Count} tags). Source: {opt.Source} {(opt.Source == "s7" ? opt.Plc.Address : opt.SnapshotFile)}. Read-only.");
    var (askOn, askWhy) = app.Services.GetRequiredService<AskService>().Availability();
    Console.WriteLine(askOn ? $"Ask a question: on ({opt.Ask.Model})." : $"Ask a question: off. {askWhy}");
    Console.WriteLine("Press Ctrl+C to stop.");
});

app.Run();
return 0;
