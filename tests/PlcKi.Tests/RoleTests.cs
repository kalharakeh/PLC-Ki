using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using PlcKi.Plant;
using Xunit;

/// <summary>
/// The three roles (admin, technician, operator) are enforced by the server, not only hidden in the page.
/// The route walk starts the real PLC-Ki server in memory (snapshot values, no PLC, no TIA helper) and calls every
/// /api/admin endpoint it maps: a technician gets through only on the short list below, an operator never.
/// So a new /api/admin endpoint is closed to both until someone adds it to Roles on purpose.
/// </summary>
public class RoleTests : IDisposable
{
    private static readonly string Pw = Guid.NewGuid().ToString("N")[..12];
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "plcki-roles-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* ignore */ }
    }

    /// <summary>What a technician may call under /api/admin (method and route), written out here on purpose, apart from Roles.</summary>
    private static readonly HashSet<string> TechnicianMay = new(StringComparer.OrdinalIgnoreCase)
    {
        "GET /api/admin/write/check", "GET /api/admin/write/state", "GET /api/admin/write/log", "POST /api/admin/write",
        "GET /api/admin/hold", "GET /api/admin/hold/check", "POST /api/admin/hold", "POST /api/admin/hold/release",
        "PUT /api/admin/documents/upload/{name}", "PATCH /api/admin/documents/{id}",
    };

    [Fact]
    public void Each_role_may_do_its_own_list()
    {
        Assert.True(Roles.Can("admin", Permission.Settings));
        Assert.True(Roles.Can("admin", Permission.RealValues));
        Assert.True(Roles.Can("technician", Permission.PlcsimValues));
        Assert.True(Roles.Can("technician", Permission.Documents));
        Assert.True(Roles.Can("technician", Permission.Notes));
        Assert.False(Roles.Can("technician", Permission.Settings));
        Assert.False(Roles.Can("technician", Permission.RealValues));
        Assert.True(Roles.Can("operator", Permission.Notes));
        Assert.False(Roles.Can("operator", Permission.PlcsimValues));
        Assert.False(Roles.Can("operator", Permission.Documents));
        Assert.False(Roles.Can(null, Permission.Notes));
        Assert.False(Roles.Can("ADMIN", Permission.Settings));     // roles are stored lower case; anything else is nobody
    }

    [Theory]
    [InlineData("admin", "plcsim", true)]
    [InlineData("admin", "real", true)]
    [InlineData("technician", "plcsim", true)]
    [InlineData("technician", "real", false)]
    [InlineData("technician", "none", false)]      // PLCSIM not confirmed: refused, never "probably fine"
    [InlineData("technician", null, false)]
    [InlineData("operator", "plcsim", false)]
    [InlineData("operator", "real", false)]
    [InlineData(null, "plcsim", false)]
    public void Values_change_on_plcsim_for_technicians_and_on_a_real_plc_for_admins_only(string? role, string? target, bool allowed)
    {
        var why = Roles.ValueChangeRefused(role, target);
        Assert.Equal(allowed, why == null);
        if (role == "technician" && target == "real") Assert.Contains("PLCSIM only", why);
    }

    [Fact]
    public void A_technician_may_not_delete_or_rescan_documents()
    {
        Assert.True(Roles.MayCallAdminPath("technician", "PUT", "/api/admin/documents/upload/manual.pdf"));
        Assert.True(Roles.MayCallAdminPath("technician", "PATCH", "/api/admin/documents/abc123"));
        Assert.False(Roles.MayCallAdminPath("technician", "DELETE", "/api/admin/documents/abc123"));
        Assert.False(Roles.MayCallAdminPath("technician", "POST", "/api/admin/documents/scan"));
        Assert.False(Roles.MayCallAdminPath("technician", "PATCH", "/api/admin/documents/scan"));
        Assert.False(Roles.MayCallAdminPath("technician", "PUT", "/api/admin/documents/upload/a/b"));
        Assert.False(Roles.MayCallAdminPath("technician", "POST", "/api/admin/cpu/mode"));     // PLCSIM Start/Stop: admins
        Assert.False(Roles.MayCallAdminPath("technician", "PUT", "/api/admin/settings"));
        Assert.False(Roles.MayCallAdminPath("operator", "POST", "/api/admin/write"));
        Assert.True(Roles.MayCallAdminPath("admin", "DELETE", "/api/admin/users/x"));
    }

    [Fact]
    public void Users_can_be_technicians()
    {
        Directory.CreateDirectory(_dir);
        var users = new UserStore(Path.Combine(_dir, "users.json"));
        users.Add("anna", Pw, "admin");
        users.Add("tom", Pw, "Technician");
        Assert.Equal("technician", users.Find("tom")!.Role);
        var stamp = users.Find("tom")!.Stamp;
        users.SetRole("tom", "operator", "anna");
        Assert.NotEqual(stamp, users.Find("tom")!.Stamp);          // a role change ends the other sessions
        Assert.Throws<PlantException>(() => users.Add("x", Pw, "shiftlead"));
    }

    [Fact]
    public async Task Every_admin_route_is_closed_to_operators_and_to_technicians_except_their_list()
    {
        Directory.CreateDirectory(_dir);
        var users = new UserStore(Path.Combine(_dir, "users.json"));
        users.Add("anna", Pw, "admin");
        users.Add("tom", Pw, "technician");
        users.Add("olga", Pw, "operator");

        await using var server = await RealServer.StartAsync(_dir);
        var tech = await server.LoginAsync("tom", Pw);
        var op = await server.LoginAsync("olga", Pw);

        var routes = server.AdminRoutes();
        Assert.True(routes.Count > 40, $"only {routes.Count} admin routes found");
        foreach (var r in TechnicianMay) Assert.Contains(r, routes);     // the list matches real routes

        var wrong = new List<string>();
        foreach (var route in routes)
        {
            var (method, template) = Split(route);
            var url = System.Text.RegularExpressions.Regex.Replace(template, @"\{[^}]+\}", "x-test");
            var asOp = await op.Send(new HttpMethod(method), url);
            if (asOp.StatusCode != HttpStatusCode.Forbidden) wrong.Add($"operator {route}: {(int)asOp.StatusCode}");
            var asTech = await tech.Send(new HttpMethod(method), url);
            bool through = asTech.StatusCode is not (HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized);
            if (through != TechnicianMay.Contains(route)) wrong.Add($"technician {route}: {(int)asTech.StatusCode}");
        }
        Assert.True(wrong.Count == 0, string.Join("\n", wrong));

        // Settings stay closed with a plain reason
        var settings = await tech.Send(HttpMethod.Put, "/api/admin/settings");
        Assert.Contains("Only an administrator", await settings.Content.ReadAsStringAsync());

        // No PLC here (snapshot values): a technician may change nothing, and Start/Stop is never offered
        var state = JsonDocument.Parse(await (await tech.Send(HttpMethod.Get, "/api/admin/write/state")).Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("none", state.GetProperty("values").GetString());
        Assert.False(state.GetProperty("startStop").GetBoolean());
        var write = await tech.Send(HttpMethod.Post, "/api/admin/write", new { symbol = "\"DB_IO\".XV101_ZSO", value = "true" });
        Assert.Equal(HttpStatusCode.Conflict, write.StatusCode);

        // An operator still writes alarm notes (the alarm id does not exist here: 404, not 403)
        var note = await op.Send(HttpMethod.Post, "/api/memory/occurrences/x-test/note", new { text = "checked" });
        Assert.Equal(HttpStatusCode.NotFound, note.StatusCode);
    }

    private static (string Method, string Template) Split(string route)
    {
        var i = route.IndexOf(' ');
        return (route[..i], route[(i + 1)..]);
    }

    /// <summary>A logged-in caller with its own cookie.</summary>
    private sealed class Client
    {
        private readonly HttpClient _http;
        private string? _cookie;
        public Client(HttpClient http) => _http = http;

        public async Task<HttpResponseMessage> Send(HttpMethod method, string url, object? body = null)
        {
            var req = new HttpRequestMessage(method, url);
            if (body != null || method != HttpMethod.Get) req.Content = JsonContent.Create(body ?? new { });
            if (_cookie != null) req.Headers.Add("Cookie", _cookie);
            var r = await _http.SendAsync(req);
            if (r.Headers.TryGetValues("Set-Cookie", out var set)) _cookie = set.First().Split(';')[0];
            return r;
        }
    }

    /// <summary>
    /// The real server (Program.cs) on an in-memory test server: its entry point runs as at a plant, and the
    /// hosting events swap the web server for a TestServer (as WebApplicationFactory does).
    /// </summary>
    private sealed class RealServer : IAsyncDisposable
    {
        private static readonly AsyncLocal<bool> Ours = new();
        private readonly IHost _host;
        private readonly Task _run;
        private RealServer(IHost host, Task run) { _host = host; _run = run; }

        public static async Task<RealServer> StartAsync(string dataDir)
        {
            var built = new TaskCompletionSource<IHost>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var sub = DiagnosticListener.AllListeners.Subscribe(new Observer<DiagnosticListener>(l =>
            {
                if (l.Name != "Microsoft.Extensions.Hosting") return;
                l.Subscribe(new Observer<KeyValuePair<string, object?>>(kv =>
                {
                    if (!Ours.Value) return;     // other tests build their own apps at the same time
                    if (kv.Key == "HostBuilding" && kv.Value is IHostBuilder b)
                        b.ConfigureServices(s => s.AddSingleton<IServer, TestServer>());
                    else if (kv.Key == "HostBuilt" && kv.Value is IHost h)
                        built.TrySetResult(h);
                }));
            }));

            var entry = typeof(PlcKi.Server.Live.WriteEndpoints).Assembly.EntryPoint!;
            var args = new[]
            {
                "--Plant:DataDir=" + dataDir, "--PlcKi:Source=snapshot", "--PlcKi:SnapshotFile=models/snapshots/step30-estop-and-drive.json",
                "--PlcKi:Model=models/demo-mixing-station.json", "--PlcKi:Plc:Address=", "--PlcKi:Tia:Url=", "--PlcKi:Tia:Mode=remote",
                "--urls=http://127.0.0.1:0", "--Logging:LogLevel:Default=Warning",
            };
            var run = Task.Run(async () =>
            {
                Ours.Value = true;
                var result = entry.Invoke(null, new object[] { args });
                if (result is Task t) await t;
            });
            var host = await built.Task.WaitAsync(TimeSpan.FromSeconds(60));
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var life = host.Services.GetRequiredService<IHostApplicationLifetime>();
            life.ApplicationStarted.Register(() => started.TrySetResult());
            if (life.ApplicationStarted.IsCancellationRequested) started.TrySetResult();
            await Task.WhenAny(started.Task, run).WaitAsync(TimeSpan.FromSeconds(60));
            if (run.IsFaulted) await run;      // show why it did not start
            return new RealServer(host, run);
        }

        public async Task<Client> LoginAsync(string name, string password)
        {
            var c = new Client(((TestServer)_host.Services.GetRequiredService<IServer>()).CreateClient());
            var r = await c.Send(HttpMethod.Post, "/api/auth/login", new { name, password });
            Assert.True(r.IsSuccessStatusCode, $"login {name}: {(int)r.StatusCode}");
            return c;
        }

        /// <summary>"METHOD /api/admin/..." for every endpoint the server maps under /api/admin.</summary>
        public HashSet<string> AdminRoutes()
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var e in _host.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>())
            {
                var t = "/" + (e.RoutePattern.RawText ?? "").TrimStart('/');
                if (!t.StartsWith("/api/admin", StringComparison.OrdinalIgnoreCase)) continue;
                foreach (var m in e.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods ?? new[] { "GET" })
                    set.Add($"{m} {t.TrimEnd('/')}");
            }
            return set;
        }

        public async ValueTask DisposeAsync()
        {
            _host.Services.GetRequiredService<IHostApplicationLifetime>().StopApplication();
            try { await _run.WaitAsync(TimeSpan.FromSeconds(30)); } catch { /* stopped */ }
        }
    }

    private sealed class Observer<T>(Action<T> next) : IObserver<T>
    {
        public void OnCompleted() { }
        public void OnError(Exception error) { }
        public void OnNext(T value) => next(value);
    }
}
