using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace PlcKi.Core.Knowledge;

/// <summary>One document in the library: a device manual, an electrical drawing, a fault list.</summary>
public sealed class DocumentInfo
{
    public string Id { get; set; } = "";
    /// <summary>File name in its folder (also the name shown, without the extension).</summary>
    public string File { get; set; } = "";
    /// <summary>"machine": only for this machine. "shared": for every machine on this PLC-Ki.</summary>
    public string Scope { get; set; } = "machine";
    /// <summary>manual, drawing, faults (a fault list) or other.</summary>
    public string Kind { get; set; } = "manual";
    /// <summary>What it is for, in the plant's words: "SINAMICS G120 drives", "Fanuc R-30iB", "Inficon LDS3000".</summary>
    public string? Device { get; set; }
    public int Pages { get; set; }
    public long Bytes { get; set; }
    public DateTimeOffset Added { get; set; }
    public int FaultCodes { get; set; }
    /// <summary>reading, ready, notext (a scanned PDF without text), failed.</summary>
    public string State { get; set; } = "reading";
    public string? Note { get; set; }
    /// <summary>File size and time when it was read, so a replaced file is read again.</summary>
    public long SourceTicks { get; set; }
}

/// <summary>A fault or error code found in a document (a list manual, a fault table) or built in.</summary>
public sealed class FaultCode
{
    public string Code { get; set; } = "";
    public string Title { get; set; } = "";
    /// <summary>Text that follows the code in the document: reaction, cause, remedy...</summary>
    public string? Details { get; set; }
    public string? Cause { get; set; }
    public string? Fix { get; set; }
    public int Page { get; set; }
}

/// <summary>What is stored next to each document after reading it once.</summary>
public sealed class DocumentIndex
{
    public DocumentInfo Info { get; set; } = new();
    public List<string> Pages { get; set; } = new();
    public List<FaultCode> Faults { get; set; } = new();
}

public sealed record DocumentHit(DocumentInfo Document, int Page, string Snippet, double Score);
public sealed record FaultHit(FaultCode Fault, DocumentInfo? Document, DiagnosticEntry? BuiltIn, string Source);

/// <summary>
/// The manuals, drawings and fault lists PLC-Ki knows, kept as plain files so people can also copy them in:
///   documents/shared/...                  for every machine on this PLC-Ki (e.g. the SINAMICS list manual)
///   documents/machines/&lt;machine&gt;/...      only for one machine (e.g. its EPLAN drawing)
/// Each file is read once (PDF text, CSV fault tables, text files) into documents/.../.index/&lt;file&gt;.json; search
/// works on that text, page by page. Nothing here talks to a PLC or to the internet.
/// </summary>
public sealed class DocumentLibrary
{
    public static readonly string[] Extensions = { ".pdf", ".csv", ".tsv", ".txt", ".md" };
    public static readonly string[] Kinds = { "manual", "drawing", "faults", "other" };

    private static readonly JsonSerializerOptions JsonOpt = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly object _lock = new();
    private readonly Dictionary<string, Entry> _docs = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _reading = new(1, 1);
    /// <summary>Background reads that were started and have not finished. Counted from the moment they are queued, not from the moment a worker picks them up.</summary>
    private readonly List<Task> _running = new();

    private sealed class Entry
    {
        public required string Path;
        public required DocumentIndex Index;
        public List<Dictionary<string, int>> Terms = new();
        public List<int> Lengths = new();
        /// <summary>Table of contents or index pages ("Motor overcurrent ....... 812"): they name everything, explain nothing.</summary>
        public List<bool> Contents = new();
    }

    public DocumentLibrary(string root, string machineName)
    {
        Root = System.IO.Path.GetFullPath(root);
        MachineKey = Key(machineName);
        MachineName = machineName;
        Directory.CreateDirectory(SharedDir);
        Directory.CreateDirectory(MachineDir);
    }

    public string Root { get; }
    public string MachineKey { get; }
    public string MachineName { get; }
    public string SharedDir => System.IO.Path.Combine(Root, "shared");
    public string MachineDir => System.IO.Path.Combine(Root, "machines", MachineKey);

    /// <summary>Folder name for a machine: its name with only safe characters.</summary>
    public static string Key(string machineName)
    {
        var s = Regex.Replace(machineName ?? "", @"[^A-Za-z0-9._-]+", "-").Trim('-', '.');
        return s.Length == 0 ? "machine" : s.Length > 60 ? s[..60] : s;
    }

    public static string SafeFileName(string name)
    {
        var file = System.IO.Path.GetFileName(name ?? "");
        var ext = System.IO.Path.GetExtension(file).ToLowerInvariant();
        var stem = Regex.Replace(System.IO.Path.GetFileNameWithoutExtension(file), @"[^\p{L}\p{N} ._()+-]+", "_").Trim(' ', '.');
        if (stem.Length == 0) stem = "document";
        if (stem.Length > 100) stem = stem[..100];
        return stem + ext;
    }

    private string Dir(string scope) => scope == "shared" ? SharedDir : MachineDir;
    private static string IndexFile(string path) =>
        System.IO.Path.Combine(System.IO.Path.GetDirectoryName(path)!, ".index", System.IO.Path.GetFileName(path) + ".json");
    private static string IdOf(string scope, string file) =>
        Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(scope + "/" + file.ToLowerInvariant())))[..12].ToLowerInvariant();
    private static long Ticks(FileInfo f) => f.Length ^ f.LastWriteTimeUtc.Ticks;

    /// <summary>Every document, this machine's first, then the shared ones.</summary>
    public List<DocumentInfo> List()
    {
        lock (_lock)
            return _docs.Values.Select(e => e.Index.Info)
                .OrderBy(i => i.Scope == "machine" ? 0 : 1).ThenBy(i => i.File, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public bool Busy
    {
        get
        {
            lock (_lock) return _running.Count > 0 || _docs.Values.Any(e => e.Index.Info.State == "reading");
        }
    }

    public (DocumentInfo Info, string Path)? Get(string id)
    {
        lock (_lock) return _docs.TryGetValue(id, out var e) ? (e.Index.Info, e.Path) : null;
    }

    /// <summary>
    /// Finds new, changed and removed files in both folders. New or changed files are read in the background;
    /// call <see cref="ReadPendingAsync"/> to wait for them (tests) or let it run (server).
    /// </summary>
    public Task Scan(CancellationToken ct = default)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var todo = new List<Entry>();
        foreach (var scope in new[] { "machine", "shared" })
        {
            foreach (var path in Directory.EnumerateFiles(Dir(scope)))
            {
                var file = System.IO.Path.GetFileName(path);
                if (file.StartsWith('.') || file.EndsWith(".part", StringComparison.OrdinalIgnoreCase)) continue;
                if (!Extensions.Contains(System.IO.Path.GetExtension(file).ToLowerInvariant())) continue;
                var id = IdOf(scope, file);
                seen.Add(id);
                var fi = new FileInfo(path);
                lock (_lock)
                    if (_docs.TryGetValue(id, out var have) && have.Index.Info.SourceTicks == Ticks(fi)) continue;

                // Read before? Use what is stored, unless the file changed since
                DocumentIndex? idx = null;
                try
                {
                    if (System.IO.File.Exists(IndexFile(path)))
                        idx = JsonSerializer.Deserialize<DocumentIndex>(System.IO.File.ReadAllText(IndexFile(path)), JsonOpt);
                }
                catch (Exception) { idx = null; }
                if (idx != null && idx.Info.SourceTicks == Ticks(fi) && idx.Info.State != "reading")
                {
                    idx.Info.Id = id; idx.Info.Scope = scope; idx.Info.File = file;
                    Put(new Entry { Path = path, Index = idx });
                    continue;
                }
                var info = new DocumentInfo
                {
                    Id = id, File = file, Scope = scope, Bytes = fi.Length, Added = idx?.Info.Added ?? DateTimeOffset.Now,
                    Kind = idx?.Info.Kind ?? GuessKind(file), Device = idx?.Info.Device, State = "reading",
                };
                var e = new Entry { Path = path, Index = new DocumentIndex { Info = info } };
                Put(e);
                todo.Add(e);
            }
        }
        lock (_lock)
            foreach (var gone in _docs.Keys.Where(k => !seen.Contains(k)).ToList()) _docs.Remove(gone);
        if (todo.Count == 0) return Task.CompletedTask;
        // Registered before Scan returns: whoever waits afterwards sees this read even if no worker thread has started it yet
        Task read;
        lock (_lock)
        {
            read = Task.Run(() => ReadAll(todo, ct), ct);
            _running.Add(read);
        }
        _ = read.ContinueWith(t => { lock (_lock) _running.Remove(t); }, TaskScheduler.Default);
        return read;
    }

    /// <summary>Waits until every document that was found so far is read.</summary>
    public async Task ReadPendingAsync()
    {
        while (true)
        {
            Task[] running;
            lock (_lock) running = _running.ToArray();
            if (running.Length == 0) return;
            // A read that failed or was cancelled is still finished; its documents are marked "failed" or stay as they were
            foreach (var t in running) { try { await t.ConfigureAwait(false); } catch (Exception) { } }
            await Task.Yield();
        }
    }

    private async Task ReadAll(List<Entry> todo, CancellationToken ct)
    {
        await _reading.WaitAsync(ct);
        try
        {
            foreach (var e in todo)
            {
                ct.ThrowIfCancellationRequested();
                DocumentIndex idx;
                try
                {
                    idx = DocumentReader.Read(e.Path, e.Index.Info);
                    idx.Info.SourceTicks = System.IO.File.Exists(e.Path) ? Ticks(new FileInfo(e.Path)) : 0;
                    Save(e.Path, idx);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // File removed, locked or folder gone: say so instead of staying "reading" for ever
                    e.Index.Info.State = "failed";
                    e.Index.Info.Note = "The file could not be read: " + ex.Message;
                    continue;
                }
                lock (_lock)
                    if (_docs.ContainsKey(idx.Info.Id)) Put(new Entry { Path = e.Path, Index = idx });
            }
        }
        finally { _reading.Release(); }
    }

    private static void Save(string path, DocumentIndex idx)
    {
        var file = IndexFile(path);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(file)!);
        // Own temporary name per write: two writers of the same index (a rescan, a second PLC-Ki process) must not share one
        var tmp = file + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp";
        System.IO.File.WriteAllText(tmp, JsonSerializer.Serialize(idx, JsonOpt));
        System.IO.File.Move(tmp, file, overwrite: true);
    }

    private void Put(Entry e)
    {
        e.Terms = e.Index.Pages.Select(p => Count(Tokens(p))).ToList();
        e.Lengths = e.Terms.Select(t => t.Values.Sum()).ToList();
        e.Contents = e.Index.Pages.Select(p =>
        {
            var lines = p.Split('\n').Where(l => l.Trim().Length > 0).ToList();
            return lines.Count > 0 && lines.Count(l => Regex.IsMatch(l, @"(\.\s?){4,}\s*\d+\s*$")) >= 0.3 * lines.Count;
        }).ToList();
        lock (_lock) _docs[e.Index.Info.Id] = e;
    }

    /// <summary>Saves an uploaded file (the stream) and starts reading it. Replaces a file with the same name.</summary>
    public async Task<DocumentInfo> AddAsync(Stream content, string name, string scope, string? kind, string? device, CancellationToken ct = default)
    {
        scope = scope == "shared" ? "shared" : "machine";
        var file = SafeFileName(name);
        if (!Extensions.Contains(System.IO.Path.GetExtension(file)))
            throw new ArgumentException("PLC-Ki reads PDF files, fault lists as CSV, and text files (.pdf, .csv, .tsv, .txt, .md).");
        var path = System.IO.Path.Combine(Dir(scope), file);
        var tmp = path + ".part";
        await using (var fs = System.IO.File.Create(tmp)) await content.CopyToAsync(fs, ct);
        System.IO.File.Move(tmp, path, overwrite: true);
        // Kind and device go into the stored index first, so the scan keeps them
        var fi = new FileInfo(path);
        var info = new DocumentInfo
        {
            Id = IdOf(scope, file), File = file, Scope = scope, Bytes = fi.Length, Added = DateTimeOffset.Now,
            Kind = Kinds.Contains(kind) ? kind! : GuessKind(file), Device = Clean(device), State = "reading",
        };
        Save(path, new DocumentIndex { Info = info });
        _ = Scan(ct);
        return Get(info.Id)?.Info ?? info;
    }

    /// <summary>Changes the kind or the device a document is for.</summary>
    public DocumentInfo Update(string id, string? kind, string? device)
    {
        Entry e;
        lock (_lock)
        {
            if (!_docs.TryGetValue(id, out e!)) throw new KeyNotFoundException("That document is not in the library any more.");
            if (kind != null && Kinds.Contains(kind)) e.Index.Info.Kind = kind;
            if (device != null) e.Index.Info.Device = Clean(device);
        }
        if (e.Index.Info.State != "reading") Save(e.Path, e.Index);
        return e.Index.Info;
    }

    public void Delete(string id)
    {
        Entry? e;
        lock (_lock) { if (!_docs.Remove(id, out e)) return; }
        TryDelete(e.Path);
        TryDelete(IndexFile(e.Path));
    }

    private static void TryDelete(string path) { try { System.IO.File.Delete(path); } catch (IOException) { } }

    private static string? Clean(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim().Length > 120 ? s.Trim()[..120] : s.Trim();

    private static string GuessKind(string file)
    {
        var ext = System.IO.Path.GetExtension(file).ToLowerInvariant();
        if (ext is ".csv" or ".tsv") return "faults";
        return Regex.IsMatch(file, @"eplan|schaltplan|schema|wiring|circuit|drawing|electrical|stromlauf|\bE-?plan", RegexOptions.IgnoreCase) ? "drawing" : "manual";
    }

    // ---- search ----

    /// <summary>
    /// The pages that best match the words or codes, best first (BM25 over every page of every ready document).
    /// kind narrows to manual, drawing, faults or other; device to documents whose device or file name contains it.
    /// minMatch: the share of the query's words a page must have (0.6 = at least 60 %).
    /// </summary>
    public List<DocumentHit> Search(string query, int max = 5, string? kind = null, string? device = null, double minMatch = 0)
    {
        var qTerms = Tokens(query).Distinct().ToList();
        if (qTerms.Count == 0) return new();
        List<Entry> docs;
        lock (_lock) docs = _docs.Values.Where(e => e.Index.Info.State == "ready").ToList();
        if (kind != null) docs = docs.Where(e => e.Index.Info.Kind == kind).ToList();
        if (!string.IsNullOrWhiteSpace(device))
            docs = docs.Where(e => (e.Index.Info.Device ?? "").Contains(device, StringComparison.OrdinalIgnoreCase)
                                   || e.Index.Info.File.Contains(device, StringComparison.OrdinalIgnoreCase)).ToList() is { Count: > 0 } d ? d : docs;

        int n = docs.Sum(e => e.Terms.Count);
        if (n == 0) return new();
        double avg = docs.Sum(e => e.Lengths.Sum()) / (double)n;
        var df = qTerms.ToDictionary(t => t, t => docs.Sum(e => e.Terms.Count(p => p.ContainsKey(t))));
        // A word in nearly every page (the device name in a manual about it) says little
        var hits = new List<DocumentHit>();
        foreach (var e in docs)
        {
            // Words in the device name or file name count a little on every page of that document
            var meta = Count(Tokens(e.Index.Info.File + " " + e.Index.Info.Device));
            for (int p = 0; p < e.Terms.Count; p++)
            {
                double score = 0;
                int matched = 0;
                foreach (var t in qTerms)
                {
                    e.Terms[p].TryGetValue(t, out var tf);
                    if (tf == 0) { if (meta.ContainsKey(t)) score += 0.3; continue; }
                    matched++;
                    double idf = Math.Log(1 + (n - df[t] + 0.5) / (df[t] + 0.5));
                    score += idf * tf * 2.2 / (tf + 1.2 * (0.25 + 0.75 * e.Lengths[p] / Math.Max(1, avg)));
                    if (CodeToken.IsMatch(t)) score += 4;    // an exact fault code is worth more than any word
                }
                if (matched == 0 || matched < minMatch * qTerms.Count) continue;
                score *= 0.5 + 0.5 * matched / qTerms.Count;
                if (e.Contents[p]) score *= 0.3;
                hits.Add(new DocumentHit(e.Index.Info, p + 1, Snippet(e.Index.Pages[p], qTerms), Math.Round(score, 2)));
            }
        }
        return hits.OrderByDescending(h => h.Score).ThenBy(h => h.Page).Take(Math.Clamp(max, 1, 20)).ToList();
    }

    /// <summary>Fault codes found in the documents that match a code (exact) or words (title), best first.</summary>
    public List<FaultHit> FindFaults(string query, string? device = null, int max = 5)
    {
        List<Entry> docs;
        lock (_lock) docs = _docs.Values.Where(e => e.Index.Info.State == "ready" && e.Index.Faults.Count > 0).ToList();
        if (!string.IsNullOrWhiteSpace(device))
            docs = docs.Where(e => (e.Index.Info.Device ?? "").Contains(device, StringComparison.OrdinalIgnoreCase)
                                   || e.Index.Info.File.Contains(device, StringComparison.OrdinalIgnoreCase)).ToList() is { Count: > 0 } d ? d : docs;
        var codes = CodesIn(query);
        var words = Tokens(query).Where(t => t.Length >= 3 && !CodeToken.IsMatch(t)).ToHashSet();
        var found = new List<(FaultHit Hit, int Score)>();
        foreach (var e in docs)
            foreach (var f in e.Index.Faults)
            {
                int score = 0;
                if (codes.Contains(NormalizeCode(f.Code))) score += 100;
                else if (codes.Count > 0) continue;     // asked for a code: only that code
                else
                {
                    var tw = Tokens(f.Title).ToHashSet();
                    score += words.Count(tw.Contains) * 3;
                    if (score < 3 || words.Count(tw.Contains) < Math.Min(2, words.Count)) continue;
                }
                found.Add((new FaultHit(f, e.Index.Info, null, $"{e.Index.Info.File}, page {f.Page}"), score));
            }
        return found.OrderByDescending(x => x.Score).ThenBy(x => x.Hit.Document!.Scope == "machine" ? 0 : 1)
            .Select(x => x.Hit).Take(Math.Clamp(max, 1, 20)).ToList();
    }

    public int FaultCount { get { lock (_lock) return _docs.Values.Sum(e => e.Index.Faults.Count); } }

    // ---- text helpers ----

    // Fault and error codes as people write them: F07801, A7910, SRVO-062, 0x8C10, 16#8C10, E123, KSS01234
    private static readonly Regex CodeToken = new(@"^(?:[a-z]{1,5}-?\d{2,6}|0x[0-9a-f]{3,8}|16#[0-9a-f]{2,8})$", RegexOptions.Compiled);
    private static readonly Regex CodeInText = new(@"\b(?:[A-Za-z]{1,5}-?\d{2,6}|0x[0-9A-Fa-f]{3,8}|16#[0-9A-Fa-f]{2,8})\b", RegexOptions.Compiled);

    /// <summary>Codes in a question, normalized ("f7801" and "F07801" are the same).</summary>
    public static HashSet<string> CodesIn(string text) =>
        CodeInText.Matches(text ?? "").Select(m => m.Value).Where(v => Regex.IsMatch(v, @"\d")).Select(NormalizeCode).ToHashSet(StringComparer.Ordinal);

    /// <summary>F07801 = F7801, SRVO-062 = SRVO062, 16#8C10 = 0x8C10.</summary>
    public static string NormalizeCode(string code)
    {
        var s = Regex.Replace(code.ToUpperInvariant(), @"[\s-]", "");
        if (s.StartsWith("16#")) s = "0X" + s[3..];
        if (s.StartsWith("0X")) return "0X" + s[2..].TrimStart('0');
        var m = Regex.Match(s, @"^([A-Z]+)0*(\d+)$");
        return m.Success ? m.Groups[1].Value + m.Groups[2].Value : s;
    }

    private static readonly HashSet<string> Stop = new(StringComparer.Ordinal)
    {
        "the", "and", "for", "with", "this", "that", "from", "are", "was", "has", "have", "not", "can", "you", "what", "how",
        "why", "does", "mean", "means", "der", "die", "das", "und", "ist", "mit", "von", "den", "des", "ein", "eine", "auf",
    };

    /// <summary>Lower-case words and codes; a code is also kept in its normalized form so F7801 finds F07801.</summary>
    public static IEnumerable<string> Tokens(string? text)
    {
        if (string.IsNullOrEmpty(text)) yield break;
        foreach (Match m in Regex.Matches(text.ToLowerInvariant(), @"0x[0-9a-f]+|16#[0-9a-f]+|[\p{L}\p{N}]+(?:[-:.%/][\p{L}\p{N}]+)*"))
        {
            var w = m.Value;
            if (w.Length < 2 || Stop.Contains(w)) continue;
            if (CodeToken.IsMatch(w)) { yield return NormalizeCode(w).ToLowerInvariant(); continue; }
            yield return w;
            // X2:14, %I12.3, -B23: also the parts, so "X2" finds "X2:14"
            if (Regex.IsMatch(w, @"[-:.%/]"))
                foreach (var part in Regex.Split(w, @"[-:.%/]").Where(x => x.Length >= 2 && !Stop.Contains(x)))
                    yield return CodeToken.IsMatch(part) ? NormalizeCode(part).ToLowerInvariant() : part;
        }
    }

    private static Dictionary<string, int> Count(IEnumerable<string> tokens)
    {
        var d = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var t in tokens) d[t] = d.GetValueOrDefault(t) + 1;
        return d;
    }

    /// <summary>About 300 characters around the first place a query word appears.</summary>
    public static string Snippet(string page, IReadOnlyCollection<string> terms, int width = 300)
    {
        var flat = Regex.Replace(page, @"\s+", " ").Trim();
        int best = -1;
        foreach (var t in terms.OrderByDescending(t => CodeToken.IsMatch(t)).ThenByDescending(t => t.Length))
        {
            // The text may write the code with zeros or a dash: F07801 for f7801
            var pattern = CodeToken.IsMatch(t)
                ? Regex.Replace(Regex.Escape(t), @"^([a-z]+)(\d)", "$1[-\\s]?0*$2")
                : Regex.Escape(t);
            var m = Regex.Match(flat, pattern, RegexOptions.IgnoreCase);
            if (m.Success) { best = m.Index; break; }
        }
        if (best < 0) return flat.Length <= width ? flat : flat[..width] + " …";
        int start = Math.Max(0, best - width / 3);
        int end = Math.Min(flat.Length, start + width);
        return (start > 0 ? "… " : "") + flat[start..end].Trim() + (end < flat.Length ? " …" : "");
    }
}
