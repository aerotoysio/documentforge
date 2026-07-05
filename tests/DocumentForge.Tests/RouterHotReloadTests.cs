using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using DocumentForge.Cli.Commands;
using DocumentForge.Cli.Router;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Xunit;

namespace DocumentForge.Tests;

/// <summary>
/// Issue #134 — PUT /cluster/config hot-reload + router admin auth.
/// </summary>
public sealed class RouterHotReloadTests : IDisposable
{
    private readonly List<IDisposable> _disposables = new();
    private readonly List<string> _files = new();
    private static readonly HttpClient _http = new();

    private const string ThreeShards = """
        {
          "shards": [
            { "name": "ring-a", "leader": "http://localhost:5001" },
            { "name": "ring-b", "leader": "http://localhost:5002" },
            { "name": "ring-c", "leader": "http://localhost:5003" }
          ],
          "collections": [ { "name": "orders", "strategy": "hash", "shardKeyPath": "pnr" } ]
        }
        """;

    private const string TwoShards = """
        {
          "shards": [
            { "name": "ring-a", "leader": "http://localhost:5001" },
            { "name": "ring-b", "leader": "http://localhost:5002" }
          ],
          "collections": [ { "name": "orders", "strategy": "hash", "shardKeyPath": "pnr" } ]
        }
        """;

    // ---------------------------------------------------------------- unit

    [Fact]
    public void ApplyConfig_BumpsVersion_Monotonically()
    {
        using var router = new ClusterRouter(ClusterConfig.FromJson(ThreeShards));
        Assert.Equal(0, router.Config.Version);
        Assert.Equal(1, router.ApplyConfig(ClusterConfig.FromJson(TwoShards)));
        Assert.Equal(2, router.ApplyConfig(ClusterConfig.FromJson(ThreeShards)));
        Assert.Equal(2, router.Config.Version);
    }

    [Fact]
    public void ApplyConfig_SwapsRingSet_Live()
    {
        using var router = new ClusterRouter(ClusterConfig.FromJson(ThreeShards));
        Assert.Equal(3, router.Rings.Count);

        router.ApplyConfig(ClusterConfig.FromJson(TwoShards));
        Assert.Equal(2, router.Rings.Count);
        Assert.False(router.Rings.ContainsKey("ring-c"));

        // Every key now routes to one of the two remaining shards.
        for (int i = 0; i < 500; i++)
            Assert.Contains(router.PickShardNameForKey($"PNR-{i:D5}"), new[] { "ring-a", "ring-b" });
    }

    [Fact]
    public void ApplyConfig_ReusesUnchangedRingClients()
    {
        using var router = new ClusterRouter(ClusterConfig.FromJson(ThreeShards));
        var ringABefore = router.Rings["ring-a"];

        router.ApplyConfig(ClusterConfig.FromJson(TwoShards)); // ring-a endpoint unchanged
        Assert.Same(ringABefore, router.Rings["ring-a"]); // same HttpClient reused, not churned
    }

    [Fact]
    public void ApplyConfig_InvalidConfig_Throws_AndLeavesLiveStateIntact()
    {
        using var router = new ClusterRouter(ClusterConfig.FromJson(ThreeShards));
        // An empty-shards config is structurally invalid.
        Assert.Throws<InvalidOperationException>(() => router.ApplyConfig(new ClusterConfig()));
        Assert.Equal(0, router.Config.Version);   // unchanged
        Assert.Equal(3, router.Rings.Count);      // ring untouched
    }

    // ---------------------------------------------------------------- http

    private (string baseUrl, string configPath) BootRouter(string initialJson, string? routerKey)
    {
        var port = FreePort();
        var configPath = Path.Combine(Path.GetTempPath(), $"cluster_{Guid.NewGuid():N}.json");
        File.WriteAllText(configPath, initialJson);
        _files.Add(configPath);

        var router = new ClusterRouter(ClusterConfig.FromJson(initialJson));
        _disposables.Add(router);

        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls($"http://127.0.0.1:{port}");
        var app = builder.Build();
        RouterEndpoints.Map(app, router, configPath, routerKey);
        app.StartAsync().GetAwaiter().GetResult();
        _disposables.Add(new Stopper(app));
        return ($"http://127.0.0.1:{port}", configPath);
    }

    private static async Task<HttpResponseMessage> Put(string url, string json, string? bearer)
    {
        var req = new HttpRequestMessage(HttpMethod.Put, url)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
        if (bearer is not null)
            req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", bearer);
        return await _http.SendAsync(req);
    }

    [Fact]
    public async Task Put_WithKey_RequiresBearer_ThenAppliesAndPersists()
    {
        var (baseUrl, configPath) = BootRouter(ThreeShards, routerKey: "s3cr3t");

        // No bearer → 401.
        var unauth = await Put($"{baseUrl}/cluster/config", TwoShards, bearer: null);
        Assert.Equal(HttpStatusCode.Unauthorized, unauth.StatusCode);

        // Correct bearer → 200, version bumps to 1.
        var ok = await Put($"{baseUrl}/cluster/config", TwoShards, bearer: "s3cr3t");
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        using var body = JsonDocument.Parse(await ok.Content.ReadAsStringAsync());
        Assert.True(body.RootElement.GetProperty("applied").GetBoolean());
        Assert.Equal(1, body.RootElement.GetProperty("version").GetInt32());
        Assert.Equal(2, body.RootElement.GetProperty("shards").GetInt32());
        Assert.True(body.RootElement.GetProperty("persisted").GetBoolean());

        // Persisted to disk with the bumped version.
        var onDisk = ClusterConfig.FromJson(File.ReadAllText(configPath));
        Assert.Equal(1, onDisk.Version);
        Assert.Equal(2, onDisk.Shards.Count);

        // GET (with bearer) reflects the live swap.
        var getReq = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl}/cluster/config");
        getReq.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "s3cr3t");
        var live = ClusterConfig.FromJson(await (await _http.SendAsync(getReq)).Content.ReadAsStringAsync());
        Assert.Equal(2, live.Shards.Count);
    }

    [Fact]
    public async Task Get_ClusterConfig_WithKey_RequiresBearer()
    {
        var (baseUrl, _) = BootRouter(ThreeShards, routerKey: "k");
        var resp = await _http.GetAsync($"{baseUrl}/cluster/config");
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task Put_InvalidConfig_Returns400()
    {
        var (baseUrl, _) = BootRouter(ThreeShards, routerKey: "k");
        var resp = await Put($"{baseUrl}/cluster/config", """{ "shards": [] }""", bearer: "k");
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Put_NoKey_LoopbackAllowed()
    {
        // With no router key configured, a loopback client (the test) may PUT.
        var (baseUrl, _) = BootRouter(ThreeShards, routerKey: null);
        var resp = await Put($"{baseUrl}/cluster/config", TwoShards, bearer: null);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }

    [Fact]
    public async Task Get_ClusterConfig_NoKey_StaysOpen()
    {
        var (baseUrl, _) = BootRouter(ThreeShards, routerKey: null);
        var resp = await _http.GetAsync($"{baseUrl}/cluster/config");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }

    // ---------------------------------------------------------------- infra

    private static int FreePort()
    {
        using var l = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var p = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return p;
    }

    public void Dispose()
    {
        for (int i = _disposables.Count - 1; i >= 0; i--)
            try { _disposables[i].Dispose(); } catch { }
        foreach (var f in _files)
            try { File.Delete(f); } catch { }
    }

    private sealed class Stopper : IDisposable
    {
        private readonly WebApplication _app;
        public Stopper(WebApplication app) => _app = app;
        public void Dispose()
        {
            try { using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2)); _app.StopAsync(cts.Token).GetAwaiter().GetResult(); } catch { }
            try { _app.DisposeAsync().AsTask().GetAwaiter().GetResult(); } catch { }
        }
    }
}
