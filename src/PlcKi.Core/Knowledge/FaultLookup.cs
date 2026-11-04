namespace PlcKi.Core.Knowledge;

/// <summary>
/// One place to look up a device fault code: first the manuals and fault lists added for this machine (they are
/// the device's own words), then the built-in general knowledge (FANUC, IO-Link, SINAMICS and other Siemens messages).
/// </summary>
public static class FaultLookup
{
    public static List<FaultHit> Find(DocumentLibrary? docs, string query, string? device = null, int max = 5)
    {
        var hits = new List<FaultHit>();
        if (string.IsNullOrWhiteSpace(query)) return hits;
        if (docs != null) hits.AddRange(docs.FindFaults(query, device, max));

        var codes = DocumentLibrary.CodesIn(query);
        foreach (var kb in new[] { SiemensDiagnostics.Devices, SiemensDiagnostics.Default })
            foreach (var m in kb.Search(query + (device != null ? " " + device : ""), max: 3))
            {
                // Asked for a code: only entries that list it; otherwise only good word matches
                bool codeHit = m.Entry.Codes.Any(c => codes.Contains(DocumentLibrary.NormalizeCode(c)));
                if (codes.Count > 0 ? !codeHit : m.Score < 12) continue;
                if (hits.Any(h => h.BuiltIn?.Id == m.Entry.Id)) continue;
                var shown = m.Entry.Codes.FirstOrDefault(c => codes.Contains(DocumentLibrary.NormalizeCode(c))) ?? m.Entry.Codes.FirstOrDefault() ?? m.Entry.Category;
                hits.Add(new FaultHit(new FaultCode { Code = shown, Title = m.Entry.Title }, null, m.Entry,
                    $"{kb.File.Name} (general knowledge)"));
            }
        return hits.Take(max).ToList();
    }

    /// <summary>The JSON the Ask tools and the pages get for one hit.</summary>
    public static object ToJson(FaultHit h) => h.BuiltIn is { } e
        ? new
        {
            code = h.Fault.Code, title = e.Title, source = h.Source, generalKnowledge = true, meaning = e.Meaning,
            causes = e.Causes, checks = e.Checks, fixes = e.Fixes, safety = e.Safety,
            documentId = (string?)null, page = (int?)null, details = (string?)null,
        }
        : new
        {
            code = h.Fault.Code, title = h.Fault.Title, source = h.Source, generalKnowledge = false, meaning = (string?)null,
            causes = h.Fault.Cause is { } c ? new List<string> { c } : new List<string>(), checks = new List<string>(),
            fixes = h.Fault.Fix is { } f ? new List<string> { f } : new List<string>(), safety = (string?)null,
            documentId = h.Document?.Id, page = (int?)h.Fault.Page, details = h.Fault.Details,
        };
}
