using System.Net.Sockets;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PlcKi.Core.Model;

namespace PlcKi.Plant;

public sealed record LoginRequest(string? Name, string? Password);
public sealed record PasswordRequest(string? Current, string? Password);
public sealed record NewUserRequest(string? Name, string? Password, string? Role);
public sealed record RoleRequest(string? Role);
public sealed record TestPlcRequest(string? Address);

public static class PlantEndpoints
{
    public static WebApplication MapPlant(this WebApplication app)
    {
        // Remember what this PLC-Ki started with, so the settings page can show what is running now
        var started = app.Services.GetRequiredService<PlantContext>();
        started.Running ??= app.Services.GetRequiredService<SettingsStore>().Current();

        var auth = app.MapGroup("/api/auth");
        var admin = app.MapGroup("/api/admin");
        // Every handler turns a PlantException into a readable error for the page
        auth.AddEndpointFilter(Errors);
        admin.AddEndpointFilter(Errors);

        auth.MapGet("/state", (HttpContext http, UserStore users, PlantContext ctx, Microsoft.Extensions.Configuration.IConfiguration config) =>
        {
            var name = http.User.Identity?.IsAuthenticated == true ? http.User.Identity.Name : null;
            return Results.Ok(new
            {
                setupNeeded = !users.Any,
                setupAllowed = !users.Any && PlantHost.IsLocal(http),
                user = name == null ? null : new { name, role = users.Find(name)?.Role },
                publicView = Microsoft.Extensions.Configuration.ConfigurationBinder.GetValue(config, "Plant:PublicView", false),
                service = ctx.IsService,
            });
        });

        // First start: whoever sits at the PLC-Ki PC creates the administrator
        auth.MapPost("/setup", async (LoginRequest req, HttpContext http, UserStore users, ILoggerFactory lf) =>
        {
            if (users.Any) throw new PlantException("The administrator already exists. Log in instead.", 409);
            if (!PlantHost.IsLocal(http))
                throw new PlantException("Create the first login on the PLC-Ki PC itself (http://localhost:…), not from another computer.", 403);
            var user = users.Add(req.Name ?? "", req.Password ?? "", UserStore.Admin);
            lf.CreateLogger("PlcKi.Plant").LogInformation("Administrator {Name} created.", user.Name);
            await PlantHost.SignIn(http, user);
            return Results.Ok(new { name = user.Name, role = user.Role });
        });

        auth.MapPost("/login", async (LoginRequest req, HttpContext http, UserStore users, LoginThrottle throttle, ILoggerFactory lf) =>
        {
            var name = req.Name?.Trim() ?? "";
            if (throttle.Blocked(name) is { } wait)
                throw new PlantException($"Too many wrong passwords. Try again in {Math.Ceiling(wait.TotalSeconds)} seconds.", 429);
            var user = users.Verify(name, req.Password);
            if (user == null)
            {
                throttle.Failed(name);
                lf.CreateLogger("PlcKi.Plant").LogWarning("Failed login for {Name} from {Ip}.", name, http.Connection.RemoteIpAddress);
                throw new PlantException("Wrong user name or password.", 401);
            }
            throttle.Succeeded(name);
            await PlantHost.SignIn(http, user);
            return Results.Ok(new { name = user.Name, role = user.Role });
        });

        auth.MapPost("/logout", async (HttpContext http) =>
        {
            await http.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return Results.NoContent();
        });

        auth.MapPost("/password", async (PasswordRequest req, HttpContext http, UserStore users) =>
        {
            var name = http.User.Identity?.IsAuthenticated == true ? http.User.Identity.Name : null;
            if (name == null) throw new PlantException("Please log in.", 401);
            if (users.Verify(name, req.Current) == null) throw new PlantException("The current password is wrong.");
            var user = users.SetPassword(name, req.Password ?? "");
            await PlantHost.SignIn(http, user);     // keep this session, end the others
            return Results.NoContent();
        });

        admin.MapGet("/settings", (SettingsStore store, PlantContext ctx, HttpContext http) => Results.Ok(new
        {
            settings = store.Current(),
            info = Info(ctx, store, http),
            models = Files(ctx, "models", "*.json", ctx.ModelsDir),
            snapshots = Files(ctx, Path.Combine("models", "snapshots"), "*.json", null),
        }));

        admin.MapPut("/settings", (PlantSettings s, SettingsStore store, PlantContext ctx, HttpContext http, ILoggerFactory lf) =>
        {
            store.Save(s);
            lf.CreateLogger("PlcKi.Plant").LogInformation("Settings changed by {Name}.", http.User.Identity?.Name);
            return Results.Ok(new { settings = store.Current(), info = Info(ctx, store, http) });
        });

        // Is anything listening on the S7 port (TCP 102) at this address? Opens a TCP connection and closes it; sends nothing.
        admin.MapPost("/test-plc", async (TestPlcRequest req) =>
        {
            var address = req.Address?.Trim() ?? "";
            if (address.Length == 0) throw new PlantException("Enter an address first.");
            if (SettingsStore.AddressProblem(address) is { } bad) return Results.Ok(new { ok = false, message = bad });
            using var tcp = new TcpClient();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            var started = DateTime.UtcNow;
            try
            {
                await tcp.ConnectAsync(address, 102, cts.Token);
                return Results.Ok(new { ok = true, message = $"{address} answers on the S7 port (102) after {(DateTime.UtcNow - started).TotalMilliseconds:0} ms." });
            }
            catch (OperationCanceledException)
            {
                return Results.Ok(new { ok = false, message = $"No answer from {address} within 3 s. Check the address, the cable and that this PC is in the PLC's subnet." });
            }
            catch (SocketException ex)
            {
                return Results.Ok(new { ok = false, message = ex.SocketErrorCode switch
                {
                    SocketError.HostNotFound or SocketError.NoData or SocketError.TryAgain => $"{address} is not known on this network. Check the spelling, or use the PLC's IP address from TIA.",
                    SocketError.ConnectionRefused => $"Something is at {address}, but it does not accept connections on the S7 port (102). Check it is really the PLC.",
                    SocketError.NetworkUnreachable or SocketError.HostUnreachable => $"{address} cannot be reached from this PC. Check that this PC is in the PLC's subnet and the cable is in.",
                    _ => $"{address}: {ex.Message}",
                } });
            }
        });

        // Upload a machine model made by the archive importer
        admin.MapPost("/models", async (HttpRequest request, PlantContext ctx) =>
        {
            if (!request.HasFormContentType) throw new PlantException("Send the model as a file.");
            var form = await request.ReadFormAsync();
            var file = form.Files.FirstOrDefault() ?? throw new PlantException("No file received.");
            var name = Path.GetFileName(file.FileName);
            if (!name.EndsWith(".json", StringComparison.OrdinalIgnoreCase) || name.Any(c => !(char.IsLetterOrDigit(c) || c is '.' or '-' or '_' or ' ')))
                throw new PlantException("The model must be a .json file whose name has only letters, digits, spaces, dots, dashes and underscores.");
            if (file.Length > 50_000_000) throw new PlantException("The file is larger than 50 MB.");
            var tmp = Path.Combine(ctx.ModelsDir, ".upload-" + Guid.NewGuid().ToString("N"));
            try
            {
                await using (var fs = File.Create(tmp)) await file.CopyToAsync(fs);
                MachineModel model;
                try { model = MachineModel.Load(tmp); }
                catch (Exception ex) { throw new PlantException($"This is not a PLC-Ki machine model: {ex.Message}"); }
                var target = Path.Combine(ctx.ModelsDir, name);
                File.Move(tmp, target, overwrite: true);
                return Results.Ok(new { path = target, name = model.Name, tags = model.Tags.Count });
            }
            finally
            {
                if (File.Exists(tmp)) File.Delete(tmp);
            }
        }).DisableAntiforgery();

        admin.MapPost("/restart", (PlantContext ctx, IHostApplicationLifetime life, ILoggerFactory lf) =>
        {
            if (!ctx.IsService)
            {
                if (ctx.RestartInProcess == null)
                    throw new PlantException("PLC-Ki was started by hand, so it cannot restart itself. Stop it (Ctrl+C) and start it again.", 409);
                lf.CreateLogger("PlcKi.Plant").LogWarning("Restart requested from the page.");
                var restart = ctx.RestartInProcess;
                _ = Task.Run(async () => { await Task.Delay(300); restart(); });
                return Results.Accepted();
            }
            lf.CreateLogger("PlcKi.Plant").LogWarning("Restart requested from the settings page.");
            // Leave with an error code: the service is set up (by the installer) to be restarted by Windows after that
            _ = Task.Run(async () =>
            {
                await Task.Delay(500);
                Environment.Exit(1);
            });
            return Results.Accepted();
        });

        admin.MapGet("/users", (UserStore users) =>
            Results.Ok(users.List().Select(u => new { u.Name, u.Role, u.Created })));

        admin.MapPost("/users", (NewUserRequest req, UserStore users) =>
        {
            var u = users.Add(req.Name ?? "", req.Password ?? "", req.Role ?? UserStore.Operator);
            return Results.Ok(new { u.Name, u.Role, u.Created });
        });

        admin.MapDelete("/users/{name}", (string name, UserStore users, HttpContext http) =>
        {
            users.Delete(name, http.User.Identity!.Name!);
            return Results.NoContent();
        });

        admin.MapPut("/users/{name}/role", (string name, RoleRequest req, UserStore users, HttpContext http) =>
        {
            users.SetRole(name, req.Role ?? "", http.User.Identity!.Name!);
            return Results.NoContent();
        });

        admin.MapPut("/users/{name}/password", (string name, PasswordRequest req, UserStore users) =>
        {
            users.SetPassword(name, req.Password ?? "");
            return Results.NoContent();
        });

        return app;
    }

    private static object Info(PlantContext ctx, SettingsStore store, HttpContext http) => new
    {
        dataDir = ctx.DataDir,
        service = ctx.IsService,
        restartNeeded = store.RestartNeeded(),
        // What runs now (the settings at start). The port is the one this page was opened on.
        running = ctx.Running is { } r ? new
        {
            r.Source, r.Address, r.Model, r.Export, r.SnapshotFile, r.PlcVia, r.PollMs, r.TimeoutMs, r.TiaMode, r.TiaUrl, r.AllowRemote,
            port = http.Request.Host.Port ?? r.Port,
        } : null,
        apiKey = ctx.ApiKeyInSettings ? "settings" : ctx.ApiKeyInEnvironment ? "environment" : "none",
        plcPassword = ctx.PlcPasswordInEnvironment ? "environment" : ctx.PlcPasswordInSettings ? "settings" : "none",
        tiaToken = ctx.TiaTokenInSettings || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("PLCKI_VM_TOKEN")) ? "set" : "none",
        canRestart = ctx.IsService || ctx.RestartInProcess != null,
    };

    /// <summary>Files shipped with the app (relative paths) and, optionally, uploaded ones (absolute paths).</summary>
    private static List<object> Files(PlantContext ctx, string appSubDir, string pattern, string? uploadDir)
    {
        var list = new List<object>();
        var shipped = Path.Combine(ctx.AppDir, appSubDir);
        if (Directory.Exists(shipped))
            foreach (var f in Directory.GetFiles(shipped, pattern).Order())
                list.Add(new { path = Path.Combine(appSubDir, Path.GetFileName(f)).Replace('\\', '/'), label = Path.GetFileName(f), uploaded = false });
        if (uploadDir != null && Directory.Exists(uploadDir))
            foreach (var f in Directory.GetFiles(uploadDir, pattern).Order())
                list.Add(new { path = f, label = Path.GetFileName(f), uploaded = true });
        return list;
    }

    private static async ValueTask<object?> Errors(EndpointFilterInvocationContext c, EndpointFilterDelegate next)
    {
        try
        {
            return await next(c);
        }
        catch (PlantException ex)
        {
            return Results.Json(new { error = ex.Message }, statusCode: ex.Status);
        }
    }
}
