using System.Net;
using System.Text;
using System.Text.Json;
using DocumentForge.Cli;
using DocumentForge.Cli.Auth;
using DocumentForge.Cli.Commands;
using DocumentForge.Engine;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Xunit;

namespace DocumentForge.Tests;

/// <summary>
/// Issue #113 — cluster-config distribution. PUT /admin/cluster-config
/// validates and persists a pushed cluster.json to {dataDir}/cluster.json;
/// GET returns it verbatim; a push with an older version than the stored
/// config is rejected with 409.
/// </summary>
public sealed class ClusterConfigPushApiTests : IDisposable
{
    private readonly List<IDisposable> _disposables = new();
    private readonly List<string> _paths = new();
    private readonly string _dataDir;
    private static readonly HttpClient _http = new();

    public ClusterConfigPushApiTests()
    {
        _dataDir = Path.Combine(Path.GetTempPath(), $"clustercfg_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dataDir);
    }

    private string Boot()
    {
        var port = FreePort();
        var path = Path.Combine(Path.GetTempPath(), $"clustercfg_{Guid.NewGuid():N}.dfdb");
        _paths.Add(path);
        var db = DocumentForgeDb.Create(path);
        _disposables.Add(db);

        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls($"http://127.0.0.1:{port}");
        var app = builder.Build();
        app.Use(async (ctx, next) =>
        {
            ctx.Items[AuthContext.ContextKey] = AuthContext.DevMode();
            await next();
        });
        ServeCommand.MapAdminEndpoints(app, db, new NodeConfig { NodeName = "n1", DataDir = _dataDir });
        app.StartAsync().GetAwaiter().GetResult();
        _disposables.Add(new Stopper(app));
        return $"http://127.0.0.1:{port}";
    }

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
        foreach (var p in _paths)
            foreach (var ext in new[] { "", ".wal", ".recovery", ".lock", ".term" })
                try { File.Delete(p + ext); } catch { }
        try { Directory.Delete(_dataDir, recursive: true); } catch { }
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

    private static StringContent Body(string json) => new(json, Encoding.UTF8, "application/json");

    // The engine cluster-config format is PascalCase (ClusterConfig.ToJson
    // uses no naming policy, and FromJson is case-SENSITIVE) — the same
    // bytes `dfdb cluster` verbs and Studio's cluster editor write.
    private const string ValidConfigV2 =
        """
        {
          "Version": 2,
          "Shards": [ { "Name": "ring-a", "Endpoint": "http://localhost:5001" } ],
          "Collections": { "orders": { "Strategy": "Hash", "ShardKeyPath": "pnr" } },
          "VirtualNodesPerShard": 150
        }
        """;

    [Fact]
    public async Task Get_Before_Any_Push_Is_404()
    {
        var baseUrl = Boot();
        var resp = await _http.GetAsync($"{baseUrl}/admin/cluster-config");
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task Put_Persists_And_Get_Returns_Verbatim()
    {
        var baseUrl = Boot();

        var put = await _http.PutAsync($"{baseUrl}/admin/cluster-config", Body(ValidConfigV2));
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        using var putDoc = JsonDocument.Parse(await put.Content.ReadAsStringAsync());
        Assert.True(putDoc.RootElement.GetProperty("saved").GetBoolean());
        Assert.Equal(2, putDoc.RootElement.GetProperty("version").GetInt32());

        Assert.True(File.Exists(Path.Combine(_dataDir, "cluster.json")));

        var get = await _http.GetAsync($"{baseUrl}/admin/cluster-config");
        Assert.Equal(HttpStatusCode.OK, get.StatusCode);
        Assert.Equal(ValidConfigV2, await get.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Stale_Version_Push_Is_Rejected_With_409()
    {
        var baseUrl = Boot();
        await _http.PutAsync($"{baseUrl}/admin/cluster-config", Body(ValidConfigV2));

        var stale = ValidConfigV2.Replace("\"Version\": 2", "\"Version\": 1");
        var resp = await _http.PutAsync($"{baseUrl}/admin/cluster-config", Body(stale));

        Assert.Equal(HttpStatusCode.Conflict, resp.StatusCode);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        Assert.Equal("staleVersion", doc.RootElement.GetProperty("code").GetString());
        Assert.Equal(2, doc.RootElement.GetProperty("storedVersion").GetInt32());
        Assert.Equal(1, doc.RootElement.GetProperty("pushedVersion").GetInt32());

        // The stored file is untouched.
        var get = await _http.GetAsync($"{baseUrl}/admin/cluster-config");
        Assert.Equal(ValidConfigV2, await get.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Same_Version_RePush_Is_Idempotent_And_Accepted()
    {
        var baseUrl = Boot();
        await _http.PutAsync($"{baseUrl}/admin/cluster-config", Body(ValidConfigV2));
        var resp = await _http.PutAsync($"{baseUrl}/admin/cluster-config", Body(ValidConfigV2));
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("""{ "Version": 3, "Shards": [ { "Name": "", "Endpoint": "http://x" } ] }""")]
    [InlineData("""{ "Version": 3, "Shards": [ { "Name": "a", "Endpoint": "" } ] }""")]
    [InlineData("""{ "Version": 3, "Shards": [], "Collections": { "o": { "Strategy": "Hash" } } }""")]
    public async Task Invalid_Configs_Are_Rejected_With_400(string body)
    {
        var baseUrl = Boot();
        var resp = await _http.PutAsync($"{baseUrl}/admin/cluster-config", Body(body));
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.False(File.Exists(Path.Combine(_dataDir, "cluster.json")));
    }
}
