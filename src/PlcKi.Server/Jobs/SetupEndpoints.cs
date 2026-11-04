using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http.Features;
using PlcKi.Core.Generate;
using PlcKi.Core.Import;
using PlcKi.Core.Model;
using PlcKi.Plant;
using PlcKi.Server.Import;
using PlcKi.Server.Live;

namespace PlcKi.Server.Jobs;

public sealed record JobInput(string? Text);
public sealed record ExportRequest(string? Archive);
public sealed record RefreshRequest(string? Project);
public sealed record BuildRequest(string? Export, string? Plc, string? Name, string? Effort, bool Compare);
public sealed record UseProgramRequest(string? Model, string? Export);
public sealed record DevelopRequest(
    string? Package, string? Request, string? Project, string? Plc, string? Edit, bool UseContext, bool SclOnly,
    string? Download, string? PlcAddress, int? Watch, int? Rounds, string? Effort);

/// <summary>
/// What the Setup pages do with buttons instead of commands: import a program (archive -> export -> model),
/// the block generator loop, and the TIA connection. All under /api/admin: administrators only.
/// The read-only rule is unchanged: nothing here talks to a PLC; downloads stay with TiaImporter and its guards.
/// </summary>
public static class SetupEndpoints
{
    private static readonly Regex SafeName = new(@"^[A-Za-z0-9_.-]{1,64}$");

    public static WebApplication MapSetup(this WebApplication app)
    {
        var api = app.MapGroup("/api/admin");
        api.AddEndpointFilter(async (c, next) =>
        {
            try { return await next(c); }
            catch (JobException ex) { return Results.Json(new { error = ex.Message }, statusCode: ex.Status); }
            catch (PlantException ex) { return Results.Json(new { error = ex.Message }, statusCode: ex.Status); }
        });

        // ---- jobs ----
        api.MapGet("/jobs", (JobService jobs) => Results.Ok(jobs.List().Select(j => j.View(int.MaxValue))));
        api.MapGet("/jobs/{id}", (string id, int? from, JobService jobs) =>
            jobs.Find(id) is { } j ? Results.Ok(j.View(from ?? 0)) : Results.NotFound(new { error = "No such job." }));
        api.MapPost("/jobs/{id}/input", (string id, JobInput req, JobService jobs) =>
        {
            jobs.Input(id, req.Text ?? "");
            return Results.NoContent();
        });
        api.MapPost("/jobs/{id}/stop", (string id, JobService jobs) =>
        {
            jobs.Stop(id);
            return Results.NoContent();
        });

        // ---- TIA connection ----
        api.MapGet("/tia/status", async (TiaConnection tia, CancellationToken ct) => Results.Ok(await tia.StatusAsync(ct)));
        api.MapPost("/tia/start-helper", async (TiaConnection tia, CancellationToken ct) =>
        {
            if (tia.Mode != "local") throw new JobException("Only when TIA is on this PC.");
            if (tia.EnsureLocalHelper() is { } why) throw new JobException(why);
            await Task.Delay(1500, ct);
            return Results.Ok(await tia.StatusAsync(ct));
        });
        api.MapGet("/tia/projects", async (TiaConnection tia, CancellationToken ct) =>
        {
            try { return Results.Text(await tia.Client().ProjectsAsync(ct), "application/json"); }
            catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or TaskCanceledException)
            { throw new JobException("Cannot list the TIA projects: " + ex.Message, 502); }
        });

        // ---- program import: archive -> export -> model ----
        api.MapGet("/import/files", (PlantContext ctx, PlcKiOptions opt) => Results.Ok(new
        {
            archives = Files(Dir(ctx, "archives"), "*.zap*"),
            exports = Files(Dir(ctx, "exports"), "*.zip"),
            models = Files(ctx.ModelsDir, "*.json"),
            current = new { model = opt.Model, export = opt.Export },
        }));

        // The file is the request body (no form), so archives of hundreds of MB stream straight to disk
        api.MapPut("/import/upload/{name}", async (string name, HttpRequest request, PlantContext ctx, CancellationToken ct) =>
        {
            var feature = request.HttpContext.Features.Get<IHttpMaxRequestBodySizeFeature>();
            if (feature is { IsReadOnly: false }) feature.MaxRequestBodySize = 2L * 1024 * 1024 * 1024;
            var ext = Path.GetExtension(name).ToLowerInvariant();
            var isArchive = Regex.IsMatch(ext, @"^\.zap\d+$");
            if (!isArchive && ext != ".zip") throw new JobException("Choose a TIA archive (.zap20) or a program export (.zip from TiaImporter export).");
            var safe = ExportCommand.SafeName(Path.GetFileNameWithoutExtension(name)) + ext;
            var file = Path.Combine(Dir(ctx, isArchive ? "archives" : "exports"), safe);
            var tmp = file + ".part";
            await using (var fs = File.Create(tmp)) await request.Body.CopyToAsync(fs, ct);
            if (!isArchive)
            {
                try { Describe(ProgramExport.Load(tmp)); }
                catch (Exception ex) { File.Delete(tmp); throw new JobException("This zip is not a program export: " + ex.Message); }
            }
            File.Move(tmp, file, overwrite: true);
            return Results.Ok(new { kind = isArchive ? "archive" : "export", file, name = Path.GetFileName(file) });
        });

        api.MapGet("/import/export-info", (string file, PlantContext ctx) =>
        {
            var path = Inside(ctx, file, "exports");
            return Results.Ok(Describe(ProgramExport.Load(path)));
        });

        api.MapPost("/import/export", (ExportRequest req, JobService jobs, TiaConnection tia, PlantContext ctx, HttpContext http) =>
        {
            var archive = Inside(ctx, req.Archive ?? "", "archives");
            var vm = tia.Client();   // checks the settings
            var outFile = Path.Combine(Dir(ctx, "exports"), Path.GetFileNameWithoutExtension(archive) + ".zip");
            var job = jobs.Start("export", $"Export {Path.GetFileName(archive)} with TIA", User(http),
                new[] { "export", "--tia", tia.Url!, "--archive", archive, "--out", outFile },
                tia.JobEnvironment(), new() { ["export"] = outFile });
            return Results.Ok(job.View(0));
        });

        // "Refresh from TIA": export the TIA project as it is now and show it at once (code, logic, tags, addresses).
        // The machine model stays; only a new model build re-reads alarms and steps.
        api.MapPost("/import/refresh", (RefreshRequest req, JobService jobs, TiaConnection tia, PlantContext ctx, PlcKiOptions opt,
            SettingsStore store, ProgramService program, HttpContext http) =>
        {
            var project = (req.Project ?? "").Trim();
            if (project.Length == 0) project = store.Current().TiaProject?.Trim() ?? "";
            if (project.Length == 0) throw new JobException("Choose the TIA project to read.");
            tia.Client();   // checks the settings
            var job = StartRefresh(jobs, tia, ctx, opt, store, program, project, User(http));
            try { var st = store.Current(); if (st.TiaProject != project) { st.TiaProject = project; store.Save(st); } } catch (PlantException) { }
            return Results.Ok(job.View(0));
        });

        api.MapPost("/import/build", (BuildRequest req, JobService jobs, PlantContext ctx, PlcKiOptions opt, SettingsStore store, HttpContext http) =>
        {
            var export = Inside(ctx, req.Export ?? "", "exports", allowApp: true);
            var name = ExportCommand.SafeName(string.IsNullOrWhiteSpace(req.Name) ? Path.GetFileNameWithoutExtension(export) : req.Name!);
            var outFile = Path.Combine(ctx.ModelsDir, name + ".json");
            var args = new List<string> { "import", "--export", export, "--out", outFile, "--effort", req.Effort is { Length: > 0 } e ? e : "high" };
            if (!string.IsNullOrWhiteSpace(req.Plc)) { args.Add("--plc"); args.Add(req.Plc!); }
            if (req.Compare) { args.Add("--compare"); args.Add(ctx.ResolveAppPath(opt.Model)); }
            var job = jobs.Start("import", $"Build the machine model from {Path.GetFileName(export)}", User(http), args, null,
                new() { ["model"] = outFile, ["export"] = export });
            store.LinkExport(outFile, export);   // choosing this model later also picks its export
            return Results.Ok(job.View(0));
        });

        api.MapGet("/import/model-info", (string file, PlantContext ctx) =>
        {
            var path = Inside(ctx, file, null, allowModels: true, allowApp: true);
            MachineModel m;
            try { m = MachineModel.Load(path); }
            catch (Exception ex) { return Results.Ok(new { ok = false, error = ex.Message }); }
            return Results.Ok(new
            {
                ok = true, name = m.Name, description = m.Description, tags = m.Tags.Count, sequences = m.Sequences.Count,
                steps = m.Sequences.Sum(s => s.Steps.Count), equipment = m.Equipment.Count, alarms = m.Alarms.Count, exchange = m.Exchange.Count,
                overview = m.Overview,
            });
        });

        // Make this the program PLC-Ki watches: saves the settings; applied at the next (in-page) restart
        api.MapPost("/import/use", (UseProgramRequest req, SettingsStore store, PlantContext ctx) =>
        {
            var model = Inside(ctx, req.Model ?? "", null, allowModels: true, allowApp: true);
            var s = store.Current();
            s.Model = model;
            if (!string.IsNullOrWhiteSpace(req.Export)) { s.Export = Inside(ctx, req.Export!, "exports", allowApp: true); store.LinkExport(model, s.Export); }
            store.Save(s);
            return Results.Ok(new { restartNeeded = store.RestartNeeded() });
        });

        // ---- block generator ----
        api.MapGet("/generator/packages", (PlantContext ctx) =>
        {
            var root = Dir(ctx, "packages");
            return Results.Ok(Directory.GetDirectories(root)
                .Select(d => (dir: d, h: TryHistory(d)))
                .Where(x => x.h != null)
                .OrderByDescending(x => x.h!.Latest?.Time)
                .Select(x => new
                {
                    name = Path.GetFileName(x.dir),
                    versions = x.h!.Versions.Count,
                    latest = x.h.Latest?.Folder,
                    time = x.h.Latest?.Time,
                    request = x.h.Versions.FirstOrDefault()?.Request,
                    state = ReportOf(x.dir, x.h.Latest?.Folder)?.CompileState,
                }));
        });

        api.MapGet("/generator/packages/{name}", (string name, PlantContext ctx) =>
        {
            var dir = Package(ctx, name);
            var h = TryHistory(dir) ?? throw new JobException("No such package.", 404);
            return Results.Ok(new
            {
                name, h.Plc, h.Context,
                versions = h.Versions.Select((v, i) => (v, prev: i > 0 ? h.Versions[i - 1].Folder : null)).Reverse().Select(x =>
                {
                    var v = x.v;
                    var vdir = Path.Combine(dir, v.Folder);
                    PackageSummary? summary = null;
                    try { if (Directory.Exists(vdir)) summary = PackageSummary.Compare(x.prev != null ? Path.Combine(dir, x.prev) : null, vdir); }
                    catch (IOException) { /* a folder being written: no summary this time */ }
                    var review = Path.Combine(vdir, BlockPackage.ReviewFile);
                    return new
                    {
                        v.Folder, v.Version, v.Time, v.Kind, v.Request, v.Files, v.Added, v.Changed, v.Removed, v.Problems, v.Replaces,
                        review = File.Exists(review) ? File.ReadAllText(review) : null,
                        report = ReportOf(dir, v.Folder),
                        summary,
                    };
                }),
            });
        });

        api.MapGet("/generator/packages/{name}/{version}/{file}", (string name, string version, string file, PlantContext ctx) =>
        {
            if (!Regex.IsMatch(version, @"^v\d{3}$") || !BlockPackage.IsPackageFile(file)) throw new JobException("No such file.", 404);
            var path = Path.Combine(Package(ctx, name), version, file);
            return File.Exists(path) ? Results.Text(File.ReadAllText(path), "text/plain; charset=utf-8") : Results.NotFound();
        });

        api.MapPost("/generator/run", (DevelopRequest req, JobService jobs, TiaConnection tia, PlantContext ctx, PlcKiOptions opt, SettingsStore store,
            ProgramService program, HttpContext http) =>
        {
            var name = (req.Package ?? "").Trim();
            if (!SafeName.IsMatch(name)) throw new JobException("Give the job a short name (letters, digits, - and _), for example pump-fb.");
            var dir = Path.Combine(Dir(ctx, "packages"), name);
            var project = (req.Project ?? "").Trim();
            if (project.Length == 0) throw new JobException("Choose the TIA project the blocks go into.");
            var hasRequest = !string.IsNullOrWhiteSpace(req.Request);
            if (!hasRequest && TryHistory(dir)?.Latest == null) throw new JobException("Say what you need.");
            tia.Client();   // checks the settings

            var args = new List<string> { "develop", "--vm", tia.Url!, "--project", project, "--out", dir };
            if (hasRequest) { args.Add("--request"); args.Add(req.Request!.Trim()); }
            if (!string.IsNullOrWhiteSpace(req.Plc)) { args.Add("--plc"); args.Add(req.Plc!.Trim()); }
            if (req.UseContext && !string.IsNullOrWhiteSpace(opt.Export)) { args.Add("--context"); args.Add(ctx.ResolveAppPath(opt.Export)); }
            if (!string.IsNullOrWhiteSpace(req.Edit))
            {
                if (!req.UseContext || string.IsNullOrWhiteSpace(opt.Export)) throw new JobException("Changing existing blocks needs the current program (\"Fit into the current program\").");
                args.Add("--edit"); args.Add(req.Edit!.Trim());
            }
            if (req.SclOnly) args.Add("--scl-only");
            if (req.Rounds is { } r) { args.Add("--rounds"); args.Add(Math.Clamp(r, 0, 6).ToString()); }
            if (req.Effort is { Length: > 0 } e) { args.Add("--effort"); args.Add(e); }
            var download = req.Download ?? "none";
            var title = hasRequest ? $"Generate {name}" : $"Import {name} again";
            if (download == "plcsim")
            {
                args.Add("--download");
                if (!string.IsNullOrWhiteSpace(req.PlcAddress)) { args.Add("--plc-address"); args.Add(req.PlcAddress!.Trim()); }
                args.Add("--watch"); args.Add(Math.Clamp(req.Watch ?? 30, 5, 600).ToString());
                title += " → PLCSIM";
            }
            else if (download == "real")
            {
                args.Add("--download-real");
                title += " → real PLC";
            }
            else if (download != "none") throw new JobException("Download is none, plcsim or real.");
            // Blocks went into the program PLC-Ki shows: read it again afterwards, so Code, Logic and Tags show them too
            var sameProgram = string.Equals(program.Project, ProjectName(project), StringComparison.OrdinalIgnoreCase);
            var user = User(http);
            var job = jobs.Start("develop", title, user, args, tia.JobEnvironment(), new() { ["package"] = name },
                onExit: done =>
                {
                    if (!sameProgram || done.ExitCode != 0) return;
                    try { StartRefresh(jobs, tia, ctx, opt, store, program, project, user); }
                    catch (JobException) { /* another job started meanwhile: the page offers the refresh button */ }
                });
            // Remember the project for next time (best effort: never stops the job)
            try { var st = store.Current(); if (st.TiaProject != project) { st.TiaProject = project; store.Save(st); } } catch (PlantException) { }
            return Results.Ok(job.View(0));
        });

        return app;
    }

    // ---- helpers ----

    /// <summary>The project's file name without folder and extension; the path is a Windows path on the TIA PC.</summary>
    public static string ProjectName(string project) =>
        Path.GetFileNameWithoutExtension(project.Replace('\\', '/').Split('/').Last());

    /// <summary>
    /// Starts the export of <paramref name="project"/> on the TIA PC. When it succeeds, the program pages show it at once
    /// and it is saved as the program export (no restart), linked to the running model.
    /// </summary>
    internal static Job StartRefresh(JobService jobs, TiaConnection tia, PlantContext ctx, PlcKiOptions opt, SettingsStore store,
        ProgramService program, string project, string user)
    {
        var name = ExportCommand.SafeName(ProjectName(project));
        var outFile = Path.Combine(Dir(ctx, "exports"), name + "-from-TIA.zip");
        var tmp = Path.Combine(Dir(ctx, "refresh"), name + "-from-TIA.zip");   // only replaces the shown export once it is complete
        return jobs.Start("refresh", $"Refresh the program from TIA ({name})", user,
            new[] { "export", "--tia", tia.Url!, "--project", project, "--out", tmp },
            tia.JobEnvironment(), new() { ["export"] = outFile },
            beforeFinish: (job, code) =>
            {
                if (code != 0 || !File.Exists(tmp)) { try { File.Delete(tmp); } catch (IOException) { } return; }
                File.Move(tmp, outFile, overwrite: true);
                if (program.Reload(outFile) is { } error) { job.Line("PLC-Ki cannot show this export: " + error); return; }
                opt.Export = outFile;
                try { store.SaveExportLive(outFile); }
                catch (PlantException ex) { job.Line("Shown now, but not saved for the next start: " + ex.Message); }
                if (!string.IsNullOrWhiteSpace(opt.Model)) store.LinkExport(ctx.ResolveAppPath(opt.Model), outFile);
                job.Line("PLC-Ki now shows this program: Code, Logic and Tags use the new addresses. Alarms and steps still come from the machine model.");
            });
    }

    private static string User(HttpContext http) => http.User.Identity?.Name ?? "?";

    private static string Dir(PlantContext ctx, string sub) => Directory.CreateDirectory(Path.Combine(ctx.WorkDir, sub)).FullName;

    private static string Package(PlantContext ctx, string name)
    {
        if (!SafeName.IsMatch(name)) throw new JobException("No such package.", 404);
        return Path.Combine(Dir(ctx, "packages"), name);
    }

    private static PackageHistory? TryHistory(string dir)
    {
        try { return File.Exists(Path.Combine(dir, BlockPackage.ManifestFile)) ? PackageHistory.Load(dir) : null; }
        catch { return null; }
    }

    private static ImportReport? ReportOf(string dir, string? version)
    {
        if (version == null) return null;
        var f = Path.Combine(dir, version, BlockPackage.ReportFile);
        try { return File.Exists(f) ? JsonSerializer.Deserialize<ImportReport>(File.ReadAllText(f)) : null; }
        catch { return null; }
    }

    private static List<object> Files(string dir, string pattern) => !Directory.Exists(dir) ? new()
        : new DirectoryInfo(dir).GetFiles(pattern).Where(f => !f.Name.EndsWith(".part")).OrderByDescending(f => f.LastWriteTimeUtc)
            .Select(f => (object)new { name = f.Name, path = f.FullName, size = f.Length, time = f.LastWriteTime }).ToList();

    /// <summary>A file the page names: only inside PLC-Ki's own work folders (or shipped with the app), never anywhere on disk.</summary>
    private static string Inside(PlantContext ctx, string file, string? workSub, bool allowModels = false, bool allowApp = false)
    {
        if (string.IsNullOrWhiteSpace(file)) throw new JobException("Choose a file first.");
        var full = Path.GetFullPath(ctx.ResolveAppPath(file.Trim()));
        var roots = new List<string>();
        if (workSub != null) roots.Add(Dir(ctx, workSub));
        if (allowModels) roots.Add(ctx.ModelsDir);
        if (allowApp) roots.Add(Path.Combine(ctx.AppDir, "models"));
        if (!roots.Any(r => full.StartsWith(Path.GetFullPath(r).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)))
            throw new JobException("That file is not in PLC-Ki's folders.", 403);
        if (!File.Exists(full)) throw new JobException("File not found: " + Path.GetFileName(full), 404);
        return full;
    }

    private static object Describe(ProgramExport export) => new
    {
        project = export.Project,
        archive = export.Archive,
        problems = export.Problems,
        plcs = export.Plcs.Select(p => new
        {
            p.Name, p.Cpu, ip = p.IpAddresses, blocks = p.Blocks.Count,
            tags = ProgramExport.Tags(p).Count(t => t.ModelType != null),
            languages = p.Blocks.SelectMany(b => b.NetworkLanguages.Append(b.Language ?? "")).Where(l => l.Length > 0).Distinct(),
            p.Problems,
        }),
    };
}
