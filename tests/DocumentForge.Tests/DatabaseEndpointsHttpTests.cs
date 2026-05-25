using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using DocumentForge.Cli.Auth;
using DocumentForge.Cli.Commands;
using DocumentForge.Engine;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;
using Xunit;

namespace DocumentForge.Tests;

/// <summary>
/// HTTP integration tests for Issue #66 Phase 2's <c>/databases</c> REST
/// surface. Boots a minimal Kestrel host wired up with the registry +
/// <see cref="DatabaseEndpoints.Map"/> and asserts the wire shape +
/// status codes that Studio (and any other client) rely on.
/// </summary>
public sealed class DatabaseEndpointsHttpTests : IDisposable
{
    private readonly List<IDisposable> _disposables = new();
    private readonly List<string> _dirs = new();
    private static readonly HttpClient _http = new();

    private (DatabaseRegistry registry, string baseUrl, string dataDir) BootServer()
    {
        var port = FindFreePort();
        var dataDir = Path.Combine(Path.GetTempPath(), $"dbep_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dataDir);
        _dirs.Add(dataDir);

        var registry = new DatabaseRegistry();
        _disposables.Add(registry);

        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls($"http://127.0.0.1:{port}");
        var app = builder.Build();
        // Issue #72: scope-enforcement requires an AuthContext on
        // HttpContext.Items. ServeCommand installs this; for the
        // bare-DatabaseEndpoints harness we drop in dev-mode auth so
        // the tests exercise endpoint behaviour, not authentication.
        // The actual auth flow is unit-tested in AuthContextTests.
        app.Use(async (ctx, next) =>
        {
            ctx.Items[AuthContext.ContextKey] = AuthContext.DevMode();
            await next();
        });
        DatabaseEndpoints.Map(app, registry, dataDir);

        app.StartAsync().GetAwaiter().GetResult();
        _disposables.Add(new HostStopper(app));

        return (registry, $"http://127.0.0.1:{port}", dataDir);
    }

    private static int FindFreePort()
    {
        using var listener = new TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    public void Dispose()
    {
        for (int i = _disposables.Count - 1; i >= 0; i--)
        {
            try { _disposables[i].Dispose(); } catch { }
        }
        foreach (var d in _dirs)
        {
            try { Directory.Delete(d, recursive: true); } catch { }
        }
    }

    // Helper used to bring Kestrel down cleanly in Dispose.
    private sealed class HostStopper : IDisposable
    {
        private readonly WebApplication _app;
        public HostStopper(WebApplication app) => _app = app;
        public void Dispose()
        {
            try
            {
                using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(2));
                _app.StopAsync(cts.Token).GetAwaiter().GetResult();
            }
            catch { }
            try { _app.DisposeAsync().AsTask().GetAwaiter().GetResult(); } catch { }
        }
    }

    [Fact]
    public async Task GetDatabases_EmptyRegistry_ReturnsZero()
    {
        var (_, baseUrl, _) = BootServer();
        var response = await _http.GetAsync($"{baseUrl}/databases");
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<DatabasesListResponse>();
        Assert.NotNull(body);
        Assert.Equal(0, body!.Count);
        Assert.Empty(body.Databases);
        Assert.Null(body.Default);
    }

    [Fact]
    public async Task PostDatabases_CreatesNewFile_InDefaultDataDir()
    {
        // Path-less POST → registry derives {dataDir}/acme.dfdb. This is
        // the Studio "+ Add Database" path — operators don't have to know
        // the on-disk layout.
        var (_, baseUrl, dataDir) = BootServer();

        var resp = await _http.PostAsJsonAsync($"{baseUrl}/databases", new { name = "acme" });
        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        var created = await resp.Content.ReadFromJsonAsync<DatabaseEntryResponse>();
        Assert.NotNull(created);
        Assert.Equal("acme", created!.Name);
        Assert.True(created.IsDefault);
        Assert.Equal(Path.Combine(dataDir, "acme.dfdb"), created.FilePath);
        Assert.True(File.Exists(created.FilePath));
    }

    [Fact]
    public async Task PostDatabases_ExplicitPath_Honoured()
    {
        var (_, baseUrl, dataDir) = BootServer();
        var explicitPath = Path.Combine(dataDir, "weird-name.dfdb");

        var resp = await _http.PostAsJsonAsync($"{baseUrl}/databases",
            new { name = "weird", path = explicitPath });
        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        var created = await resp.Content.ReadFromJsonAsync<DatabaseEntryResponse>();
        Assert.Equal(explicitPath, created!.FilePath);
        Assert.True(File.Exists(explicitPath));
    }

    [Fact]
    public async Task PostDatabases_DuplicateName_Returns409()
    {
        // Conflict is the natural status for "this resource already exists".
        // Studio uses it to render an "already exists" inline error instead
        // of a generic 500.
        var (_, baseUrl, _) = BootServer();
        await _http.PostAsJsonAsync($"{baseUrl}/databases", new { name = "acme" });

        var dup = await _http.PostAsJsonAsync($"{baseUrl}/databases", new { name = "acme" });
        Assert.Equal(HttpStatusCode.Conflict, dup.StatusCode);
    }

    [Fact]
    public async Task PostDatabases_InvalidName_Returns400()
    {
        var (_, baseUrl, _) = BootServer();
        var resp = await _http.PostAsJsonAsync($"{baseUrl}/databases",
            new { name = "1starts_with_digit" });
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task PostDatabases_MissingName_Returns400()
    {
        var (_, baseUrl, _) = BootServer();
        var resp = await _http.PostAsJsonAsync($"{baseUrl}/databases", new { name = "" });
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task GetDatabases_MultipleAttached_AllReturned()
    {
        var (_, baseUrl, _) = BootServer();
        await _http.PostAsJsonAsync($"{baseUrl}/databases", new { name = "alpha" });
        await _http.PostAsJsonAsync($"{baseUrl}/databases", new { name = "beta" });
        await _http.PostAsJsonAsync($"{baseUrl}/databases", new { name = "gamma" });

        var list = await _http.GetFromJsonAsync<DatabasesListResponse>($"{baseUrl}/databases");
        Assert.Equal(3, list!.Count);
        var names = list.Databases.Select(d => d.Name).OrderBy(n => n).ToList();
        Assert.Equal(new[] { "alpha", "beta", "gamma" }, names);
        // First attached is implicit default.
        Assert.Equal("alpha", list.Default);
    }

    [Fact]
    public async Task DeleteDatabase_DefaultsToDetach_FileKept()
    {
        var (registry, baseUrl, _) = BootServer();
        await _http.PostAsJsonAsync($"{baseUrl}/databases", new { name = "acme" });
        var info = registry.List().First();
        Assert.True(File.Exists(info.FilePath));

        var resp = await _http.DeleteAsync($"{baseUrl}/databases/acme");
        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadFromJsonAsync<DeleteDatabaseResponse>();
        Assert.Equal("detached", body!.Action);
        Assert.True(File.Exists(info.FilePath), "Detach must NOT delete the data file.");
    }

    [Fact]
    public async Task DeleteDatabase_DeleteFilesTrue_DropsEverything()
    {
        var (registry, baseUrl, _) = BootServer();
        await _http.PostAsJsonAsync($"{baseUrl}/databases", new { name = "doomed" });
        var info = registry.List().First();
        // Force WAL materialisation so the test proves sidecars are cleaned.
        registry.Get("doomed").Insert("orders", """{"pnr":"X"}""");

        var resp = await _http.DeleteAsync($"{baseUrl}/databases/doomed?deleteFiles=true");
        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadFromJsonAsync<DeleteDatabaseResponse>();
        Assert.Equal("dropped", body!.Action);
        Assert.False(File.Exists(info.FilePath));
        foreach (var ext in new[] { ".wal", ".recovery", ".lock", ".followerseq" })
            Assert.False(File.Exists(info.FilePath + ext));
    }

    [Fact]
    public async Task DeleteDatabase_Unknown_Returns404()
    {
        var (_, baseUrl, _) = BootServer();
        var resp = await _http.DeleteAsync($"{baseUrl}/databases/ghost");
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task SetDefault_SwitchesActiveDatabase()
    {
        var (_, baseUrl, _) = BootServer();
        await _http.PostAsJsonAsync($"{baseUrl}/databases", new { name = "alpha" });
        await _http.PostAsJsonAsync($"{baseUrl}/databases", new { name = "beta" });

        var resp = await _http.PostAsync($"{baseUrl}/databases/beta/set-default", content: null);
        resp.EnsureSuccessStatusCode();

        var list = await _http.GetFromJsonAsync<DatabasesListResponse>($"{baseUrl}/databases");
        Assert.Equal("beta", list!.Default);
        Assert.Equal(1, list.Databases.Count(d => d.IsDefault));
    }

    [Fact]
    public async Task SetDefault_UnknownName_Returns404()
    {
        var (_, baseUrl, _) = BootServer();
        await _http.PostAsJsonAsync($"{baseUrl}/databases", new { name = "alpha" });
        var resp = await _http.PostAsync($"{baseUrl}/databases/ghost/set-default", content: null);
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task ScopedReplication_AlphaLeadsBeta_InsertPropagates()
    {
        // The "swarm on one box" demo: two attached DBs in one service,
        // wire one as leader and the other as its follower over local TCP,
        // insert into the leader, observe in the follower. Pre-Phase 2.5
        // this required two `dfdb serve` processes; the scoped routes
        // make it intra-service.
        var (registry, baseUrl, _) = BootServer();

        // Phase 1: stand up alpha + beta.
        await _http.PostAsJsonAsync($"{baseUrl}/databases", new { name = "alpha" });
        await _http.PostAsJsonAsync($"{baseUrl}/databases", new { name = "beta" });

        // Phase 2: alpha leader on a free TCP port.
        var replPort = FindFreePort();
        var leaderResp = await _http.PostAsJsonAsync(
            $"{baseUrl}/db/alpha/replication/start-leader", new { port = replPort });
        leaderResp.EnsureSuccessStatusCode();

        // Phase 3: beta dials alpha. Same process — loopback only.
        var followerResp = await _http.PostAsJsonAsync(
            $"{baseUrl}/db/beta/replication/start-follower",
            new { host = "localhost", port = replPort });
        followerResp.EnsureSuccessStatusCode();

        // Phase 4: wait for handshake to complete. The TCP connect + snapshot
        // transfer is async; poll status until alpha sees its follower OR
        // we hit a generous deadline. The deadline guards against a
        // genuinely broken wiring rather than slow CI — handshake is
        // usually <100ms on loopback.
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            var status = await _http.GetFromJsonAsync<ReplicationStatusResponse>(
                $"{baseUrl}/db/alpha/replication/status");
            if (status?.Leader?.FollowerCount > 0) break;
            await Task.Delay(100);
        }

        // Phase 5: drive a write through alpha's engine directly (bypassing
        // the flat /collections route's set-default plumbing) and verify
        // beta sees it. We use the engine instances from the registry to
        // keep the test focused on replication, not on route routing.
        var alpha = registry.Get("alpha");
        var beta = registry.Get("beta");
        alpha.Insert("orders", """{"pnr":"REP-A1"}""");

        // Replication is async — wait for the op to land on beta.
        var seen = false;
        deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            var rows = beta.Execute("SELECT * FROM orders").Documents;
            if (rows.Count == 1 && rows[0]["pnr"].AsString == "REP-A1")
            {
                seen = true;
                break;
            }
            await Task.Delay(100);
        }
        Assert.True(seen, "Beta never received the replicated insert.");

        // Phase 6: status surface — alpha reports beta as a follower,
        // beta reports alpha as its leader endpoint.
        var alphaStatus = await _http.GetFromJsonAsync<ReplicationStatusResponse>(
            $"{baseUrl}/db/alpha/replication/status");
        var betaStatus = await _http.GetFromJsonAsync<ReplicationStatusResponse>(
            $"{baseUrl}/db/beta/replication/status");
        Assert.Equal("leader", alphaStatus!.Role);
        Assert.True(alphaStatus.Leader!.FollowerCount >= 1);
        Assert.Equal("follower", betaStatus!.Role);
        Assert.NotNull(betaStatus.Follower?.Leader);
        Assert.Equal($"localhost:{replPort}", betaStatus.Follower!.Leader!.Endpoint);
    }

    [Fact]
    public async Task ScopedReplication_UnknownDatabase_Returns404()
    {
        var (_, baseUrl, _) = BootServer();
        // No DB called "ghost" — every scoped verb must 404, not 500.
        var statusResp = await _http.GetAsync($"{baseUrl}/db/ghost/replication/status");
        Assert.Equal(HttpStatusCode.NotFound, statusResp.StatusCode);

        var leaderResp = await _http.PostAsJsonAsync(
            $"{baseUrl}/db/ghost/replication/start-leader", new { port = 5500 });
        Assert.Equal(HttpStatusCode.NotFound, leaderResp.StatusCode);

        var followerResp = await _http.PostAsJsonAsync(
            $"{baseUrl}/db/ghost/replication/start-follower",
            new { host = "localhost", port = 5500 });
        Assert.Equal(HttpStatusCode.NotFound, followerResp.StatusCode);
    }

    [Fact]
    public async Task SwarmScenario_AttachThreeDropOne_StateConsistent()
    {
        // The user-facing point of Phase 2: "create a swarm on one box."
        // Drive the registry through the exact sequence Studio would.
        var (_, baseUrl, _) = BootServer();

        await _http.PostAsJsonAsync($"{baseUrl}/databases", new { name = "tenant_a" });
        await _http.PostAsJsonAsync($"{baseUrl}/databases", new { name = "tenant_b" });
        await _http.PostAsJsonAsync($"{baseUrl}/databases", new { name = "tenant_c" });

        var afterAttach = await _http.GetFromJsonAsync<DatabasesListResponse>($"{baseUrl}/databases");
        Assert.Equal(3, afterAttach!.Count);

        // Switch active to tenant_b.
        await _http.PostAsync($"{baseUrl}/databases/tenant_b/set-default", content: null);

        // Drop tenant_a.
        var drop = await _http.DeleteAsync($"{baseUrl}/databases/tenant_a?deleteFiles=true");
        drop.EnsureSuccessStatusCode();

        var final = await _http.GetFromJsonAsync<DatabasesListResponse>($"{baseUrl}/databases");
        Assert.Equal(2, final!.Count);
        Assert.Equal("tenant_b", final.Default);
        Assert.DoesNotContain(final.Databases, d => d.Name == "tenant_a");
    }

    // Issue #83 — /databases/unattached. The endpoint that powers the
    // Studio "Browse & attach" panel.
    [Fact]
    public async Task GetUnattached_DropOrphanFiles_ListsThem()
    {
        var (_, baseUrl, dataDir) = BootServer();

        // Drop a .dfdb file directly in data-dir, untouched by the registry.
        var orphan = Path.Combine(dataDir, "orphan_tenant.dfdb");
        File.WriteAllBytes(orphan, new byte[] { 0, 0, 0, 0 });

        var list = await _http.GetFromJsonAsync<UnattachedResponse>(
            $"{baseUrl}/databases/unattached");
        Assert.NotNull(list);
        Assert.Equal(1, list!.Count);
        Assert.False(list.Recursive);
        Assert.Equal(Path.GetFullPath(orphan), list.Files[0].Path);
        Assert.Equal("orphan_tenant", list.Files[0].SuggestedName);
        Assert.False(list.Files[0].NameConflict);
    }

    [Fact]
    public async Task GetUnattached_AttachedFile_Excluded()
    {
        var (_, baseUrl, dataDir) = BootServer();
        await _http.PostAsJsonAsync($"{baseUrl}/databases", new { name = "live" });
        // Drop a separate orphan so the response isn't empty.
        File.WriteAllBytes(Path.Combine(dataDir, "ghost.dfdb"), new byte[] { 0 });

        var list = await _http.GetFromJsonAsync<UnattachedResponse>(
            $"{baseUrl}/databases/unattached");
        Assert.Equal(1, list!.Count);
        Assert.Equal("ghost", list.Files[0].SuggestedName);
        Assert.DoesNotContain(list.Files, f => f.SuggestedName == "live");
    }

    [Fact]
    public async Task GetUnattached_SkipsImplicitFiles()
    {
        // data.dfdb and _system.dfdb (had they been present) would be
        // service implementation details — they must never appear as
        // "unattached" candidates the operator could click.
        var (_, baseUrl, dataDir) = BootServer();
        File.WriteAllBytes(Path.Combine(dataDir, "data.dfdb"), new byte[] { 0 });
        File.WriteAllBytes(Path.Combine(dataDir, "_system.dfdb"), new byte[] { 0 });
        File.WriteAllBytes(Path.Combine(dataDir, "tenant_real.dfdb"), new byte[] { 0 });

        var list = await _http.GetFromJsonAsync<UnattachedResponse>(
            $"{baseUrl}/databases/unattached");
        Assert.Equal(1, list!.Count);
        Assert.Equal("tenant_real", list.Files[0].SuggestedName);
    }

    [Fact]
    public async Task GetUnattached_RecursiveFlag_FindsSubfolderFiles()
    {
        // Container deployments often mount a volume containing nested
        // folders; the recursive flag lets the operator surface those
        // without manually typing paths.
        var (_, baseUrl, dataDir) = BootServer();
        var subdir = Path.Combine(dataDir, "_backup");
        Directory.CreateDirectory(subdir);
        File.WriteAllBytes(Path.Combine(subdir, "archived.dfdb"), new byte[] { 0 });

        var nonRecursive = await _http.GetFromJsonAsync<UnattachedResponse>(
            $"{baseUrl}/databases/unattached");
        Assert.Equal(0, nonRecursive!.Count);

        var recursive = await _http.GetFromJsonAsync<UnattachedResponse>(
            $"{baseUrl}/databases/unattached?recursive=true");
        Assert.Equal(1, recursive!.Count);
        Assert.True(recursive.Recursive);
        Assert.Equal("archived", recursive.Files[0].SuggestedName);
    }

    [Fact]
    public async Task GetUnattached_NameConflictFlag_SurfacedOnDuplicate()
    {
        // If an orphan file's basename matches a name already in the
        // registry, the UI needs to know — otherwise one-click attach
        // would 409. Flag it so the panel can prompt for a rename.
        var (_, baseUrl, dataDir) = BootServer();
        await _http.PostAsJsonAsync($"{baseUrl}/databases", new { name = "shared" });
        // Drop a file at a different path with the SAME basename.
        var other = Path.Combine(dataDir, "_archive", "shared.dfdb");
        Directory.CreateDirectory(Path.GetDirectoryName(other)!);
        File.WriteAllBytes(other, new byte[] { 0 });

        var list = await _http.GetFromJsonAsync<UnattachedResponse>(
            $"{baseUrl}/databases/unattached?recursive=true");
        var conflictRow = list!.Files.Single(f => f.Path == Path.GetFullPath(other));
        Assert.True(conflictRow.NameConflict);
    }

    // Response DTOs — narrow shapes for deserialization in tests.
    private sealed class UnattachedResponse
    {
        public string DataDir { get; set; } = "";
        public bool Recursive { get; set; }
        public int Count { get; set; }
        public List<UnattachedRow> Files { get; set; } = new();
    }
    private sealed class UnattachedRow
    {
        public string SuggestedName { get; set; } = "";
        public bool NameConflict { get; set; }
        public string Path { get; set; } = "";
        public long SizeBytes { get; set; }
        public string ModifiedUtc { get; set; } = "";
    }

    private sealed class DatabasesListResponse
    {
        public string? Default { get; set; }
        public int Count { get; set; }
        public List<DatabaseEntryResponse> Databases { get; set; } = new();
    }
    private sealed class DatabaseEntryResponse
    {
        public string Name { get; set; } = "";
        public string FilePath { get; set; } = "";
        public bool IsDefault { get; set; }
    }
    private sealed class DeleteDatabaseResponse
    {
        public string Name { get; set; } = "";
        public string Action { get; set; } = "";
        public string? DefaultAfter { get; set; }
    }
    private sealed class ReplicationStatusResponse
    {
        public string Database { get; set; } = "";
        public string Role { get; set; } = "";
        public ReplicationLeaderInfo? Leader { get; set; }
        public ReplicationFollowerInfo? Follower { get; set; }
    }
    private sealed class ReplicationLeaderInfo
    {
        public ulong CurrentSeq { get; set; }
        public int FollowerCount { get; set; }
    }
    private sealed class ReplicationFollowerInfo
    {
        public ulong LastAppliedSeq { get; set; }
        public ReplicationLeaderEndpoint? Leader { get; set; }
    }
    private sealed class ReplicationLeaderEndpoint
    {
        public string Endpoint { get; set; } = "";
    }
}
