using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace TiaImporter
{
    /// <summary>
    /// "serve": a small HTTP service on the TIA PC, so PLC-Ki on another PC can run the generate, import, compile and
    /// PLCSIM download loop without copying folders by hand. One import or download at a time, run as a job the PC polls; every request needs the token.
    ///
    ///   GET  /status                 alive check, settings
    ///   PUT  /packages/{name}        body: zip of a package folder (generated.json + v001, v002, ...)
    ///   POST /import                 {"package","project","plc","version","replace","keepOnError"} -> {"job"}
    ///   POST /download               {"project","plc","real"} -> {"job"}: PLCSIM, or a guarded real PLC (see PlcsimDownload)
    ///   GET  /jobs/{id}              {"done","log"} and when done {"exitCode","report"}
    ///   GET  /projects               the .ap20 projects under the allowed folder, for the PLC-Ki pages
    ///   PUT  /archives/{name}        body: a .zap20 archive to export
    ///   POST /export                 {"archive"} -> {"job"}: export the archive's PLC programs (TiaImporter export)
    ///   GET  /exports/{name}         the finished export as a zip
    ///   GET  /cpu                    RUN/STOP state (and LEDs) of the PLCSIM CPU at --plcsim-ip (PLCSIM Advanced API)
    ///   POST /cpu                    {"mode":"run"|"stop","ip"}: switches that PLCSIM CPU only; never a real PLC
    ///
    /// Projects are opened only when a request names them, and only .ap20 files under the allowed folder.
    /// </summary>
    internal sealed class Serve
    {
        private static readonly Regex PackageName = new Regex(@"^[A-Za-z0-9_.-]{1,64}$");
        private readonly int _port;
        private readonly string _token;
        private readonly string _plcsimIp;
        private readonly DirectoryInfo _packages;
        private readonly DirectoryInfo _projects;
        private readonly HashSet<string> _allowReal;
        private readonly bool _localOnly;
        private readonly Dictionary<string, Job> _jobs = new Dictionary<string, Job>();

        public Serve(int port, string token, string plcsimIp, DirectoryInfo packages, DirectoryInfo projects, IEnumerable<string> allowReal = null,
            bool localOnly = false)
        {
            _localOnly = localOnly;
            _allowReal = new HashSet<string>(allowReal ?? Enumerable.Empty<string>());
            _port = port;
            _token = token;
            _plcsimIp = plcsimIp;
            _packages = packages;
            _projects = projects;
        }

        public int Run()
        {
            if (string.IsNullOrWhiteSpace(_token) || _token.Length < 8)
                throw new InvalidOperationException("Give a --token of at least 8 characters; PLC-Ki sends the same one.");
            _packages.Create();
            var listener = new HttpListener();
            // --local: only this PC (PLC-Ki on the same PC as TIA); needs no administrator rights or firewall rule
            listener.Prefixes.Add(_localOnly ? $"http://localhost:{_port}/" : $"http://+:{_port}/");
            try { listener.Start(); }
            catch (HttpListenerException ex)
            {
                throw new InvalidOperationException($"Cannot listen on port {_port}: {ex.Message}. Run once as administrator:\n" +
                    $"  netsh http add urlacl url=http://+:{_port}/ user=%USERNAME%\n" +
                    $"  netsh advfirewall firewall add rule name=\"PLC-Ki TiaImporter\" dir=in action=allow protocol=TCP localport={_port}");
            }
            Console.WriteLine($"TiaImporter serve on port {_port}{(_localOnly ? " (this PC only)" : "")}. Packages in {_packages.FullName}, projects under {_projects.FullName}.");
            Console.WriteLine(_plcsimIp != null ? $"PLCSIM downloads to {_plcsimIp}." : "PLCSIM downloads are off (no --plcsim-ip).");
            Console.WriteLine(_allowReal.Count > 0
                ? $"REAL PLC downloads allowed to {string.Join(", ", _allowReal)}, each confirmed by typing the address in this window."
                : "Real PLC downloads are off (no --allow-real).");
            Console.WriteLine("Press Ctrl+C to stop.");
            while (true)
            {
                var ctx = listener.GetContext();   // requests are answered one by one; imports and downloads run as jobs
                try { Handle(ctx); }
                catch (Exception ex)
                {
                    Console.WriteLine("ERROR: " + ex.Message);
                    try { Reply(ctx, 500, new Dictionary<string, object> { ["error"] = ex.Message }); } catch { /* client gone */ }
                }
            }
        }

        private void Handle(HttpListenerContext ctx)
        {
            var req = ctx.Request;
            var path = req.Url.AbsolutePath.TrimEnd('/');
            Console.WriteLine($"{DateTime.Now:HH:mm:ss} {req.HttpMethod} {path} from {req.RemoteEndPoint?.Address}");
            if (req.Headers["X-PlcKi-Token"] != _token) { Reply(ctx, 401, new Dictionary<string, object> { ["error"] = "Wrong or missing token." }); return; }

            if (req.HttpMethod == "GET" && path == "/status")
            {
                Reply(ctx, 200, new Dictionary<string, object>
                {
                    ["ok"] = true, ["machine"] = Environment.MachineName, ["packages"] = _packages.FullName,
                    ["projects"] = _projects.FullName, ["plcsimIp"] = _plcsimIp, ["realAllowed"] = _allowReal.Count > 0,
                });
                return;
            }
            if (req.HttpMethod == "PUT" && path.StartsWith("/packages/"))
            {
                var name = Uri.UnescapeDataString(path.Substring("/packages/".Length));
                if (!PackageName.IsMatch(name)) throw new InvalidOperationException("Package names use letters, digits, _ . - only.");
                var dir = new DirectoryInfo(Path.Combine(_packages.FullName, name));
                var tmp = Path.GetTempFileName();
                try
                {
                    using (var f = File.Create(tmp)) req.InputStream.CopyTo(f);
                    ExtractPackage(tmp, dir);
                }
                finally { File.Delete(tmp); }
                Reply(ctx, 200, new Dictionary<string, object> { ["ok"] = true, ["folder"] = dir.FullName });
                return;
            }
            if (req.HttpMethod == "GET" && path == "/projects")
            {
                var list = _projects.Exists
                    ? _projects.EnumerateFiles("*.ap20", SearchOption.AllDirectories)
                        .Where(f => f.DirectoryName.IndexOf("\\UserFiles", StringComparison.OrdinalIgnoreCase) < 0)
                        .OrderByDescending(f => f.LastWriteTimeUtc).Take(200)
                        .Select(f => (object)new Dictionary<string, object> { ["path"] = f.FullName, ["name"] = Path.GetFileNameWithoutExtension(f.Name), ["changed"] = f.LastWriteTimeUtc.ToString("o") })
                        .ToList()
                    : new List<object>();
                Reply(ctx, 200, new Dictionary<string, object> { ["projects"] = list, ["folder"] = _projects.FullName });
                return;
            }
            if (req.HttpMethod == "PUT" && path.StartsWith("/archives/"))
            {
                var name = Uri.UnescapeDataString(path.Substring("/archives/".Length));
                if (!PackageName.IsMatch(name) || !Regex.IsMatch(name, @"\.zap\d+$", RegexOptions.IgnoreCase))
                    throw new InvalidOperationException("Archive names use letters, digits, _ . - only and end in .zap20.");
                var dir = Directory.CreateDirectory(Path.Combine(_packages.FullName, "archives"));
                var file = Path.Combine(dir.FullName, name);
                using (var f = File.Create(file)) req.InputStream.CopyTo(f);
                Reply(ctx, 200, new Dictionary<string, object> { ["ok"] = true, ["file"] = file });
                return;
            }
            if (req.HttpMethod == "GET" && path.StartsWith("/exports/"))
            {
                var name = Uri.UnescapeDataString(path.Substring("/exports/".Length));
                var zip = new FileInfo(Path.Combine(_packages.FullName, "exports", name + ".zip"));
                if (!PackageName.IsMatch(name) || !zip.Exists) { Reply(ctx, 404, new Dictionary<string, object> { ["error"] = "No such export." }); return; }
                ctx.Response.StatusCode = 200;
                ctx.Response.ContentType = "application/zip";
                ctx.Response.ContentLength64 = zip.Length;
                using (var f = zip.OpenRead()) f.CopyTo(ctx.Response.OutputStream);
                ctx.Response.Close();
                return;
            }
            if (req.HttpMethod == "GET" && path == "/cpu")
            {
                var st = _plcsimIp == null
                    ? new Dictionary<string, object> { ["found"] = false, ["error"] = "No PLCSIM address on the TIA helper (start serve with --plcsim-ip)." }
                    : PlcsimControl.State(_plcsimIp);
                st["plcsimIp"] = _plcsimIp;
                Reply(ctx, 200, st);
                return;
            }
            if (req.HttpMethod == "POST" && path == "/cpu")
            {
                JsonElement body;
                using (var r = new StreamReader(req.InputStream, Encoding.UTF8)) body = JsonDocument.Parse(r.ReadToEnd()).RootElement.Clone();
                // Only the PLCSIM CPU this helper downloads to; the caller names it, so a mix-up is refused, not guessed
                if (_plcsimIp == null) throw new InvalidOperationException("Start/Stop is off: start serve with --plcsim-ip <PLCSIM address>.");
                if (!string.Equals(Str(body, "ip"), _plcsimIp, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException($"Refused: Start/Stop only works on the PLCSIM CPU at {_plcsimIp}, not {Str(body, "ip") ?? "(no address)"}.");
                var mode = Str(body, "mode");
                var log = new StringWriter();
                var st = PlcsimControl.SetMode(_plcsimIp, mode, log);
                Console.Write(log.ToString());
                try { File.AppendAllText(Path.Combine(_packages.FullName, "plcsim-mode.log"), $"{DateTime.Now:o} {mode} {_plcsimIp} from {req.RemoteEndPoint?.Address}\r\n"); }
                catch (IOException) { /* log is a courtesy */ }
                st["plcsimIp"] = _plcsimIp;
                Reply(ctx, 200, st);
                return;
            }
            if (req.HttpMethod == "GET" && path.StartsWith("/jobs/"))
            {
                Job job;
                lock (_jobs) _jobs.TryGetValue(path.Substring("/jobs/".Length), out job);
                if (job == null) { Reply(ctx, 404, new Dictionary<string, object> { ["error"] = "Unknown job (serve was restarted?)." }); return; }
                Reply(ctx, 200, job.Answer());
                return;
            }
            if (req.HttpMethod == "POST" && (path == "/import" || path == "/download" || path == "/export"))
            {
                // Checked here, so a bad request is refused at once; the work itself runs as a job the PC polls,
                // so a short network drop (the download can reset the adapter) does not lose the result.
                JsonElement body;
                using (var r = new StreamReader(req.InputStream, Encoding.UTF8)) body = JsonDocument.Parse(r.ReadToEnd()).RootElement.Clone();
                var isExport = path == "/export";
                var project = isExport ? null : Project(Str(body, "project"));
                var plc = Str(body, "plc");
                FileInfo archive = null;
                if (isExport)
                {
                    var name = Str(body, "archive");
                    if (name == null || !PackageName.IsMatch(name)) throw new InvalidOperationException("Name the archive.");
                    archive = new FileInfo(Path.Combine(_packages.FullName, "archives", name));
                    if (!archive.Exists) throw new FileNotFoundException("Upload the archive first: " + name);
                }
                var requestedBy = req.RemoteEndPoint?.Address?.ToString() ?? "?";
                DirectoryInfo dir = null;
                if (path == "/import")
                {
                    var name = Str(body, "package");
                    if (name == null || !PackageName.IsMatch(name)) throw new InvalidOperationException("Name the package.");
                    dir = new DirectoryInfo(Path.Combine(_packages.FullName, name));
                }
                else if (!isExport && !Bool(body, "real") && _plcsimIp == null)
                    throw new InvalidOperationException("Downloads are off: start serve with --plcsim-ip <PLCSIM address>.");

                Job job;
                lock (_jobs)
                {
                    if (_jobs.Values.Any(j => !j.Done))
                    {
                        Reply(ctx, 409, new Dictionary<string, object> { ["error"] = "The TIA PC is busy with another import or download. Try again when it has finished." });
                        return;
                    }
                    job = new Job();
                    _jobs[job.Id] = job;
                    foreach (var old in _jobs.Values.Where(j => j.Done).OrderBy(j => j.Started).Take(Math.Max(0, _jobs.Count - 10)).ToList()) _jobs.Remove(old.Id);
                }
                var tee = new Tee(Console.Out, job);
                var thread = new System.Threading.Thread(() =>
                {
                    int code;
                    string report = null;
                    try
                    {
                        if (archive != null)
                        {
                            var name = Path.GetFileNameWithoutExtension(archive.Name);
                            var exports = Directory.CreateDirectory(Path.Combine(_packages.FullName, "exports"));
                            var outDir = new DirectoryInfo(Path.Combine(exports.FullName, name));
                            if (outDir.Exists) outDir.Delete(true);
                            code = new Exporter(tee).Run(archive, outDir, Bool(body, "hardwareOnly"));
                            var zip = Path.Combine(exports.FullName, name + ".zip");
                            if (File.Exists(zip)) File.Delete(zip);
                            if (code == 0 || outDir.Exists) ZipFile.CreateFromDirectory(outDir.FullName, zip, CompressionLevel.Optimal, false);
                            report = JsonSerializer.Serialize(new Dictionary<string, object> { ["export"] = name });
                        }
                        else if (dir != null)
                        {
                            var started = DateTime.UtcNow.AddSeconds(-2);
                            code = new BlockImport(tee).Run(project, dir, plc, Str(body, "version"), Bool(body, "replace"), Bool(body, "keepOnError"));
                            var reportFile = dir.Exists ? dir.GetFiles("import-report.json", SearchOption.AllDirectories).OrderByDescending(f => f.LastWriteTimeUtc).FirstOrDefault() : null;
                            if (reportFile != null && reportFile.LastWriteTimeUtc >= started) report = File.ReadAllText(reportFile.FullName);   // only a report this job wrote
                        }
                        else if (Bool(body, "real"))
                            code = new PlcsimDownload(tee).RunReal(project, plc, _allowReal, AskHere, Path.Combine(_packages.FullName, "real-downloads.log"), requestedBy);
                        else
                            code = new PlcsimDownload(tee).Run(project, plc, _plcsimIp);
                    }
                    catch (Exception ex)
                    {
                        tee.WriteLine("ERROR: " + ex.Message);
                        code = 1;
                    }
                    job.Finish(code, report);
                    Console.WriteLine($"{DateTime.Now:HH:mm:ss} job {job.Id} finished with exit code {code}.");
                }) { IsBackground = true };
                thread.Start();
                Reply(ctx, 202, new Dictionary<string, object> { ["job"] = job.Id });
                return;
            }
            Reply(ctx, 404, new Dictionary<string, object> { ["error"] = $"Unknown request {req.HttpMethod} {path}" });
        }

        /// <summary>Asks the person at this window; no answer within 2 minutes counts as "no".</summary>
        private static string AskHere(string question)
        {
            Console.Beep();
            Console.WriteLine();
            Console.Write(question);
            var read = System.Threading.Tasks.Task.Run(() => Console.ReadLine());
            if (read.Wait(TimeSpan.FromMinutes(2))) return read.Result;
            Console.WriteLine();
            Console.WriteLine("No answer in 2 minutes: cancelled. (Press Enter once to clear the prompt.)");
            return null;
        }

        /// <summary>The project file named in the request: an existing .ap20 under the allowed folder.</summary>
        private FileInfo Project(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) throw new InvalidOperationException("Name the project (.ap20).");
            var full = new FileInfo(Path.GetFullPath(path));
            if (!full.Extension.Equals(".ap20", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("The project must be an .ap20 file.");
            if (!full.FullName.StartsWith(_projects.FullName.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"Projects must be under {_projects.FullName}.");
            if (!full.Exists) throw new FileNotFoundException($"Project not found: {full.FullName}");
            return full;
        }

        /// <summary>Unpacks a package zip into its folder, replacing what was there. Entries may not leave the folder.</summary>
        private static void ExtractPackage(string zipFile, DirectoryInfo dir)
        {
            var root = dir.FullName.TrimEnd('\\') + "\\";
            using (var zip = ZipFile.OpenRead(zipFile))
            {
                foreach (var e in zip.Entries)
                {
                    var target = Path.GetFullPath(Path.Combine(dir.FullName, e.FullName));
                    if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException($"Bad entry in the zip: {e.FullName}");
                }
                if (dir.Exists)
                    foreach (var sub in dir.GetDirectories("v*")) if (Regex.IsMatch(sub.Name, @"^v\d{3}$"))
                        foreach (var f in sub.GetFiles()) if (f.Name != "import-report.json") f.Delete();   // reports from earlier runs stay
                dir.Create();
                foreach (var e in zip.Entries)
                {
                    var target = Path.GetFullPath(Path.Combine(dir.FullName, e.FullName));
                    if (e.FullName.EndsWith("/") || e.FullName.EndsWith("\\")) { Directory.CreateDirectory(target); continue; }
                    Directory.CreateDirectory(Path.GetDirectoryName(target));
                    e.ExtractToFile(target, true);
                }
            }
        }

        private static string Str(JsonElement e, string name) =>
            e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

        private static bool Bool(JsonElement e, string name) =>
            e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

        private static void Reply(HttpListenerContext ctx, int status, Dictionary<string, object> body)
        {
            var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(body));
            ctx.Response.StatusCode = status;
            ctx.Response.ContentType = "application/json; charset=utf-8";
            ctx.Response.ContentLength64 = bytes.Length;
            ctx.Response.OutputStream.Write(bytes, 0, bytes.Length);
            ctx.Response.Close();
        }

        /// <summary>One import or download running in the background; the PC polls GET /jobs/{id} until it is done.</summary>
        private sealed class Job
        {
            private readonly StringBuilder _log = new StringBuilder();
            private int _exitCode;
            private string _report;
            public readonly string Id = Guid.NewGuid().ToString("N");
            public readonly DateTime Started = DateTime.UtcNow;
            public volatile bool Done;

            public void Append(string text) { lock (_log) _log.Append(text); }

            public void Finish(int exitCode, string report)
            {
                lock (_log) { _exitCode = exitCode; _report = report; }
                Done = true;
            }

            public Dictionary<string, object> Answer()
            {
                lock (_log)
                {
                    var a = new Dictionary<string, object> { ["job"] = Id, ["done"] = Done, ["log"] = _log.ToString() };
                    if (Done)
                    {
                        a["exitCode"] = _exitCode;
                        if (_report != null) a["report"] = JsonDocument.Parse(_report).RootElement.Clone();
                    }
                    return a;
                }
            }
        }

        /// <summary>Writes to the console and to the job's log at the same time.</summary>
        private sealed class Tee : TextWriter
        {
            private readonly TextWriter _console;
            private readonly Job _job;
            public Tee(TextWriter console, Job job) { _console = console; _job = job; }
            public override Encoding Encoding => Encoding.UTF8;
            public override void Write(char value) { _console.Write(value); _job.Append(value.ToString()); }
            public override void Write(string value) { _console.Write(value); _job.Append(value); }
            public override void WriteLine(string value) { _console.WriteLine(value); _job.Append(value + Environment.NewLine); }
        }
    }
}
