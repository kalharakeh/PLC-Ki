using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using PlcKi.Core.Model;

namespace PlcKi.Server.Link;

/// <summary>
/// The server's end of PLC-Ki Link. Link runs on the user's laptop, which reaches the PLC and has TIA; the server
/// reaches neither. Link connects out to the server (the server never connects in) and
///   - sends the PLC values it reads (read-only, the same driver as the server uses directly), and
///   - carries the server's calls to "TiaImporter serve" on the laptop: the server queues them, Link picks them up,
///     runs them against its local helper and sends the answer back.
/// Lives for the whole process, so an in-page restart does not drop a job that is talking to TIA through Link.
/// </summary>
public sealed class LinkHub
{
    /// <summary>Jobs (child processes) call the relay with this token instead of the TIA helper's own.</summary>
    public string RelayToken { get; } = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
    /// <summary>Where this app answers on this machine, e.g. http://localhost:5080 (set once the app runs).</summary>
    public string? LocalBase { get; set; }
    public string? RelayUrl => LocalBase == null ? null : LocalBase + "/link/tia";

    private string? _keyFile;
    private byte[]? _keyHash;
    private DateTimeOffset? _keyCreated;

    private readonly object _gate = new();
    private DateTimeOffset _lastSeen = DateTimeOffset.MinValue;
    private LinkHello _hello = new();
    private LinkValues? _values;
    private long _valuesSeq;
    private readonly SemaphoreSlim _valuesSignal = new(0, int.MaxValue);
    private IReadOnlyList<TagDef> _tags = Array.Empty<TagDef>();

    private readonly Channel<RelayRequest> _queue = Channel.CreateUnbounded<RelayRequest>();
    private readonly ConcurrentDictionary<string, RelayRequest> _pending = new();

    /// <summary>How long Link may be quiet before it counts as gone (it calls at least every 25 s).</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(40);

    // ---------- pairing key ----------

    /// <summary>Loads the key (its hash) from the data folder. Called on every (re)start of the app.</summary>
    public void Load(string dataDir)
    {
        _keyFile = Path.Combine(dataDir, "link.json");
        _keyHash = null; _keyCreated = null;
        try
        {
            if (!File.Exists(_keyFile)) return;
            using var doc = JsonDocument.Parse(File.ReadAllText(_keyFile));
            _keyHash = Convert.FromHexString(doc.RootElement.GetProperty("keyHash").GetString()!);
            _keyCreated = doc.RootElement.TryGetProperty("created", out var c) ? c.GetDateTimeOffset() : null;
        }
        catch { _keyHash = null; }
    }

    public bool HasKey => _keyHash != null;
    public DateTimeOffset? KeyCreated => _keyCreated;

    /// <summary>Makes a new key, keeps only its hash, and returns it once. An old key stops working.</summary>
    public string NewKey()
    {
        if (_keyFile == null) throw new InvalidOperationException("Link is not set up yet.");
        var key = "plk_" + Convert.ToBase64String(RandomNumberGenerator.GetBytes(24)).Replace('+', '-').Replace('/', '_').TrimEnd('=');
        _keyHash = SHA256.HashData(Encoding.UTF8.GetBytes(key));
        _keyCreated = DateTimeOffset.Now;
        File.WriteAllText(_keyFile, JsonSerializer.Serialize(new { keyHash = Convert.ToHexString(_keyHash), created = _keyCreated }));
        lock (_gate) { _lastSeen = DateTimeOffset.MinValue; _hello = new(); _values = null; }
        return key;
    }

    public bool CheckKey(string? key)
    {
        if (_keyHash == null || string.IsNullOrEmpty(key)) return false;
        return CryptographicOperations.FixedTimeEquals(_keyHash, SHA256.HashData(Encoding.UTF8.GetBytes(key)));
    }

    public bool CheckRelayToken(string? token) =>
        token != null && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(token), Encoding.UTF8.GetBytes(RelayToken));

    // ---------- presence ----------

    public void Seen(LinkHello? hello = null)
    {
        lock (_gate)
        {
            _lastSeen = DateTimeOffset.Now;
            if (hello != null) _hello = hello;
        }
    }

    public bool Connected { get { lock (_gate) return DateTimeOffset.Now - _lastSeen < Timeout; } }
    public bool TiaReady { get { lock (_gate) return Connected && _hello.Tia; } }
    public string? Machine { get { lock (_gate) return Connected ? _hello.Machine : null; } }

    public object Status()
    {
        lock (_gate)
        {
            var v = _values;
            return new
            {
                hasKey = HasKey,
                keyCreated = _keyCreated,
                connected = Connected,
                lastSeen = _lastSeen == DateTimeOffset.MinValue ? (DateTimeOffset?)null : _lastSeen,
                machine = _hello.Machine,
                version = _hello.Version,
                tia = Connected && _hello.Tia,
                tiaMessage = _hello.TiaMessage,
                plc = v == null ? null : new { connected = v.Connected, address = v.Address, error = v.Error, time = v.Time, tags = v.Values?.Count ?? 0 },
            };
        }
    }

    // ---------- PLC values ----------

    /// <summary>The tags the model needs; Link reads exactly these.</summary>
    public IReadOnlyList<TagDef> Tags { get => _tags; set => _tags = value; }

    public void SetValues(LinkValues v)
    {
        lock (_gate) { _values = v; _valuesSeq++; }
        _valuesSignal.Release();
    }

    /// <summary>Waits for values newer than <paramref name="afterSeq"/>, up to <paramref name="wait"/>.</summary>
    public (LinkValues? Values, long Seq) WaitValues(long afterSeq, TimeSpan wait)
    {
        var until = DateTime.UtcNow + wait;
        while (true)
        {
            lock (_gate) if (_valuesSeq > afterSeq) return (_values, _valuesSeq);
            var left = until - DateTime.UtcNow;
            if (left <= TimeSpan.Zero || !_valuesSignal.Wait(left)) { lock (_gate) return (_valuesSeq > afterSeq ? _values : null, _valuesSeq); }
        }
    }

    // ---------- TIA relay ----------

    public void Enqueue(RelayRequest r)
    {
        _pending[r.Id] = r;
        _queue.Writer.TryWrite(r);
    }

    /// <summary>The next request for Link, or null after <paramref name="wait"/>. Requests whose caller gave up are skipped.</summary>
    public async Task<RelayRequest?> NextAsync(TimeSpan wait, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(wait);
        try
        {
            while (true)
            {
                var r = await _queue.Reader.ReadAsync(cts.Token);
                if (!r.Abandoned) return r;
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return null; }
    }

    public RelayRequest? Pending(string id) => _pending.GetValueOrDefault(id);
    public void Done(string id) => _pending.TryRemove(id, out _);
}

/// <summary>What Link says about itself when it connects (and every 25 s).</summary>
public sealed class LinkHello
{
    public string Machine { get; set; } = "";
    public string Version { get; set; } = "";
    /// <summary>True when Link runs the TIA helper on the laptop and it answers.</summary>
    public bool Tia { get; set; }
    public string? TiaMessage { get; set; }
}

/// <summary>One read of the PLC by Link. Values are already normalized (bool, number or null) by tag id.</summary>
public sealed class LinkValues
{
    public DateTimeOffset Time { get; set; }
    public bool Connected { get; set; }
    public string? Address { get; set; }
    public string? Error { get; set; }
    public Dictionary<string, JsonElement>? Values { get; set; }
    public Dictionary<string, string>? Errors { get; set; }
}

/// <summary>A call to the TIA helper waiting for Link to carry it.</summary>
public sealed class RelayRequest
{
    public string Id { get; } = Guid.NewGuid().ToString("N");
    public required string Method { get; init; }
    public required string PathAndQuery { get; init; }
    public string? ContentType { get; init; }
    /// <summary>The request body, saved to a temp file (archives can be large), or null.</summary>
    public string? BodyFile { get; init; }
    public TaskCompletionSource<RelayResponse> Response { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    /// <summary>Set by the caller once it copied the answer, so Link's upload of it can finish.</summary>
    public TaskCompletionSource Copied { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public volatile bool Abandoned;
}

public sealed record RelayResponse(int Status, string? ContentType, Stream Body);
