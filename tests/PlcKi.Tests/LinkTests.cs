using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using PlcKi.Core.Model;
using PlcKi.Server.Link;
using PlcKi.Server.Live;
using Xunit;

/// <summary>PLC-Ki Link on the server side: the key, the PLC values, and the TIA relay with large bodies both ways.</summary>
public class LinkTests : IAsyncLifetime
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "plcki-link-" + Guid.NewGuid().ToString("N"));
    private readonly LinkHub _hub = new();
    private WebApplication _app = null!;
    private string _base = "";
    private string _key = "";

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_dir);
        _hub.Load(_dir);
        _key = _hub.NewKey();
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddSingleton(_hub);
        builder.Services.AddSingleton(new PlcKiOptions { Plc = new PlcOptions { Address = "10.0.0.5", Via = "link" }, Tia = new TiaOptions { Mode = "link" } });
        _app = builder.Build();
        _app.MapLink();
        await _app.StartAsync();
        _base = _app.Urls.First();
        _hub.LocalBase = _base;
    }

    public async Task DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
        try { Directory.Delete(_dir, true); } catch { /* temp */ }
    }

    private HttpClient LinkClient(string? key = null)
    {
        var c = new HttpClient { BaseAddress = new Uri(_base + "/") };
        c.DefaultRequestHeaders.Add(LinkEndpoints.KeyHeader, key ?? _key);
        return c;
    }

    [Fact]
    public async Task Wrong_key_is_refused_and_a_new_key_replaces_the_old()
    {
        using var bad = LinkClient("plk_wrong");
        var r = await bad.PostAsJsonAsync("link/hello", new LinkHello { Machine = "x" });
        Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode);
        Assert.False(_hub.Connected);

        var old = _key;
        _key = _hub.NewKey();
        Assert.False(_hub.CheckKey(old));
        Assert.True(_hub.CheckKey(_key));
    }

    [Fact]
    public async Task Hello_returns_the_tags_and_values_reach_the_source()
    {
        var tags = new List<TagDef> { new() { Id = "Level", Symbol = "\"DB\".Level", Type = "Real" }, new() { Id = "Run", Symbol = "\"DB\".Run", Type = "Bool" } };
        var source = new LinkPlcSource(_hub, new PlcOptions { Address = "10.0.0.5", PollMs = 200 });
        source.Connect(tags);

        using var link = LinkClient();
        var hello = await (await link.PostAsJsonAsync("link/hello", new LinkHello { Machine = "LAPTOP", Tia = true })).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("10.0.0.5", hello.GetProperty("plc").GetProperty("address").GetString());
        Assert.Equal(2, hello.GetProperty("plc").GetProperty("tags").GetArrayLength());
        Assert.True(_hub.Connected);
        Assert.True(_hub.TiaReady);

        var read = Task.Run(source.Read);
        await link.PostAsJsonAsync("link/values", new
        {
            time = DateTimeOffset.Now, connected = true, address = "10.0.0.5",
            values = new Dictionary<string, object?> { ["Level"] = 42.5, ["Run"] = true },
        });
        var snap = await read;
        Assert.Equal(42.5, snap.Values["Level"]);
        Assert.Equal(true, snap.Values["Run"]);
        Assert.Contains("LAPTOP", source.Address);

        // The laptop cannot reach the PLC: the source says so instead of showing old values
        var fail = Task.Run(source.Read);
        await link.PostAsJsonAsync("link/values", new { time = DateTimeOffset.Now, connected = false, error = "no answer from 10.0.0.5" });
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => fail);
        Assert.Contains("no answer", ex.Message);
    }

    [Fact]
    public async Task Relay_carries_large_bodies_both_ways_and_checks_the_token()
    {
        using var link = LinkClient();
        await link.PostAsJsonAsync("link/hello", new LinkHello { Machine = "LAPTOP", Tia = true });

        using var caller = new HttpClient { BaseAddress = new Uri(_hub.RelayUrl! + "/") };
        Assert.Equal(HttpStatusCode.Unauthorized, (await caller.GetAsync("status")).StatusCode);
        caller.DefaultRequestHeaders.Add("X-PlcKi-Token", _hub.RelayToken);

        // A mini Link: picks up one call and answers with the body it got, reversed
        var carry = Task.Run(async () =>
        {
            using var resp = await link.GetAsync("link/next");
            var req = await resp.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("PUT", req.GetProperty("method").GetString());
            Assert.Equal("/archives/big.zap20?x=1", req.GetProperty("path").GetString());
            var body = await link.GetByteArrayAsync($"link/req/{req.GetProperty("id").GetString()}/body");
            Array.Reverse(body);
            using var answer = new HttpRequestMessage(HttpMethod.Post, $"link/req/{req.GetProperty("id").GetString()}/response") { Content = new ByteArrayContent(body) };
            answer.Content.Headers.ContentType = new("application/zip");
            answer.Headers.Add("X-Status", "201");
            (await link.SendAsync(answer)).EnsureSuccessStatusCode();
        });

        var data = new byte[40 * 1024 * 1024];      // above Kestrel's default 30 MB body limit
        new Random(1).NextBytes(data);
        using var put = await caller.PutAsync("archives/big.zap20?x=1", new ByteArrayContent(data));
        Assert.Equal(HttpStatusCode.Created, put.StatusCode);
        var back = await put.Content.ReadAsByteArrayAsync();
        await carry;
        Array.Reverse(back);
        Assert.Equal(data, back);
    }
}
