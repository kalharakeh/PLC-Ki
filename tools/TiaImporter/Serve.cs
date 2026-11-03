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
    /// PLCSIM download loop without copying folders by hand. One request at a time; every request needs the token.
    ///
    ///   GET  /status                 alive check, settings
    ///   PUT  /packages/{name}        body: zip of a package folder (generated.json + v001, v002, ...)
    ///   POST /import                 {"package","project","plc","version","replace","keepOnError"} -> log + import-report
    ///   POST /download               {"project","plc"} -> download to PLCSIM only (see PlcsimDownload)
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

        public Serve(int port, string token, string plcsimIp, DirectoryInfo packages, DirectoryInfo projects)
        {
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
            listener.Prefixes.Add($"http://+:{_port}/");
            try { listener.Start(); }
            catch (HttpListenerException ex)
            {
                throw new InvalidOperationException($"Cannot listen on port {_port}: {ex.Message}. Run once as administrator:\n" +
                    $"  netsh http add urlacl url=http://+:{_port}/ user=%USERNAME%\n" +
                    $"  netsh advfirewall firewall add rule name=\"PLC-Ki TiaImporter\" dir=in action=allow protocol=TCP localport={_port}");
            }
            Console.WriteLine($"TiaImporter serve on port {_port}. Packages in {_packages.FullName}, projects under {_projects.FullName}.");
            Console.WriteLine(_plcsimIp != null ? $"Downloads allowed only to PLCSIM at {_plcsimIp}." : "Downloads are off (no --plcsim-ip).");
            Console.WriteLine("Press Ctrl+C to stop.");
            while (true)
            {
                var ctx = listener.GetContext();   // one request at a time: TIA runs once at a time anyway
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
                    ["projects"] = _projects.FullName, ["plcsimIp"] = _plcsimIp,
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
            if (req.HttpMethod == "POST" && (path == "/import" || path == "/download"))
            {
                JsonElement body;
                using (var r = new StreamReader(req.InputStream, Encoding.UTF8)) body = JsonDocument.Parse(r.ReadToEnd()).RootElement.Clone();
                var project = Project(Str(body, "project"));
                var plc = Str(body, "plc");
                var log = new StringWriter();
                var tee = new Tee(Console.Out, log);
                int code;
                string report = null;
                try
                {
                    if (path == "/import")
                    {
                        var name = Str(body, "package");
                        if (name == null || !PackageName.IsMatch(name)) throw new InvalidOperationException("Name the package.");
                        var dir = new DirectoryInfo(Path.Combine(_packages.FullName, name));
                        code = new BlockImport(tee).Run(project, dir, plc, Str(body, "version"), Bool(body, "replace"), Bool(body, "keepOnError"));
                        var reportFile = dir.GetFiles("import-report.json", SearchOption.AllDirectories).OrderByDescending(f => f.LastWriteTimeUtc).FirstOrDefault();
                        if (reportFile != null && reportFile.LastWriteTimeUtc > DateTime.UtcNow.AddMinutes(-30)) report = File.ReadAllText(reportFile.FullName);
                    }
                    else
                    {
                        if (_plcsimIp == null) throw new InvalidOperationException("Downloads are off: start serve with --plcsim-ip <PLCSIM address>.");
                        code = new PlcsimDownload(tee).Run(project, plc, _plcsimIp);
                    }
                }
                catch (Exception ex)
                {
                    tee.WriteLine("ERROR: " + ex.Message);
                    code = 1;
                }
                var answer = new Dictionary<string, object> { ["exitCode"] = code, ["log"] = log.ToString() };
                if (report != null) answer["report"] = JsonDocument.Parse(report).RootElement.Clone();
                Reply(ctx, 200, answer);
                return;
            }
            Reply(ctx, 404, new Dictionary<string, object> { ["error"] = $"Unknown request {req.HttpMethod} {path}" });
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

        /// <summary>Writes to the console and to the reply log at the same time.</summary>
        private sealed class Tee : TextWriter
        {
            private readonly TextWriter _a, _b;
            public Tee(TextWriter a, TextWriter b) { _a = a; _b = b; }
            public override Encoding Encoding => Encoding.UTF8;
            public override void Write(char value) { _a.Write(value); _b.Write(value); }
            public override void Write(string value) { _a.Write(value); _b.Write(value); }
            public override void WriteLine(string value) { _a.WriteLine(value); _b.WriteLine(value); }
        }
    }
}
