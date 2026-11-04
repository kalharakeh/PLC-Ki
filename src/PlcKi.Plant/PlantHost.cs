using System.Net;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.Json;
using Microsoft.Extensions.Configuration.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace PlcKi.Plant;

/// <summary>
/// Hooks the plant PC features into the web app:
///   builder.AddPlant()  - Windows service support, settings.json, secrets, login
///   app.UsePlant()      - login check in front of every page and API (call right after Build)
///   app.MapPlant()      - /api/auth/... and /api/admin/... endpoints
/// </summary>
public static class PlantHost
{
    public const string RoleClaim = ClaimTypes.Role;
    public const string StampClaim = "plcki:stamp";

    public static PlantContext AddPlant(this WebApplicationBuilder builder)
    {
        bool isService = PlantContext.RunningAsService();
        var dataDir = builder.Configuration["Plant:DataDir"] ?? Environment.GetEnvironmentVariable("PLCKI_DATADIR");
        var ctx = new PlantContext(string.IsNullOrWhiteSpace(dataDir) ? PlantContext.DefaultDataDir(isService) : dataDir, isService)
        {
            ApiKeyInEnvironment = !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY")),
            PlcPasswordInEnvironment = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("PLCKI_PLC_PASSWORD")),
        };

        // settings.json goes right after appsettings*.json: above the defaults, below environment and command line
        var sources = builder.Configuration.Sources;
        int at = sources.Select((s, i) => (s, i)).LastOrDefault(x => x.s is JsonConfigurationSource).i + 1;
        sources.Insert(at, new JsonConfigurationSource
        {
            Path = Path.GetFileName(ctx.SettingsFile),
            FileProvider = new PhysicalFileProvider(ctx.DataDir),
            Optional = true,
            ReloadOnChange = true,
        });

        // Secrets saved on the settings page are decrypted once at start
        var protector = SettingsStore.Protector(DataProtectionProvider.Create(new DirectoryInfo(ctx.KeysDir), ConfigureKeys));
        var secrets = new Dictionary<string, string?>();
        string? Secret(string name)
        {
            var enc = builder.Configuration[$"{SettingsStore.SecretsSection}:{name}"];
            if (string.IsNullOrEmpty(enc)) return null;
            try { return protector.Unprotect(enc); }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"PLC-Ki: the saved {name} cannot be decrypted ({ex.Message}). Enter it again on the settings page.");
                return null;
            }
        }
        if (Secret("PlcPassword") is { } plcPassword)
        {
            secrets["PlcKi:Plc:Password"] = plcPassword;
            ctx.PlcPasswordInSettings = true;
        }
        if (Secret("TiaToken") is { } tiaToken)
        {
            secrets["PlcKi:Tia:Token"] = tiaToken;
            ctx.TiaTokenInSettings = true;
        }
        if (secrets.Count > 0) sources.Insert(at + 1, new MemoryConfigurationSource { InitialData = secrets });
        if (Secret("AnthropicApiKey") is { } apiKey)
        {
            // The assistant reads its key from this variable; a key saved on the settings page wins
            Environment.SetEnvironmentVariable("ANTHROPIC_API_KEY", apiKey);
            ctx.ApiKeyInSettings = true;
        }

        var services = builder.Services;
        services.AddWindowsService(o => o.ServiceName = PlantContext.ServiceName);
        services.AddSingleton(ctx);
        services.AddSingleton(new UserStore(ctx.UsersFile));
        services.AddSingleton(new LoginThrottle());
        services.AddSingleton<SettingsStore>();
        ConfigureKeys(services.AddDataProtection().PersistKeysToFileSystem(new DirectoryInfo(ctx.KeysDir)));
        services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme).AddCookie(o =>
        {
            o.Cookie.Name = "plcki_login";
            o.Cookie.HttpOnly = true;
            o.Cookie.SameSite = SameSiteMode.Strict;
            o.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
            o.ExpireTimeSpan = TimeSpan.FromHours(12);    // about one shift
            o.SlidingExpiration = true;
            o.Events.OnRedirectToLogin = c => { c.Response.StatusCode = 401; return Task.CompletedTask; };
            o.Events.OnRedirectToAccessDenied = c => { c.Response.StatusCode = 403; return Task.CompletedTask; };
            // Deleted users, changed roles and changed passwords end the session
            o.Events.OnValidatePrincipal = c =>
            {
                var user = c.HttpContext.RequestServices.GetRequiredService<UserStore>().Find(c.Principal?.Identity?.Name);
                if (user == null || user.Stamp != c.Principal!.FindFirstValue(StampClaim) || user.Role != c.Principal!.FindFirstValue(RoleClaim))
                    c.RejectPrincipal();
                return Task.CompletedTask;
            };
        });
        services.AddAuthorization();
        return ctx;
    }

    private static void ConfigureKeys(IDataProtectionBuilder b)
    {
        // Same name and key folder for the start-up decryption and the app, so both read the same keys
        b.SetApplicationName("PLC-Ki");
        if (OperatingSystem.IsWindows()) b.ProtectKeysWithDpapi(protectToLocalMachine: true);
    }

    public static WebApplication UsePlant(this WebApplication app)
    {
        var ctx = app.Services.GetRequiredService<PlantContext>();
        app.UseAuthentication();
        app.UseAuthorization();
        app.Use(Gate);
        var log = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("PlcKi.Plant");
        app.Lifetime.ApplicationStarted.Register(() =>
        {
            log.LogInformation("PLC-Ki data folder: {Dir}. Running as {How}.", ctx.DataDir, ctx.IsService ? "Windows service" : "a program");
            if (!app.Services.GetRequiredService<UserStore>().Any)
                Console.WriteLine("First start: open the page on this PC to create the administrator login.");
        });
        return app;
    }

    /// <summary>Pages and calls anyone may use, logged in or not.</summary>
    private static readonly HashSet<string> Open = new(StringComparer.OrdinalIgnoreCase)
    {
        "/login.html", "/login.js", "/ui.css", "/favicon.ico",
        "/api/auth/state", "/api/auth/login", "/api/auth/setup", "/api/auth/logout",
    };

    private static bool IsAdminPath(PathString p) =>
        p.StartsWithSegments("/api/admin") || p.Equals("/settings.html", StringComparison.OrdinalIgnoreCase)
        || p.Equals("/settings.js", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Public view shows the machine to anyone, but asking a question spends Claude credit, so it needs a login.
    /// Only reading whether Ask is on (GET /api/ask) stays open, so the page can hide the button.
    /// POST /api/watch stays open: it only picks which values the page shows (read-only, capped by WatchList.Max).
    /// </summary>
    private static bool NeedsLoginInPublicView(HttpRequest req) =>
        req.Path.StartsWithSegments("/api/ask") && !(HttpMethods.IsGet(req.Method) && req.Path.Equals("/api/ask"));

    private static bool IsPage(PathString p) => p == "/" || p.Value!.EndsWith(".html", StringComparison.OrdinalIgnoreCase);

    private static async Task Gate(HttpContext http, Func<Task> next)
    {
        var path = http.Request.Path;
        if (Open.Contains(path.Value ?? "")) { await next(); return; }
        // PLC-Ki Link and the TIA relay check their own key or token (Link/LinkEndpoints.cs)
        if (path.StartsWithSegments("/link")) { await next(); return; }

        var users = http.RequestServices.GetRequiredService<UserStore>();
        var user = http.User.Identity?.IsAuthenticated == true ? http.User : null;
        bool admin = user?.IsInRole(UserStore.Admin) == true;

        if (IsAdminPath(path))
        {
            if (admin) { await next(); return; }
            await Deny(http, user == null ? 401 : 403, user == null ? null : "Only an administrator can change the settings.");
            return;
        }
        if (user != null) { await next(); return; }

        var config = http.RequestServices.GetRequiredService<IConfiguration>();
        bool publicView = config.GetValue("Plant:PublicView", false) && users.Any;
        if (publicView && !path.StartsWithSegments("/api/auth") && !NeedsLoginInPublicView(http.Request)) { await next(); return; }

        await Deny(http, 401, null);
    }

    private static async Task Deny(HttpContext http, int status, string? message)
    {
        var path = http.Request.Path;
        if (HttpMethods.IsGet(http.Request.Method) && IsPage(path))
        {
            var back = path == "/" ? "" : "?next=" + Uri.EscapeDataString(path.Value!);
            http.Response.Redirect(status == 401 ? "/login.html" + back : "/");
            return;
        }
        http.Response.StatusCode = status;
        await http.Response.WriteAsJsonAsync(new { error = message ?? "Please log in.", login = status == 401 });
    }

    public static bool IsLocal(HttpContext http) =>
        http.Connection.RemoteIpAddress is { } ip && IPAddress.IsLoopback(ip);

    internal static Task SignIn(HttpContext http, PlantUser user) =>
        http.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.Name, user.Name),
            new Claim(RoleClaim, user.Role),
            new Claim(StampClaim, user.Stamp),
        }, CookieAuthenticationDefaults.AuthenticationScheme)), new AuthenticationProperties { IsPersistent = true });
}
