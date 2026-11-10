using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using PlcKi.Plant;
using Xunit;

/// <summary>Login, users and the settings store of the plant PC features, in memory with a temporary data folder.</summary>
public class PlantTests : IDisposable
{
    // Made up at run time, so no password-like text sits in the source
    private static readonly string AdminPw = NewSecret(), OperatorPw = NewSecret(), NewPw = NewSecret(), PlcPw = NewSecret();
    private static string NewSecret() => Guid.NewGuid().ToString("N")[..12];

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "plcki-test-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* ignore */ }
    }

    [Fact]
    public void Users_hash_passwords_and_keep_one_admin()
    {
        var file = Path.Combine(_dir, "users.json");
        Directory.CreateDirectory(_dir);
        var users = new UserStore(file);
        Assert.Throws<PlantException>(() => users.Add("anna", "short", UserStore.Admin));
        users.Add("anna", AdminPw, UserStore.Admin);
        users.Add("ben", OperatorPw, UserStore.Operator);

        Assert.DoesNotContain(AdminPw, File.ReadAllText(file));
        Assert.NotNull(users.Verify("ANNA", AdminPw));
        Assert.Null(users.Verify("anna", "wrong password"));
        Assert.Null(users.Verify("nobody", AdminPw));

        Assert.Throws<PlantException>(() => users.Delete("anna", "ben"));            // last admin
        Assert.Throws<PlantException>(() => users.SetRole("anna", "operator", "ben"));
        Assert.Throws<PlantException>(() => users.Delete("anna", "anna"));           // yourself

        var stamp = users.Find("ben")!.Stamp;
        users.SetPassword("ben", NewPw);
        Assert.NotEqual(stamp, users.Find("ben")!.Stamp);                           // other sessions end

        var reloaded = new UserStore(file);
        Assert.NotNull(reloaded.Verify("ben", NewPw));
        reloaded.Delete("ben", "anna");
        Assert.Single(reloaded.List());
    }

    [Fact]
    public void Five_wrong_passwords_lock_the_name_for_a_minute()
    {
        var now = DateTimeOffset.UtcNow;
        var t = new LoginThrottle(() => now);
        for (int i = 0; i < 4; i++) t.Failed("anna");
        Assert.Null(t.Blocked("anna"));
        t.Failed("anna");
        Assert.NotNull(t.Blocked("anna"));
        Assert.Null(t.Blocked("ben"));
        now = now.AddMinutes(1.1);
        Assert.Null(t.Blocked("anna"));
    }

    private sealed class Client
    {
        private readonly HttpClient _http;
        private string? _cookie;
        public Client(HttpClient http) => _http = http;

        public async Task<HttpResponseMessage> Send(HttpMethod method, string url, object? body = null)
        {
            var req = new HttpRequestMessage(method, url);
            if (body != null) req.Content = JsonContent.Create(body);
            if (_cookie != null) req.Headers.Add("Cookie", _cookie);
            var r = await _http.SendAsync(req);
            if (r.Headers.TryGetValues("Set-Cookie", out var set))
                _cookie = set.First().Split(';')[0];
            return r;
        }
    }

    private async Task<WebApplication> Start(bool local = true, bool publicView = false)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Test" });
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Plant:DataDir"] = _dir,
            ["PlcKi:Model"] = "models/demo-mixing-station.json",
            ["Plant:PublicView"] = publicView ? "true" : "false",
        });
        builder.WebHost.UseTestServer();
        builder.AddPlant();
        var app = builder.Build();
        app.Use((ctx, next) =>
        {
            ctx.Connection.RemoteIpAddress = local ? IPAddress.Loopback : IPAddress.Parse("10.0.0.5");
            return next(ctx);
        });
        app.UsePlant();
        app.MapGet("/api/status", () => Results.Ok(new { ok = true }));
        app.MapGet("/api/ask", () => Results.Ok(new { enabled = true }));
        app.MapPost("/api/ask", () => Results.Ok(new { answer = "spent credit" }));
        app.MapDelete("/api/ask/{id}", (string id) => Results.NoContent());
        app.MapPost("/api/watch", () => Results.Ok(new { values = new { } }));
        app.MapPut("/api/reports/handover", () => Results.Ok(new { saved = true }));
        app.MapPost("/api/memory/occurrences/{id}/note", (string id) => Results.Ok(new { id }));
        app.MapPlant();
        await app.StartAsync();
        return app;
    }

    [Fact]
    public async Task First_admin_only_from_this_pc_then_login_guards_everything()
    {
        await using (var remote = await Start(local: false))
        {
            var c = new Client(remote.GetTestClient());
            Assert.Equal(HttpStatusCode.Unauthorized, (await c.Send(HttpMethod.Get, "/api/status")).StatusCode);
            var setup = await c.Send(HttpMethod.Post, "/api/auth/setup", new { name = "anna", password = AdminPw });
            Assert.Equal(HttpStatusCode.Forbidden, setup.StatusCode);
        }

        await using var app = await Start();
        var admin = new Client(app.GetTestClient());
        var page = await admin.Send(HttpMethod.Get, "/");
        Assert.Equal(HttpStatusCode.Redirect, page.StatusCode);
        Assert.Equal("/login.html", page.Headers.Location!.OriginalString);

        Assert.True((await admin.Send(HttpMethod.Post, "/api/auth/setup", new { name = "anna", password = AdminPw })).IsSuccessStatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await admin.Send(HttpMethod.Post, "/api/auth/setup", new { name = "eve", password = AdminPw })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.Send(HttpMethod.Get, "/api/status")).StatusCode);
        Assert.True((await admin.Send(HttpMethod.Post, "/api/admin/users", new { name = "ben", password = OperatorPw, role = "operator" })).IsSuccessStatusCode);

        var op = new Client(app.GetTestClient());
        Assert.Equal(HttpStatusCode.Unauthorized, (await op.Send(HttpMethod.Post, "/api/auth/login", new { name = "ben", password = "nope nope" })).StatusCode);
        Assert.True((await op.Send(HttpMethod.Post, "/api/auth/login", new { name = "ben", password = OperatorPw })).IsSuccessStatusCode);
        Assert.Equal(HttpStatusCode.OK, (await op.Send(HttpMethod.Get, "/api/status")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await op.Send(HttpMethod.Get, "/api/admin/settings")).StatusCode);

        // Deleting the user ends the session at once
        Assert.Equal(HttpStatusCode.NoContent, (await admin.Send(HttpMethod.Delete, "/api/admin/users/ben")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await op.Send(HttpMethod.Get, "/api/status")).StatusCode);
    }

    [Fact]
    public async Task Public_view_shows_the_machine_but_asking_needs_a_login()
    {
        await using var app = await Start(publicView: true);
        var admin = new Client(app.GetTestClient());
        Assert.True((await admin.Send(HttpMethod.Post, "/api/auth/setup", new { name = "anna", password = AdminPw })).IsSuccessStatusCode);

        var anon = new Client(app.GetTestClient());
        Assert.Equal(HttpStatusCode.OK, (await anon.Send(HttpMethod.Get, "/api/status")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await anon.Send(HttpMethod.Get, "/api/ask")).StatusCode);          // is Ask on: hides the button
        Assert.Equal(HttpStatusCode.OK, (await anon.Send(HttpMethod.Post, "/api/watch", new { symbols = Array.Empty<object>() })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.Send(HttpMethod.Post, "/api/ask", new { question = "why?" })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.Send(HttpMethod.Post, "/API/Ask", new { question = "why?" })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.Send(HttpMethod.Delete, "/api/ask/abc")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.Send(HttpMethod.Get, "/api/admin/settings")).StatusCode);
        // Public view is read-only: notes and the handover note need a login too
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.Send(HttpMethod.Put, "/api/reports/handover", new { text = "x" })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.Send(HttpMethod.Post, "/api/memory/occurrences/a1/note", new { text = "x" })).StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await admin.Send(HttpMethod.Post, "/api/ask", new { question = "why?" })).StatusCode);
    }

    [Fact]
    public async Task Settings_are_saved_with_the_plc_password_encrypted_and_used_at_next_start()
    {
        await using (var app = await Start())
        {
            var c = new Client(app.GetTestClient());
            await c.Send(HttpMethod.Post, "/api/auth/setup", new { name = "anna", password = AdminPw });
            var current = app.Services.GetRequiredService<SettingsStore>().Current();
            current.Address = "10.20.30.40";
            current.PlcPassword = PlcPw;
            current.Port = 5099;
            var r = await c.Send(HttpMethod.Put, "/api/admin/settings", current);
            Assert.True(r.IsSuccessStatusCode, await r.Content.ReadAsStringAsync());

            current.Address = "not an address!";
            Assert.Equal(HttpStatusCode.BadRequest, (await c.Send(HttpMethod.Put, "/api/admin/settings", current)).StatusCode);
        }

        var saved = File.ReadAllText(Path.Combine(_dir, "settings.json"));
        Assert.Contains("10.20.30.40", saved);
        Assert.Contains("http://localhost:5099", saved);
        Assert.DoesNotContain(PlcPw, saved);

        await using var again = await Start();
        var config = again.Services.GetRequiredService<IConfiguration>();
        Assert.Equal("10.20.30.40", config["PlcKi:Plc:Address"]);
        Assert.Equal(PlcPw, config["PlcKi:Plc:Password"]);
    }

    [Fact]
    public async Task Ask_limit_works_at_once_and_the_model_after_a_restart()
    {
        await using var app = await Start();
        var c = new Client(app.GetTestClient());
        await c.Send(HttpMethod.Post, "/api/auth/setup", new { name = "anna", password = AdminPw });
        var store = app.Services.GetRequiredService<SettingsStore>();
        var config = app.Services.GetRequiredService<IConfiguration>();

        var s = store.Current();
        Assert.Equal("claude-opus-5-5", s.AskModel);
        s.AskMonthlyLimitUsd = 25;
        s.Username = "plcki-reader";
        var r = await c.Send(HttpMethod.Put, "/api/admin/settings", s);
        Assert.True(r.IsSuccessStatusCode, await r.Content.ReadAsStringAsync());
        Assert.Equal(25.0, config.GetValue("PlcKi:Ask:MonthlyLimitUsd", 0.0));
        Assert.Equal("plcki-reader", config["PlcKi:Plc:Username"]);

        s = store.Current();
        s.AskModel = "claude-sonnet-5-5";
        s.AskMaxTokens = 16000;
        s.AskMaxToolRounds = 20;
        var saved = System.Text.Json.JsonDocument.Parse(await (await c.Send(HttpMethod.Put, "/api/admin/settings", s)).Content.ReadAsStringAsync()).RootElement;
        Assert.True(saved.GetProperty("info").GetProperty("restartNeeded").GetBoolean());
        Assert.Equal("claude-sonnet-5-5", saved.GetProperty("settings").GetProperty("askModel").GetString());
        Assert.Equal(5090, saved.GetProperty("info").GetProperty("tiaPort").GetInt32());

        foreach (var bad in new Action<PlantSettings>[] { x => x.AskMaxTokens = 99_999, x => x.AskMaxToolRounds = 1, x => x.AskModel = "gpt-5", x => x.AskMonthlyLimitUsd = -1 })
        {
            var b = store.Current();
            bad(b);
            Assert.Equal(HttpStatusCode.BadRequest, (await c.Send(HttpMethod.Put, "/api/admin/settings", b)).StatusCode);
        }

        // A value set by hand in the config file, outside the page's ranges, does not block saving other settings
        File.WriteAllText(Path.Combine(_dir, "settings.json"), File.ReadAllText(Path.Combine(_dir, "settings.json")).Replace("\"MaxTokens\": 16000", "\"MaxTokens\": 32000"));
        (config as IConfigurationRoot)!.Reload();
        Assert.Equal(32000, store.Current().AskMaxTokens);
        var other = store.Current();
        other.HistoryMinutes = 30;
        var ok = await c.Send(HttpMethod.Put, "/api/admin/settings", other);
        Assert.True(ok.IsSuccessStatusCode, await ok.Content.ReadAsStringAsync());
    }

    [Fact]
    public void Preferences_are_kept_per_login_and_only_known_small_ones()
    {
        Directory.CreateDirectory(_dir);
        var file = Path.Combine(_dir, "prefs.json");
        var prefs = new PreferenceStore(file);
        prefs.Set("Anna", new System.Text.Json.Nodes.JsonObject { ["theme"] = "dark", ["trendTags"] = new System.Text.Json.Nodes.JsonArray("LT101", "TT201") });
        prefs.Set("ben", new System.Text.Json.Nodes.JsonObject { ["theme"] = "light" });
        Assert.Equal("dark", (string?)prefs.Get("anna")["theme"]);           // names are not case sensitive
        Assert.Equal("light", (string?)new PreferenceStore(file).Get("BEN")["theme"]);
        Assert.Empty(prefs.Get("carl"));

        prefs.Set("anna", new System.Text.Json.Nodes.JsonObject { ["theme"] = null, ["startPage"] = "#/reports" });
        var anna = prefs.Get("anna");
        Assert.False(anna.ContainsKey("theme"));                              // null removes a choice
        Assert.Equal(2, anna.Count);                                          // the others stay

        Assert.Throws<PlantException>(() => prefs.Set("anna", new System.Text.Json.Nodes.JsonObject { ["password"] = "x" }));
        Assert.Throws<PlantException>(() => prefs.Set("anna", new System.Text.Json.Nodes.JsonObject { ["trendTags"] = new string('x', 9000) }));

        prefs.Remove("anna");
        Assert.Empty(new PreferenceStore(file).Get("anna"));
        Assert.Equal("light", (string?)prefs.Get("ben")["theme"]);

        File.WriteAllText(file, "{ broken");
        Assert.Empty(new PreferenceStore(file).Get("ben"));                  // a broken file does not stop PLC-Ki
    }

    [Fact]
    public async Task Each_login_gets_its_own_choices_and_start_page()
    {
        await using var app = await Start(publicView: true);
        var admin = new Client(app.GetTestClient());
        Assert.True((await admin.Send(HttpMethod.Post, "/api/auth/setup", new { name = "anna", password = AdminPw })).IsSuccessStatusCode);
        await admin.Send(HttpMethod.Post, "/api/admin/users", new { name = "ben", password = OperatorPw, role = "operator" });
        var ben = new Client(app.GetTestClient());
        await ben.Send(HttpMethod.Post, "/api/auth/login", new { name = "ben", password = OperatorPw });

        Assert.Equal(HttpStatusCode.OK, (await admin.Send(HttpMethod.Put, "/api/me/prefs", new { theme = "dark" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await ben.Send(HttpMethod.Put, "/api/me/prefs", new { theme = "light", startPage = "#/reports" })).StatusCode);
        Assert.Contains("\"dark\"", await (await admin.Send(HttpMethod.Get, "/api/me/prefs")).Content.ReadAsStringAsync());
        Assert.Contains("\"light\"", await (await ben.Send(HttpMethod.Get, "/api/me/prefs")).Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.BadRequest, (await ben.Send(HttpMethod.Put, "/api/me/prefs", new { role = "admin" })).StatusCode);

        // Logged out (public view): no choices on the server, the page keeps them in the browser
        var anon = new Client(app.GetTestClient());
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.Send(HttpMethod.Get, "/api/me/prefs")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.Send(HttpMethod.Put, "/api/me/prefs", new { theme = "dark" })).StatusCode);

        // Start page per role: administrators set it, it comes with the login state
        Assert.Equal(HttpStatusCode.Forbidden, (await ben.Send(HttpMethod.Put, "/api/admin/start-pages", new Dictionary<string, string> { ["operator"] = "" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.Send(HttpMethod.Put, "/api/admin/start-pages", new Dictionary<string, string> { ["operator"] = "#/station/Mixer", ["technician"] = "#/alarm" })).StatusCode);
        Assert.Contains("#/station/Mixer", await (await ben.Send(HttpMethod.Get, "/api/auth/state")).Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.Send(HttpMethod.Put, "/api/admin/start-pages", new Dictionary<string, string> { ["operator"] = "https://evil.example" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.Send(HttpMethod.Put, "/api/admin/start-pages", new Dictionary<string, string> { ["shiftlead"] = "#/reports" })).StatusCode);

        // A deleted login takes its choices along: a new "ben" starts fresh
        await admin.Send(HttpMethod.Delete, "/api/admin/users/ben");
        await admin.Send(HttpMethod.Post, "/api/admin/users", new { name = "ben", password = OperatorPw, role = "operator" });
        var newBen = new Client(app.GetTestClient());
        await newBen.Send(HttpMethod.Post, "/api/auth/login", new { name = "ben", password = OperatorPw });
        Assert.Equal("{}", (await (await newBen.Send(HttpMethod.Get, "/api/me/prefs")).Content.ReadAsStringAsync()).Trim());
    }
}

public class PlcAddressTests
{
    [Theory]
    [InlineData("192.168.0.10", true)]
    [InlineData("", true)]                  // not set yet is allowed
    [InlineData("plc-line1", true)]
    [InlineData("999.1.1", false)]          // digits and dots, but not an IP address
    [InlineData("192.168.0.256", false)]
    [InlineData("not an address!", false)]
    public void Address_is_checked_before_it_is_used(string address, bool ok)
    {
        var problem = SettingsStore.AddressProblem(address);
        Assert.Equal(ok, problem == null);
        if (!ok) Assert.Contains("for example 192.168.0.10", problem);
    }
}
