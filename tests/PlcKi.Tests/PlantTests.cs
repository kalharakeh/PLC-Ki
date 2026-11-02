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

    private async Task<WebApplication> Start(bool local = true)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Test" });
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Plant:DataDir"] = _dir,
            ["PlcKi:Model"] = "models/demo-mixing-station.json",
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
}
