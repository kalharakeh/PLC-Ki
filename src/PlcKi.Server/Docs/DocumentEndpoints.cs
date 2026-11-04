using Microsoft.AspNetCore.Http.Features;
using PlcKi.Core.Knowledge;

namespace PlcKi.Server.Docs;

/// <summary>
/// Documents page: manuals, drawings and fault lists for this machine and for every machine.
///   GET  /api/documents                 the list, folders, built-in knowledge
///   GET  /api/documents/search?q=       pages that match, and fault codes when q has one
///   GET  /api/documents/related?q=      for the alarm page: fault codes and closely matching pages only
///   GET  /api/documents/{id}/file       the file itself (a PDF opens in the browser; add #page=N)
///   PUT  /api/admin/documents/upload/{name}?scope=machine|shared&amp;kind=&amp;device=   (body = the file)
///   PATCH /api/admin/documents/{id}, DELETE /api/admin/documents/{id}, POST /api/admin/documents/scan
/// Everyone who is logged in can read; adding and removing is for administrators (/api/admin).
/// </summary>
public static class DocumentEndpoints
{
    public sealed record DocumentChange(string? Kind, string? Device);

    public static void MapDocuments(this WebApplication app)
    {
        app.MapGet("/api/documents", (DocumentLibrary lib) => Results.Ok(new
        {
            machine = lib.MachineName,
            folders = new { machine = lib.MachineDir, shared = lib.SharedDir },
            busy = lib.Busy,
            faultCodes = lib.FaultCount,
            builtIn = new[] { SiemensDiagnostics.Default, SiemensDiagnostics.Devices }
                .Select(k => new { name = k.File.Name, entries = k.Entries.Count, version = k.File.Version }),
            items = lib.List(),
        }));

        app.MapGet("/api/documents/search", (string? q, string? kind, DocumentLibrary lib) =>
        {
            q = (q ?? "").Trim();
            if (q.Length == 0) return Results.Ok(new { faults = Array.Empty<object>(), pages = Array.Empty<object>() });
            var faults = DocumentLibrary.CodesIn(q).Count > 0 ? FaultLookup.Find(lib, q, max: 6) : new();
            var pages = lib.Search(q, 12, DocumentLibrary.Kinds.Contains(kind) ? kind : null);
            return Results.Ok(new { faults = faults.Select(FaultLookup.ToJson), pages = pages.Select(Page) });
        });

        // q = the alarm text (fault codes in it), tags = the devices behind the alarm (XV101,M201): the pages that name
        // them, such as the drawing sheet where XV101 is wired. Without devices, only pages with most of the alarm's words.
        app.MapGet("/api/documents/related", (string? q, string? tags, DocumentLibrary lib) =>
        {
            q = (q ?? "").Trim();
            var ids = (tags ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Take(3).ToList();
            var faults = DocumentLibrary.CodesIn(q).Count > 0 ? FaultLookup.Find(lib, q, max: 3) : new();
            var pages = ids.Count > 0
                ? ids.SelectMany(id => lib.Search(id, 3, minMatch: 1)).GroupBy(h => (h.Document.Id, h.Page)).Select(g => g.First())
                    .OrderByDescending(h => h.Score).Take(4).ToList()
                : q.Length > 0 ? lib.Search(q, 3, minMatch: 0.6).Where(h => h.Score >= 2).ToList() : new();
            return Results.Ok(new { faults = faults.Select(FaultLookup.ToJson), pages = pages.Select(Page), documents = lib.List().Count });
        });

        app.MapGet("/api/documents/{id}/file", (string id, DocumentLibrary lib) =>
        {
            if (lib.Get(id) is not { } d || !File.Exists(d.Path)) return Results.NotFound(new { error = "That document is not in the library any more." });
            var type = Path.GetExtension(d.Path).ToLowerInvariant() switch
            {
                ".pdf" => "application/pdf",
                ".csv" => "text/csv; charset=utf-8",
                ".tsv" => "text/tab-separated-values; charset=utf-8",
                _ => "text/plain; charset=utf-8",
            };
            // Inline, so the browser's PDF viewer opens it at #page=N
            return Results.File(d.Path, type, enableRangeProcessing: true);
        });

        var admin = app.MapGroup("/api/admin/documents");
        // The file is the request body (no form), so a 50 MB list manual streams straight to disk
        admin.MapPut("/upload/{name}", async (string name, string? scope, string? kind, string? device, HttpRequest request, DocumentLibrary lib, CancellationToken ct) =>
        {
            if (request.HttpContext.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } f) f.MaxRequestBodySize = 500L * 1024 * 1024;
            try { return Results.Ok(await lib.AddAsync(request.Body, name, scope ?? "machine", kind, device, ct)); }
            catch (ArgumentException ex) { return Results.Json(new { error = ex.Message }, statusCode: 400); }
        });
        admin.MapPatch("/{id}", (string id, DocumentChange change, DocumentLibrary lib) =>
        {
            try { return Results.Ok(lib.Update(id, change.Kind, change.Device)); }
            catch (KeyNotFoundException ex) { return Results.NotFound(new { error = ex.Message }); }
        });
        admin.MapDelete("/{id}", (string id, DocumentLibrary lib) => { lib.Delete(id); return Results.NoContent(); });
        admin.MapPost("/scan", (DocumentLibrary lib) => { _ = lib.Scan(); return Results.Ok(new { items = lib.List() }); });
    }

    private static object Page(DocumentHit h) => new
    {
        id = h.Document.Id, file = h.Document.File, device = h.Document.Device, kind = h.Document.Kind, scope = h.Document.Scope,
        page = h.Page, text = h.Snippet, score = h.Score,
    };
}
