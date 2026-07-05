using System.Net;
using System.Net.Http.Json;
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
/// Issue #111 — service settings API. GET /admin/config returns the effective
/// node config with secrets redacted to presence + fingerprint; PUT /admin/config
/// applies only the live-editable subset and rejects restart-required fields.
/// </summary>
public sealed class ServiceConfigApiTests : IDisposable
{
    private readonly List<IDisposable> _disposables = new();
    private readonly List<string> _paths = new();
    private static readonly HttpClient _http = new();

    private (string baseUrl, DocumentForgeDb db) Boot(NodeConfig config)
    {
        var port = FreePort();
        var path = Path.Combine(Path.GetTempPath(), $"cfgapi_{Guid.NewGuid():N}.dfdb");
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
        ServeCommand.MapAdminEndpoints(app, db, config);
        app.StartAsync().GetAwaiter().GetResult();
        _disposables.Add(new Stopper(app));
        return ($"http://127.0.0.1:{port}", db);
    }

    private static int FreePort()
    {
        using var l = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        l.Start();
        var p = ((System.Net.IPEndPoint)l.LocalEndpoint).Port;
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

    private static NodeConfig SampleConfig() => new()
    {
        NodeName = "node-A",
        Port = 5000,
        DataDir = "/data/df",
        Security = new SecurityConfig
        {
            ApiKey = "super-secret-admin-key",
            ReplicationSecret = "repl-secret",
            ScopedKeys = new List<ScopedApiKey>
            {
                new() { Key = "tenant-key", Scopes = new List<string> { "db:acme" }, Description = "acme tenant" },
            },
        },
        Replication = new ReplicationConfig { Role = "leader", Port = 5500, MinSyncReplicas = 0 },
    };

    [Fact]
    public async Task GetConfig_RedactsSecrets_ButReportsPresenceAndFingerprint()
    {
        var (baseUrl, _) = Boot(SampleConfig());
        var resp = await _http.GetAsync($"{baseUrl}/admin/config");
        resp.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        var root = json.RootElement;

        var sec = root.GetProperty("security");
        Assert.True(sec.GetProperty("adminKeyConfigured").GetBoolean());
        var fp = sec.GetProperty("adminKeyFingerprint").GetString();
        Assert.StartsWith("sha256:", fp);
        // The raw secret must NEVER appear anywhere in the payload.
        var whole = root.GetRawText();
        Assert.DoesNotContain("super-secret-admin-key", whole);
        Assert.DoesNotContain("repl-secret", whole);
        Assert.DoesNotContain("tenant-key", whole);

        // Scoped keys are listed by fingerprint + description, not raw key.
        var scoped = sec.GetProperty("scopedKeys");
        Assert.Equal("acme tenant", scoped[0].GetProperty("description").GetString());
        Assert.StartsWith("sha256:", scoped[0].GetProperty("keyFingerprint").GetString());

        // Node identity + restart-required list are surfaced.
        Assert.Equal("node-A", root.GetProperty("node").GetProperty("nodeName").GetString());
        Assert.Contains("port", root.GetProperty("restartRequired").EnumerateArray().Select(e => e.GetString()));
    }

    [Fact]
    public async Task PutConfig_AppliesLiveSemiSyncKnobs_ToEngine()
    {
        var (baseUrl, db) = Boot(SampleConfig());
        var resp = await _http.PutAsJsonAsync($"{baseUrl}/admin/config",
            new { minSyncReplicas = 2, syncTimeoutSeconds = 12.5 });
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        Assert.Equal(2, db.MinSyncReplicas);
        Assert.Equal(TimeSpan.FromSeconds(12.5), db.SyncReplicationTimeout);

        // GET now reflects the live values.
        using var json = JsonDocument.Parse(await (await _http.GetAsync($"{baseUrl}/admin/config")).Content.ReadAsStringAsync());
        var repl = json.RootElement.GetProperty("replication");
        Assert.Equal(2, repl.GetProperty("minSyncReplicas").GetInt32());
        Assert.Equal(12.5, repl.GetProperty("syncTimeoutSeconds").GetDouble());
    }

    [Fact]
    public async Task PutConfig_RestartRequiredField_Rejected()
    {
        var (baseUrl, db) = Boot(SampleConfig());
        var resp = await _http.PutAsJsonAsync($"{baseUrl}/admin/config", new { port = 9999 });
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        using var json = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        Assert.Equal("restartRequired", json.RootElement.GetProperty("code").GetString());
        Assert.Equal("port", json.RootElement.GetProperty("field").GetString());
        Assert.Equal(0, db.MinSyncReplicas); // nothing applied
    }

    [Fact]
    public async Task PutConfig_NoEditableFields_Returns400()
    {
        var (baseUrl, _) = Boot(SampleConfig());
        var resp = await _http.PutAsJsonAsync($"{baseUrl}/admin/config", new { somethingUnknown = 1 });
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task PutConfig_NegativeMinSyncReplicas_Rejected()
    {
        var (baseUrl, db) = Boot(SampleConfig());
        var resp = await _http.PutAsJsonAsync($"{baseUrl}/admin/config", new { minSyncReplicas = -1 });
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Equal(0, db.MinSyncReplicas); // unchanged
    }

    [Fact]
    public async Task GetConfig_NoSecrets_ReportsNotConfigured()
    {
        var (baseUrl, _) = Boot(new NodeConfig { NodeName = "bare", InsecureDevMode = true });
        using var json = JsonDocument.Parse(await (await _http.GetAsync($"{baseUrl}/admin/config")).Content.ReadAsStringAsync());
        var sec = json.RootElement.GetProperty("security");
        Assert.False(sec.GetProperty("adminKeyConfigured").GetBoolean());
        Assert.Equal(JsonValueKind.Null, sec.GetProperty("adminKeyFingerprint").ValueKind);
    }
}
