using Microsoft.AspNetCore.Http.Features;
using PlcKi.Plant;
using PlcKi.Server.Live;

namespace PlcKi.Server.Link;

/// <summary>
/// /link/...        what PLC-Ki Link calls, with its key (header X-PlcKi-Link); no login
/// /link/tia/...    the TIA helper as seen by this server's jobs, carried by Link (header X-PlcKi-Token = relay token)
/// /api/admin/link  status and a new pairing key, for the Connections page (administrators)
/// </summary>
public static class LinkEndpoints
{
    public const string KeyHeader = "X-PlcKi-Link";

    public static WebApplication MapLink(this WebApplication app)
    {
        var link = app.MapGroup("/link");

        // Link says hello (at start and every 25 s) and gets what to read
        link.MapPost("/hello", (LinkHello hello, HttpContext http, LinkHub hub, PlcKiOptions opt) =>
        {
            if (!Authorized(http, hub)) return Refused();
            hub.Seen(hello);
            var wantPlc = opt.Plc.Via == "link";
            return Results.Ok(new
            {
                plc = wantPlc ? new
                {
                    address = opt.Plc.Address, security = opt.Plc.Security, username = opt.Plc.Username,
                    pollMs = opt.Plc.PollMs, timeoutMs = opt.Plc.TimeoutMs,
                    tags = hub.Tags.Select(t => new { t.Id, t.Symbol, t.Type, t.Label }),
                } : null,
                tia = opt.Tia.Mode == "link",
                plcsimIp = opt.Tia.PlcsimIp,
            });
        });

        link.MapPost("/values", (LinkValues values, HttpContext http, LinkHub hub) =>
        {
            if (!Authorized(http, hub)) return Refused();
            hub.Seen();
            hub.SetValues(values);
            return Results.NoContent();
        });

        // Long poll: the next TIA call for Link to carry (204 when none within 25 s)
        link.MapGet("/next", async (HttpContext http, LinkHub hub, CancellationToken ct) =>
        {
            if (!Authorized(http, hub)) return Refused();
            hub.Seen();
            var r = await hub.NextAsync(TimeSpan.FromSeconds(25), ct);
            hub.Seen();
            return r == null ? Results.NoContent()
                : Results.Ok(new { id = r.Id, method = r.Method, path = r.PathAndQuery, contentType = r.ContentType, hasBody = r.BodyFile != null });
        });

        link.MapGet("/req/{id}/body", (string id, HttpContext http, LinkHub hub) =>
        {
            if (!Authorized(http, hub)) return Refused();
            var r = hub.Pending(id);
            if (r?.BodyFile == null || !File.Exists(r.BodyFile)) return Results.NotFound();
            return Results.File(r.BodyFile, r.ContentType ?? "application/octet-stream", enableRangeProcessing: false);
        });

        // Link sends the helper's answer; the body streams straight through to the waiting caller
        link.MapPost("/req/{id}/response", async (string id, HttpContext http, LinkHub hub) =>
        {
            if (!Authorized(http, hub)) return Refused();
            hub.Seen();
            var r = hub.Pending(id);
            if (r == null || r.Abandoned) return Results.NotFound();
            NoBodyLimit(http);
            var status = int.TryParse(http.Request.Headers["X-Status"], out var s) ? s : 502;
            r.Response.TrySetResult(new RelayResponse(status, http.Request.ContentType, http.Request.Body));
            await Task.WhenAny(r.Copied.Task, Task.Delay(TimeSpan.FromMinutes(30), http.RequestAborted));
            return Results.NoContent();
        });

        // The relay: jobs and the Setup pages talk to "the TIA helper" here, Link carries it to the laptop
        link.Map("/tia/{**rest}", async (string? rest, HttpContext http, LinkHub hub) =>
        {
            if (!hub.CheckRelayToken(http.Request.Headers["X-PlcKi-Token"]))
                return Results.Json(new { error = "Wrong token." }, statusCode: 401);
            if (!hub.Connected)
                return Results.Json(new { error = "PLC-Ki Link is not connected. Start it on the laptop with TIA." }, statusCode: 502);

            string? bodyFile = null;
            var hasBody = http.Request.ContentLength > 0 || http.Request.Headers.TransferEncoding.Count > 0;
            if (hasBody)
            {
                NoBodyLimit(http);
                bodyFile = Path.Combine(Path.GetTempPath(), "plcki-relay-" + Guid.NewGuid().ToString("N"));
                await using (var f = File.Create(bodyFile)) await http.Request.Body.CopyToAsync(f, http.RequestAborted);
            }
            var req = new RelayRequest
            {
                Method = http.Request.Method,
                PathAndQuery = "/" + (rest ?? "") + http.Request.QueryString,
                ContentType = http.Request.ContentType,
                BodyFile = bodyFile,
            };
            hub.Enqueue(req);
            try
            {
                // A big archive first has to travel to the laptop; everything else answers within seconds
                var limit = bodyFile != null ? TimeSpan.FromMinutes(30) : TimeSpan.FromSeconds(90);
                var done = await Task.WhenAny(req.Response.Task, Task.Delay(limit, http.RequestAborted));
                if (done != req.Response.Task)
                    return Results.Json(new { error = "PLC-Ki Link did not answer in time." }, statusCode: 504);
                var resp = req.Response.Task.Result;
                http.Response.StatusCode = resp.Status;
                if (resp.ContentType != null) http.Response.ContentType = resp.ContentType;
                await resp.Body.CopyToAsync(http.Response.Body, http.RequestAborted);
                return Results.Empty;
            }
            finally
            {
                req.Abandoned = true;
                req.Copied.TrySetResult();
                hub.Done(req.Id);
                if (bodyFile != null) try { File.Delete(bodyFile); } catch { /* temp */ }
            }
        });

        // ---- for the Connections page ----
        var admin = app.MapGroup("/api/admin/link");
        admin.MapGet("/", (LinkHub hub) => Results.Ok(hub.Status()));
        admin.MapPost("/key", (LinkHub hub) => Results.Ok(new { key = hub.NewKey() }));

        return app;
    }

    private static bool Authorized(HttpContext http, LinkHub hub) => hub.CheckKey(http.Request.Headers[KeyHeader]);

    private static IResult Refused() =>
        Results.Json(new { error = "Unknown Link key. Make a new one under Setup, Connections, and start Link with it." }, statusCode: 401);

    private static void NoBodyLimit(HttpContext http)
    {
        if (http.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } f) f.MaxRequestBodySize = null;
    }
}
