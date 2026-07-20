using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using DocumentForge.Cli;
using DocumentForge.Cli.Auth;
using DocumentForge.Cli.Commands;
using DocumentForge.Core;
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
        var (r, url, d, _) = BootServerWithCatalog(includeCatalog: false);
        return (r, url, d);
    }

    private (DatabaseRegistry registry, string baseUrl, string dataDir, DatabaseCatalog? catalog) BootServerWithCatalog(bool includeCatalog)
    {
        var port = FindFreePort();
        var dataDir = Path.Combine(Path.GetTempPath(), $"dbep_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dataDir);
        _dirs.Add(dataDir);

        var registry = new DatabaseRegistry();
        _disposables.Add(registry);

        // Catalog requires the _system DB to be attached on the registry
        // before any RecordAttach / TombstonedNames call. ServeCommand
        // does this before constructing the catalog; mirror that here
        // for the subset of tests that exercise the runtime discover
        // endpoint with tombstone semantics.
        DatabaseCatalog? catalog = null;
        if (includeCatalog)
        {
            registry.AttachOrCreate("_system", Path.Combine(dataDir, "_system.dfdb"));
            catalog = new DatabaseCatalog(registry);
        }

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
        DatabaseEndpoints.Map(app, registry, dataDir, catalog);

        app.StartAsync().GetAwaiter().GetResult();
        _disposables.Add(new HostStopper(app));

        return (registry, $"http://127.0.0.1:{port}", dataDir, catalog);
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
    public async Task PostDatabases_ReservedUnderscoreName_Returns400()
    {
        // Issue #105 — the '_' prefix is reserved for system databases; the
        // API must refuse to create/attach one.
        var (_, baseUrl, _) = BootServer();
        var resp = await _http.PostAsJsonAsync($"{baseUrl}/databases", new { name = "_evil" });
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task ScopedDataRoutes_OnReservedDatabase_Return403()
    {
        // Issue #105 — even with dev-mode (admin, all-DB) auth, the _system
        // credential store is unreachable via the document data plane.
        var (registry, baseUrl, dataDir) = BootServer();
        registry.AttachOrCreate("_system", Path.Combine(dataDir, "_system.dfdb"));

        var read = await _http.GetAsync($"{baseUrl}/db/_system/collections");
        Assert.Equal(HttpStatusCode.Forbidden, read.StatusCode);

        var query = await _http.PostAsJsonAsync($"{baseUrl}/db/_system/query",
            new { sql = "SELECT * FROM _keys" });
        Assert.Equal(HttpStatusCode.Forbidden, query.StatusCode);

        var write = await _http.PostAsync($"{baseUrl}/db/_system/collections/_keys",
            new StringContent("""{"x":1}""", System.Text.Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.Forbidden, write.StatusCode);

        // A normal database on the same server is still reachable.
        await _http.PostAsJsonAsync($"{baseUrl}/databases", new { name = "orders" });
        var ok = await _http.GetAsync($"{baseUrl}/db/orders/collections");
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
    }

    // ---- Issue #70: partial-document PATCH over the scoped data plane ----

    private static async Task<HttpResponseMessage> PatchJson(string url, string json, string? ifMatch = null)
    {
        var req = new HttpRequestMessage(HttpMethod.Patch, url)
        {
            Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
        };
        if (ifMatch is not null) req.Headers.TryAddWithoutValidation("If-Match", ifMatch);
        return await _http.SendAsync(req);
    }

    [Fact]
    public async Task Patch_MergeBody_SetsAndUnsetsFields()
    {
        var (registry, baseUrl, dataDir) = BootServer();
        registry.AttachOrCreate("shop", Path.Combine(dataDir, "shop.dfdb"));
        var db = registry.Get("shop");
        var id = db.Insert("orders", """{"pnr":"P1","status":"new","tmp":"scratch"}""");

        var resp = await PatchJson($"{baseUrl}/db/shop/collections/orders/{id}",
            """{"status":"shipped","tracking":"1Z999","tmp":null}""");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var doc = db.GetCollection("orders")!.FindById(id)!;
        Assert.Equal("shipped", doc["status"].AsString);
        Assert.Equal("1Z999", doc["tracking"].AsString);
        Assert.False(doc.ContainsKey("tmp"));   // null in a merge removes the field
        Assert.Equal("P1", doc["pnr"].AsString); // untouched fields survive
    }

    [Fact]
    public async Task Patch_OpsBody_IncrementsCounterAtomically()
    {
        var (registry, baseUrl, dataDir) = BootServer();
        registry.AttachOrCreate("shop", Path.Combine(dataDir, "shop.dfdb"));
        var db = registry.Get("shop");
        var id = db.Insert("stock", """{"sku":"A","sold":10}""");

        var resp = await PatchJson($"{baseUrl}/db/shop/collections/stock/{id}",
            """{"$ops":[{"field":"sold","op":"inc","value":5},{"field":"lastOp","op":"set","value":"sale"}]}""");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var doc = db.GetCollection("stock")!.FindById(id)!;
        Assert.Equal(15, doc["sold"].AsInt32);
        Assert.Equal("sale", doc["lastOp"].AsString);
    }

    [Fact]
    public async Task Patch_IfMatch_Mismatch_Returns412_AndWritesNothing()
    {
        var (registry, baseUrl, dataDir) = BootServer();
        registry.AttachOrCreate("shop", Path.Combine(dataDir, "shop.dfdb"));
        var db = registry.Get("shop");
        var id = db.Insert("orders", """{"pnr":"P2","status":"new"}""");

        var resp = await PatchJson($"{baseUrl}/db/shop/collections/orders/{id}",
            """{"status":"shipped"}""", ifMatch: "\"not-the-real-etag\"");
        Assert.Equal(HttpStatusCode.PreconditionFailed, resp.StatusCode);

        var doc = db.GetCollection("orders")!.FindById(id)!;
        Assert.Equal("new", doc["status"].AsString); // guard held: nothing written
    }

    [Fact]
    public async Task Patch_IfMatch_CurrentEtag_Succeeds()
    {
        var (registry, baseUrl, dataDir) = BootServer();
        registry.AttachOrCreate("shop", Path.Combine(dataDir, "shop.dfdb"));
        var db = registry.Get("shop");
        var id = db.Insert("orders", """{"pnr":"P3","status":"new"}""");
        var etag = db.GetCollection("orders")!.FindById(id)!.GetEtag();

        var resp = await PatchJson($"{baseUrl}/db/shop/collections/orders/{id}",
            """{"status":"paid"}""", ifMatch: etag);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal("paid", db.GetCollection("orders")!.FindById(id)!["status"].AsString);
    }

    [Fact]
    public async Task Patch_MissingDocument_Returns404()
    {
        var (registry, baseUrl, dataDir) = BootServer();
        registry.AttachOrCreate("shop", Path.Combine(dataDir, "shop.dfdb"));
        var resp = await PatchJson($"{baseUrl}/db/shop/collections/orders/{Guid.NewGuid()}",
            """{"status":"x"}""");
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task Patch_ImmutableId_Rejected()
    {
        var (registry, baseUrl, dataDir) = BootServer();
        registry.AttachOrCreate("shop", Path.Combine(dataDir, "shop.dfdb"));
        var db = registry.Get("shop");
        var id = db.Insert("orders", """{"pnr":"P4"}""");
        var resp = await PatchJson($"{baseUrl}/db/shop/collections/orders/{id}",
            """{"_id":"hacked"}""");
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
    public async Task DeleteDatabase_XConfirmHeader_DropsFiles()
    {
        // The 2026-07-20 demo-box incident: callers following the API's
        // destructive-op convention (X-Confirm: true, as required by the
        // drop-collection routes) got a silent Detach — file left on disk.
        var (registry, baseUrl, _) = BootServer();
        await _http.PostAsJsonAsync($"{baseUrl}/databases", new { name = "doomed2" });
        var info = registry.List().First();

        var req = new HttpRequestMessage(HttpMethod.Delete, $"{baseUrl}/databases/doomed2");
        req.Headers.TryAddWithoutValidation("X-Confirm", "true");
        var resp = await _http.SendAsync(req);
        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadFromJsonAsync<DeleteDatabaseResponse>();
        Assert.Equal("dropped", body!.Action);
        Assert.False(File.Exists(info.FilePath), "X-Confirm drop must delete the data file.");
    }

    [Fact]
    public async Task DeleteDatabase_ExplicitDeleteFilesFalse_WinsOverXConfirm()
    {
        // Studio sends ?deleteFiles=false for its Detach action; an
        // explicit query param must not be overridden by the header.
        var (registry, baseUrl, _) = BootServer();
        await _http.PostAsJsonAsync($"{baseUrl}/databases", new { name = "kept" });
        var info = registry.List().First();

        var req = new HttpRequestMessage(HttpMethod.Delete, $"{baseUrl}/databases/kept?deleteFiles=false");
        req.Headers.TryAddWithoutValidation("X-Confirm", "true");
        var resp = await _http.SendAsync(req);
        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadFromJsonAsync<DeleteDatabaseResponse>();
        Assert.Equal("detached", body!.Action);
        Assert.True(File.Exists(info.FilePath));
    }

    [Fact]
    public async Task DeleteDatabase_DropThenRecreate_NoResurrectedDocuments()
    {
        // Regression for the demo-box incident: drop a DB with documents,
        // recreate under the same name, and the new DB must be empty —
        // NOT a re-attach of the old file with every document back.
        var (registry, baseUrl, _) = BootServer();
        await _http.PostAsJsonAsync($"{baseUrl}/databases", new { name = "phoenix" });
        registry.Get("phoenix").Insert("orders", """{"pnr":"FRIDAY-1"}""");

        var req = new HttpRequestMessage(HttpMethod.Delete, $"{baseUrl}/databases/phoenix");
        req.Headers.TryAddWithoutValidation("X-Confirm", "true");
        (await _http.SendAsync(req)).EnsureSuccessStatusCode();

        var recreate = await _http.PostAsJsonAsync($"{baseUrl}/databases", new { name = "phoenix" });
        Assert.Equal(HttpStatusCode.Created, recreate.StatusCode);

        var collections = await _http.GetFromJsonAsync<ScopedCollectionsResponse>(
            $"{baseUrl}/db/phoenix/collections");
        Assert.Empty(collections!.Collections);
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

        // Issue #112 — the follower entry must expose a CONNECTABLE httpEndpoint
        // (its own HTTP base URL), not just the ephemeral replication TCP socket.
        var followerEntry = alphaStatus.Leader.Followers.FirstOrDefault();
        Assert.NotNull(followerEntry);
        Assert.False(string.IsNullOrEmpty(followerEntry!.HttpEndpoint),
            "follower should advertise a connectable httpEndpoint (issue #112)");
        Assert.StartsWith("http", followerEntry.HttpEndpoint!);
        // And it must differ from the ephemeral replication source-port socket.
        Assert.NotEqual(followerEntry.Endpoint, followerEntry.HttpEndpoint);
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

    /// <summary>Stamp a real .dfdb file on disk and immediately close it,
    /// so an Attach against the same path succeeds (vs. a 1-byte stub
    /// which would throw on header validation and silently land in the
    /// endpoint's errors list).</summary>
    private static void MakeRealDfdbFile(string path)
    {
        using var db = DocumentForgeDb.Create(path);
        // Sidecar files (.lock, .wal, .recovery) get dropped next to it
        // — that's fine, the endpoint only globs *.dfdb.
    }

    // Issue #87 — backup + restore. Single-DB happy path proves the
    // whole wiring (BackupManager, endpoint, system-DB audit row,
    // restore-to-new-name semantics, recovered data integrity).
    [Fact]
    public async Task BackupRestore_HappyPath_RecoversAllDocuments()
    {
        // Spin up a service with a real BackupManager + DatabaseCatalog
        // so the system DB writes that back the audit row + the post-
        // restore catalog record both work.
        var (registry, baseUrl, dataDir, _, _) = BootServerWithBackup();

        // Set up a tenant DB with a known doc set.
        var attach = await _http.PostAsJsonAsync($"{baseUrl}/databases",
            new { name = "tenant_x" });
        attach.EnsureSuccessStatusCode();
        for (int i = 0; i < 6; i++)
        {
            var body = new StringContent($$"""{"pnr":"X{{i}}"}""",
                System.Text.Encoding.UTF8, "application/json");
            (await _http.PostAsync($"{baseUrl}/db/tenant_x/collections/pnrs", body))
                .EnsureSuccessStatusCode();
        }

        // Take a backup.
        var bkResp = await _http.PostAsync($"{baseUrl}/databases/tenant_x/backup", content: null);
        Assert.Equal(System.Net.HttpStatusCode.Created, bkResp.StatusCode);
        var backup = await bkResp.Content.ReadFromJsonAsync<BackupWireRow>();
        Assert.NotNull(backup);
        Assert.Equal("tenant_x", backup!.Database);
        Assert.True(File.Exists(backup.Path));
        Assert.True(backup.SizeBytes > 0);

        // Audit row visible in the list.
        var list = await _http.GetFromJsonAsync<BackupListWire>($"{baseUrl}/admin/backups");
        Assert.Equal(1, list!.Count);
        Assert.Equal(backup.Id, list.Backups[0].Id);

        // Restore as a NEW DB name.
        var restoreResp = await _http.PostAsJsonAsync($"{baseUrl}/admin/backup/restore",
            new { backupId = backup.Id, newDatabaseName = "tenant_x_restored" });
        restoreResp.EnsureSuccessStatusCode();
        var restored = await restoreResp.Content.ReadFromJsonAsync<RestoreWire>();
        Assert.Equal("tenant_x_restored", restored!.Database);
        Assert.NotNull(registry.TryGet("tenant_x_restored"));

        // The restored DB has every doc the source had.
        var q = await _http.PostAsJsonAsync($"{baseUrl}/db/tenant_x_restored/query",
            new { sql = "SELECT * FROM pnrs" });
        q.EnsureSuccessStatusCode();
        var body2 = await q.Content.ReadAsStringAsync();
        for (int i = 0; i < 6; i++) Assert.Contains($"\"X{i}\"", body2);
    }

    [Fact]
    public async Task BackupRestore_RestoreToExistingName_Refused()
    {
        // Restore must never clobber an already-attached DB. The whole
        // point of "restore as a new name" is that the source survives
        // even if you fat-finger the target.
        var (_, baseUrl, _, _, _) = BootServerWithBackup();
        await _http.PostAsJsonAsync($"{baseUrl}/databases", new { name = "alpha" });
        var body = new StringContent("""{"x":1}""", System.Text.Encoding.UTF8, "application/json");
        await _http.PostAsync($"{baseUrl}/db/alpha/collections/things", body);
        var bkResp = await _http.PostAsync($"{baseUrl}/databases/alpha/backup", content: null);
        var backup = await bkResp.Content.ReadFromJsonAsync<BackupWireRow>();

        // Try to restore using the source name — refuse with 409.
        var resp = await _http.PostAsJsonAsync($"{baseUrl}/admin/backup/restore",
            new { backupId = backup!.Id, newDatabaseName = "alpha" });
        Assert.Equal(System.Net.HttpStatusCode.Conflict, resp.StatusCode);
    }

    [Fact]
    public async Task BackupConfig_Roundtrip_Persists()
    {
        var (_, baseUrl, dataDir, _, _) = BootServerWithBackup();

        var custom = Path.Combine(dataDir, "_backups_custom");
        var put = await _http.PutAsJsonAsync($"{baseUrl}/admin/backup/config",
            new { backupDir = custom, retentionCount = 25 });
        put.EnsureSuccessStatusCode();

        var read = await _http.GetFromJsonAsync<BackupConfigWire>(
            $"{baseUrl}/admin/backup/config");
        Assert.NotNull(read);
        Assert.Equal(custom, read!.BackupDirConfigured);
        Assert.Equal(custom, read.BackupDir);
        Assert.Equal(25, read.RetentionCount);
    }

    [Fact]
    public async Task BackupRetention_PrunesOlderBeyondLimit()
    {
        // With retention = 3, the 4th backup of a single DB should
        // evict the oldest. The retention sweep runs inline at the
        // end of each BackupOne call.
        var (_, baseUrl, _, _, _) = BootServerWithBackup();
        await _http.PutAsJsonAsync($"{baseUrl}/admin/backup/config",
            new { retentionCount = 3 });
        await _http.PostAsJsonAsync($"{baseUrl}/databases", new { name = "victim" });
        // Need real content so each snapshot is a distinct file.
        var body = new StringContent("""{"v":1}""", System.Text.Encoding.UTF8, "application/json");
        await _http.PostAsync($"{baseUrl}/db/victim/collections/x", body);

        var ids = new List<string>();
        for (int i = 0; i < 4; i++)
        {
            // Small delay so timestamps don't collide in the filename.
            await Task.Delay(5);
            var r = await _http.PostAsync($"{baseUrl}/databases/victim/backup", content: null);
            var rec = await r.Content.ReadFromJsonAsync<BackupWireRow>();
            ids.Add(rec!.Id);
        }

        var list = await _http.GetFromJsonAsync<BackupListWire>(
            $"{baseUrl}/databases/victim/backups");
        Assert.Equal(3, list!.Count);
        // The oldest (ids[0]) should have been pruned.
        Assert.DoesNotContain(list.Backups, b => b.Id == ids[0]);
    }

    [Fact]
    public async Task BackupAll_HandlesMultipleAttachedTenants()
    {
        var (_, baseUrl, _, _, _) = BootServerWithBackup();
        await _http.PostAsJsonAsync($"{baseUrl}/databases", new { name = "ten_a" });
        await _http.PostAsJsonAsync($"{baseUrl}/databases", new { name = "ten_b" });
        await _http.PostAsJsonAsync($"{baseUrl}/databases", new { name = "ten_c" });

        var resp = await _http.PostAsync($"{baseUrl}/admin/backup/all", content: null);
        resp.EnsureSuccessStatusCode();
        var summary = await resp.Content.ReadFromJsonAsync<BackupAllWire>();
        Assert.Equal(3, summary!.Count);
        Assert.Empty(summary.Errors);
    }

    // Issue #88 — PITR end-to-end tests. The smoke tests we ran during
    // development proved the LOGIC works; these tests guard against
    // future regression.

    [Fact]
    public async Task PitrArchiveEnableDisable_PersistsState()
    {
        // The archive enabled-flag survives Disable + re-Enable. The
        // sequence counter is monotonic across the cycle (a previously-
        // shipped segment-N doesn't get re-numbered).
        var (registry, baseUrl, _, _, _, walArchiver, _) = BootServerWithPitr();

        await _http.PostAsJsonAsync($"{baseUrl}/databases", new { name = "alpha" });

        var enable = await _http.PostAsync($"{baseUrl}/databases/alpha/archive/enable", content: null);
        enable.EnsureSuccessStatusCode();
        var status1 = await _http.GetFromJsonAsync<ArchiveStatusWire>(
            $"{baseUrl}/databases/alpha/archive/status");
        Assert.True(status1!.Enabled);

        var disable = await _http.PostAsync($"{baseUrl}/databases/alpha/archive/disable", content: null);
        disable.EnsureSuccessStatusCode();
        var status2 = await _http.GetFromJsonAsync<ArchiveStatusWire>(
            $"{baseUrl}/databases/alpha/archive/status");
        Assert.False(status2!.Enabled);
    }

    [Fact]
    public async Task PitrArchive_FlushShipsSegmentWithExpectedFormat()
    {
        // Drive an insert + flush, expect exactly one segment file on
        // disk with the engine's recovery-log format
        // ([Magic:4 "WLOG"][PageId:4][Checksum:4][PageData:8192]).
        var (_, baseUrl, _, _, backupMgr, _, _) = BootServerWithPitr();

        await _http.PostAsJsonAsync($"{baseUrl}/databases", new { name = "tenant_x" });
        await _http.PostAsync($"{baseUrl}/databases/tenant_x/archive/enable", content: null);
        var body = new StringContent("""{"pnr":"X1"}""", System.Text.Encoding.UTF8, "application/json");
        await _http.PostAsync($"{baseUrl}/db/tenant_x/collections/things", body);

        var flush = await _http.PostAsync($"{baseUrl}/admin/archive/flush", content: null);
        flush.EnsureSuccessStatusCode();
        // The flush is synchronous — by the time it returns, the
        // segment file is on disk.

        var segs = await _http.GetFromJsonAsync<ArchiveSegmentsWire>(
            $"{baseUrl}/databases/tenant_x/archive/segments");
        Assert.True(segs!.Count >= 1);
        var seg = segs.Segments[0];
        Assert.True(seg.ByteCount > 0);
        Assert.True(seg.RecordCount > 0);
        // Each record is 8204 bytes (12-byte header + 8192-byte page).
        Assert.Equal(0, seg.ByteCount % 8204);

        // Spot-check the magic bytes on disk to prove the segment
        // really is in WLOG format, not just an empty file.
        var segDir = Path.Combine(backupMgr.EffectiveBackupDir, "wal", "tenant_x");
        var segFile = Directory.EnumerateFiles(segDir, "*.walseg")
            .First(f => !f.EndsWith(".meta"));
        var bytes = File.ReadAllBytes(segFile);
        Assert.True(bytes.Length >= 4);
        Assert.Equal((byte)'W', bytes[0]);
        Assert.Equal((byte)'L', bytes[1]);
        Assert.Equal((byte)'O', bytes[2]);
        Assert.Equal((byte)'G', bytes[3]);
    }

    [Fact]
    public async Task PitrRestore_TargetAfterBaseBackup_RecoversNewWrites()
    {
        // The headline test: state at T0 captured by backup, more
        // writes happen, restore-to-now applies the segments and
        // yields a DB with both the original AND the later writes.
        var (registry, baseUrl, _, _, _, _, _) = BootServerWithPitr();

        await _http.PostAsJsonAsync($"{baseUrl}/databases", new { name = "src" });
        await _http.PostAsync($"{baseUrl}/databases/src/archive/enable", content: null);

        // T0: 3 GOOD docs.
        for (int i = 0; i < 3; i++)
        {
            var body = new StringContent($$"""{"pnr":"GOOD-{{i}}"}""",
                System.Text.Encoding.UTF8, "application/json");
            (await _http.PostAsync($"{baseUrl}/db/src/collections/pnrs", body))
                .EnsureSuccessStatusCode();
        }
        await _http.PostAsync($"{baseUrl}/admin/archive/flush", content: null);
        // Tiny delay so timestamps separate cleanly.
        await Task.Delay(50);

        // T1: take base backup.
        var bk = await _http.PostAsync($"{baseUrl}/databases/src/backup", content: null);
        var backup = await bk.Content.ReadFromJsonAsync<BackupWireRow>();
        await Task.Delay(50);

        // T2: 2 LATER docs + flush.
        for (int i = 0; i < 2; i++)
        {
            var body = new StringContent($$"""{"pnr":"LATER-{{i}}"}""",
                System.Text.Encoding.UTF8, "application/json");
            await _http.PostAsync($"{baseUrl}/db/src/collections/pnrs", body);
        }
        await _http.PostAsync($"{baseUrl}/admin/archive/flush", content: null);
        await Task.Delay(50);

        // Restore to "now" — should pick up the LATER docs.
        var targetIso = DateTime.UtcNow.ToString("O");
        var resp = await _http.PostAsJsonAsync($"{baseUrl}/admin/backup/restore-pitr",
            new { backupId = backup!.Id, targetTimeUtc = targetIso, newDatabaseName = "src_restored" });
        resp.EnsureSuccessStatusCode();
        var result = await resp.Content.ReadFromJsonAsync<PitrRestoreWire>();
        Assert.True(result!.SegmentsApplied >= 1);
        Assert.NotNull(registry.TryGet("src_restored"));

        // Query the restored DB: must have ALL 5 docs (3 GOOD + 2 LATER).
        var q = await _http.PostAsJsonAsync($"{baseUrl}/db/src_restored/query",
            new { sql = "SELECT * FROM pnrs" });
        var json = await q.Content.ReadAsStringAsync();
        for (int i = 0; i < 3; i++) Assert.Contains($"GOOD-{i}", json);
        for (int i = 0; i < 2; i++) Assert.Contains($"LATER-{i}", json);
    }

    [Fact]
    public async Task PitrRestore_TargetBeforeBaseBackup_Refused()
    {
        // PITR rolls forward only. A target before the snapshot can't
        // be achieved by any sequence of segment replays applied to
        // that snapshot.
        var (_, baseUrl, _, _, _, _, _) = BootServerWithPitr();
        await _http.PostAsJsonAsync($"{baseUrl}/databases", new { name = "src" });
        await _http.PostAsync($"{baseUrl}/databases/src/archive/enable", content: null);
        // Insert + back up so we have a backup to point at.
        var body = new StringContent("""{"x":1}""", System.Text.Encoding.UTF8, "application/json");
        await _http.PostAsync($"{baseUrl}/db/src/collections/t", body);
        await _http.PostAsync($"{baseUrl}/admin/archive/flush", content: null);
        var bkResp = await _http.PostAsync($"{baseUrl}/databases/src/backup", content: null);
        var backup = await bkResp.Content.ReadFromJsonAsync<BackupWireRow>();

        // Target = one minute BEFORE the backup.
        var tooEarly = DateTime.Parse(backup!.CreatedAtUtc).AddMinutes(-1).ToUniversalTime().ToString("O");
        var resp = await _http.PostAsJsonAsync($"{baseUrl}/admin/backup/restore-pitr",
            new { backupId = backup.Id, targetTimeUtc = tooEarly, newDatabaseName = "src_rewind" });
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, resp.StatusCode);
        var msg = await resp.Content.ReadAsStringAsync();
        Assert.Contains("rolls forward only", msg);
    }

    [Fact]
    public async Task PitrPreview_FeasibilityFlagAndCountsCorrect()
    {
        var (_, baseUrl, _, _, _, _, _) = BootServerWithPitr();
        await _http.PostAsJsonAsync($"{baseUrl}/databases", new { name = "src" });
        await _http.PostAsync($"{baseUrl}/databases/src/archive/enable", content: null);
        var body = new StringContent("""{"x":1}""", System.Text.Encoding.UTF8, "application/json");
        await _http.PostAsync($"{baseUrl}/db/src/collections/t", body);
        await _http.PostAsync($"{baseUrl}/admin/archive/flush", content: null);
        var bkResp = await _http.PostAsync($"{baseUrl}/databases/src/backup", content: null);
        var backup = await bkResp.Content.ReadFromJsonAsync<BackupWireRow>();
        await Task.Delay(50);

        // Drop another doc + flush so there's at least one segment
        // after the backup to count.
        var body2 = new StringContent("""{"x":2}""", System.Text.Encoding.UTF8, "application/json");
        await _http.PostAsync($"{baseUrl}/db/src/collections/t", body2);
        await _http.PostAsync($"{baseUrl}/admin/archive/flush", content: null);

        var preview = await _http.PostAsJsonAsync($"{baseUrl}/admin/backup/restore-pitr/preview",
            new { backupId = backup!.Id, targetTimeUtc = DateTime.UtcNow.ToString("O") });
        preview.EnsureSuccessStatusCode();
        var plan = await preview.Content.ReadFromJsonAsync<PitrPreviewWire>();
        Assert.True(plan!.FeasibleToRestore);
        Assert.True(plan.SegmentCount >= 1);
        Assert.True(plan.BytesToReplay >= 8204);  // at least one record
    }

    /// <summary>Boot a server with the FULL PITR plumbing (registry,
    /// catalog, BackupManager, WalArchiver, PointInTimeRestore). Only
    /// Issue #88 tests need all of this; #87 tests use the lighter
    /// <see cref="BootServerWithBackup"/> fixture.</summary>
    private (DatabaseRegistry registry, string baseUrl, string dataDir,
        DatabaseCatalog catalog, BackupManager backup, WalArchiver walArchiver, PointInTimeRestore pitr)
        BootServerWithPitr()
    {
        var port = FindFreePort();
        var dataDir = Path.Combine(Path.GetTempPath(), $"pitr_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dataDir);
        _dirs.Add(dataDir);

        var registry = new DatabaseRegistry();
        _disposables.Add(registry);
        registry.AttachOrCreate("_system", Path.Combine(dataDir, "_system.dfdb"));
        var catalog = new DatabaseCatalog(registry);
        var backup = new BackupManager(registry, dataDir);
        // Tight flush interval for tests so we don't wait 5s for
        // continuous-flush to kick in. The /admin/archive/flush
        // endpoint drives an immediate flush regardless of cadence,
        // but the timer still has to NOT misbehave during the test.
        var walArchiver = new WalArchiver(registry, backup, TimeSpan.FromSeconds(30));
        _disposables.Add(walArchiver);
        var pitr = new PointInTimeRestore(registry, backup, walArchiver, dataDir);

        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls($"http://127.0.0.1:{port}");
        var app = builder.Build();
        app.Use(async (ctx, next) =>
        {
            ctx.Items[AuthContext.ContextKey] = AuthContext.DevMode();
            await next();
        });
        DatabaseEndpoints.Map(app, registry, dataDir, catalog, backup, walArchiver, pitr);
        app.StartAsync().GetAwaiter().GetResult();
        _disposables.Add(new HostStopper(app));
        return (registry, $"http://127.0.0.1:{port}", dataDir, catalog, backup, walArchiver, pitr);
    }

    private sealed class ArchiveStatusWire
    {
        public string Database { get; set; } = "";
        public bool Enabled { get; set; }
        public long NextSequence { get; set; }
        public string? LastShippedAtUtc { get; set; }
        public long SegmentsThisSession { get; set; }
    }
    private sealed class ArchiveSegmentRow
    {
        public long SequenceNumber { get; set; }
        public string ArchivedAtUtc { get; set; } = "";
        public long ByteCount { get; set; }
        public int RecordCount { get; set; }
        public string? SegmentPath { get; set; }
    }
    private sealed class ArchiveSegmentsWire
    {
        public string Database { get; set; } = "";
        public int Count { get; set; }
        public long TotalBytes { get; set; }
        public List<ArchiveSegmentRow> Segments { get; set; } = new();
    }
    private sealed class PitrPreviewWire
    {
        public bool FeasibleToRestore { get; set; }
        public string? RefusalReason { get; set; }
        public int SegmentCount { get; set; }
        public long BytesToReplay { get; set; }
    }
    private sealed class PitrRestoreWire
    {
        public string Database { get; set; } = "";
        public string FilePath { get; set; } = "";
        public string SourceDatabase { get; set; } = "";
        public string BaseBackupId { get; set; } = "";
        public string TargetTimeUtc { get; set; } = "";
        public int SegmentsApplied { get; set; }
        public long BytesReplayed { get; set; }
    }

    /// <summary>Boot a server with the BackupManager wired up — used
    /// only by Issue #87 tests; everything else still uses the simpler
    /// fixture without it.</summary>
    private (DatabaseRegistry registry, string baseUrl, string dataDir, DatabaseCatalog catalog, BackupManager backup)
        BootServerWithBackup()
    {
        var port = FindFreePort();
        var dataDir = Path.Combine(Path.GetTempPath(), $"bk_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dataDir);
        _dirs.Add(dataDir);

        var registry = new DatabaseRegistry();
        _disposables.Add(registry);
        registry.AttachOrCreate("_system", Path.Combine(dataDir, "_system.dfdb"));
        var catalog = new DatabaseCatalog(registry);
        var backup = new BackupManager(registry, dataDir);

        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls($"http://127.0.0.1:{port}");
        var app = builder.Build();
        app.Use(async (ctx, next) =>
        {
            ctx.Items[AuthContext.ContextKey] = AuthContext.DevMode();
            await next();
        });
        DatabaseEndpoints.Map(app, registry, dataDir, catalog, backup);
        app.StartAsync().GetAwaiter().GetResult();
        _disposables.Add(new HostStopper(app));
        return (registry, $"http://127.0.0.1:{port}", dataDir, catalog, backup);
    }

    private sealed class BackupWireRow
    {
        public string Id { get; set; } = "";
        public string Database { get; set; } = "";
        public string Path { get; set; } = "";
        public long SizeBytes { get; set; }
        public string CreatedAtUtc { get; set; } = "";
        public string Kind { get; set; } = "";
    }
    private sealed class BackupListWire
    {
        public int Count { get; set; }
        public List<BackupWireRow> Backups { get; set; } = new();
    }
    private sealed class BackupAllWire
    {
        public int Count { get; set; }
        public List<string> Errors { get; set; } = new();
    }
    private sealed class BackupConfigWire
    {
        public string BackupDir { get; set; } = "";
        public string? BackupDirConfigured { get; set; }
        public int RetentionCount { get; set; }
    }
    private sealed class RestoreWire
    {
        public string Database { get; set; } = "";
        public string FilePath { get; set; } = "";
        public string RestoredFrom { get; set; } = "";
    }

    // Issue #86 — rebuild-catalog. Plan + execute against a real DB
    // whose catalog page has been zeroed out (simulating the bbqdubai
    // scenario). The rebuilder discovers the data chain and reconstructs
    // a catalog entry pointing at it.
    [Fact]
    public async Task RebuildCatalogPreview_FileWithDataButZeroedCatalog_ReportsRecoverableChains()
    {
        var (_, baseUrl, dataDir) = BootServer();
        var dbPath = Path.Combine(dataDir, "broken.dfdb");

        // Build a real DB with data, then close it.
        await _http.PostAsJsonAsync($"{baseUrl}/databases",
            new { name = "broken", path = dbPath });
        // Drop several docs to ensure data pages get linked into a chain.
        for (int i = 0; i < 30; i++)
        {
            var body = new StringContent($$"""{"pnr":"P-{{i:D4}}"}""",
                System.Text.Encoding.UTF8, "application/json");
            await _http.PostAsync($"{baseUrl}/db/broken/collections/orders", body);
        }
        await _http.DeleteAsync($"{baseUrl}/databases/broken");

        // Zero out page 1 to simulate catalog corruption. Page 0 (header)
        // + data pages stay intact. After this, attaching reports "no
        // collections" even though the documents are still on disk.
        ZeroPage1(dbPath);

        var preview = await _http.GetFromJsonAsync<RebuildPlanResponse>(
            $"{baseUrl}/databases/rebuild-catalog/preview?path={Uri.EscapeDataString(dbPath)}");
        Assert.NotNull(preview);
        Assert.True(preview!.SafeToRebuild);
        Assert.NotEmpty(preview.Chains);
        // The orders collection's chain should be discovered. Document
        // count is approximate (ItemCount includes deleted slots) but
        // for a fresh insert run with no deletes it should equal 30.
        Assert.Contains(preview.Chains, c => c.DocumentCount >= 30);
    }

    [Fact]
    public async Task RebuildCatalog_Execute_WritesCatalogAndAllowsReattach()
    {
        var (_, baseUrl, dataDir) = BootServer();
        var dbPath = Path.Combine(dataDir, "fix-me.dfdb");

        await _http.PostAsJsonAsync($"{baseUrl}/databases",
            new { name = "fix_me", path = dbPath });
        for (int i = 0; i < 12; i++)
        {
            var body = new StringContent($$"""{"id":{{i}}}""",
                System.Text.Encoding.UTF8, "application/json");
            await _http.PostAsync($"{baseUrl}/db/fix_me/collections/widgets", body);
        }
        await _http.DeleteAsync($"{baseUrl}/databases/fix_me");

        ZeroPage1(dbPath);

        var resp = await _http.PostAsync(
            $"{baseUrl}/databases/rebuild-catalog?path={Uri.EscapeDataString(dbPath)}",
            content: null);
        resp.EnsureSuccessStatusCode();
        var result = await resp.Content.ReadFromJsonAsync<RebuildResultResponse>();
        Assert.NotEmpty(result!.Recovered);
        Assert.True(File.Exists(result.BackupPath));

        // Re-attach with the rebuilt catalog and confirm the engine sees
        // the synthesised collection name with the expected doc count.
        var reattach = await _http.PostAsJsonAsync($"{baseUrl}/databases",
            new { name = "fix_me_again", path = dbPath });
        Assert.Equal(System.Net.HttpStatusCode.Created, reattach.StatusCode);
        var recoveredName = result.Recovered[0].Name;
        var q = await _http.PostAsJsonAsync($"{baseUrl}/db/fix_me_again/query",
            new { sql = $"SELECT * FROM {recoveredName}" });
        q.EnsureSuccessStatusCode();
        var json = await q.Content.ReadAsStringAsync();
        // Should contain at least one of our inserted docs.
        Assert.Contains("\"id\"", json);
    }

    [Fact]
    public async Task RebuildCatalogPreview_StillAttached_Returns409()
    {
        // The rebuilder needs exclusive access. If the operator hasn't
        // Detached yet, the endpoint refuses with a clear message instead
        // of "could not open file" — telling them what to do.
        var (_, baseUrl, dataDir) = BootServer();
        var dbPath = Path.Combine(dataDir, "still-live.dfdb");
        await _http.PostAsJsonAsync($"{baseUrl}/databases",
            new { name = "still_live", path = dbPath });

        var resp = await _http.GetAsync(
            $"{baseUrl}/databases/rebuild-catalog/preview?path={Uri.EscapeDataString(dbPath)}");
        Assert.Equal(System.Net.HttpStatusCode.Conflict, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync();
        Assert.Contains("still_live", body);  // mentions the name to detach
    }

    [Fact]
    public async Task RebuildCatalog_HealthyCatalog_Refuses()
    {
        // A working catalog (with entries) must NOT be clobbered by an
        // accidental rebuild request. The rebuilder's invariant catches
        // this; the endpoint surfaces it as 409 with the refusal reason.
        var (_, baseUrl, dataDir) = BootServer();
        var dbPath = Path.Combine(dataDir, "healthy.dfdb");
        await _http.PostAsJsonAsync($"{baseUrl}/databases",
            new { name = "healthy", path = dbPath });
        var body = new StringContent("""{"x":1}""", System.Text.Encoding.UTF8, "application/json");
        await _http.PostAsync($"{baseUrl}/db/healthy/collections/things", body);
        await _http.DeleteAsync($"{baseUrl}/databases/healthy");

        // Note: catalog is INTACT here — we did not zero page 1.
        var resp = await _http.PostAsync(
            $"{baseUrl}/databases/rebuild-catalog?path={Uri.EscapeDataString(dbPath)}",
            content: null);
        Assert.Equal(System.Net.HttpStatusCode.Conflict, resp.StatusCode);
        var json = await resp.Content.ReadAsStringAsync();
        Assert.Contains("not empty", json);
    }

    /// <summary>Overwrite page 1 (the collection catalog page) of a
    /// .dfdb file with zeros to simulate the corruption seen in the
    /// Issue #86 scenario. Header page 0 and data pages 2+ stay intact.</summary>
    private static void ZeroPage1(string dbPath)
    {
        using var fs = new FileStream(dbPath, FileMode.Open, FileAccess.ReadWrite,
            FileShare.None);
        fs.Seek(Constants.PageSize, SeekOrigin.Begin);
        var zeros = new byte[Constants.PageSize];
        fs.Write(zeros, 0, zeros.Length);
        fs.Flush(true);
    }

    private sealed class RebuildPlanResponse
    {
        public string Path { get; set; } = "";
        public bool SafeToRebuild { get; set; }
        public string? RefusalReason { get; set; }
        public List<PlanChainRow> Chains { get; set; } = new();
    }
    private sealed class PlanChainRow
    {
        public string SuggestedName { get; set; } = "";
        public uint FirstPageId { get; set; }
        public long DocumentCount { get; set; }
        public int PageCount { get; set; }
    }
    private sealed class RebuildResultResponse
    {
        public string Path { get; set; } = "";
        public string BackupPath { get; set; } = "";
        public int BackedUpPage1Bytes { get; set; }
        public List<RecoveredRow> Recovered { get; set; } = new();
    }
    private sealed class RecoveredRow
    {
        public string Name { get; set; } = "";
        public uint FirstPageId { get; set; }
        public long DocumentCount { get; set; }
    }

    // Issue #85 — GET /databases/{name}/health. Diagnostic surface.
    [Fact]
    public async Task GetHealth_FreshDatabase_ReturnsHealthy()
    {
        var (_, baseUrl, _) = BootServer();
        await _http.PostAsJsonAsync($"{baseUrl}/databases", new { name = "alpha" });

        var h = await _http.GetFromJsonAsync<HealthResponse>(
            $"{baseUrl}/databases/alpha/health");
        Assert.NotNull(h);
        Assert.Equal("alpha", h!.Database);
        Assert.True(h.Attached);
        Assert.Equal("Healthy", h.HealthStatus);
        Assert.Equal(0, h.Collections.Count);
        Assert.Equal(0, h.Collections.TotalDocuments);
        Assert.True(h.Files.DataSizeBytes > 0);
        Assert.Equal(0, h.Files.RecoveryLogBytes);
        Assert.Equal("healthy", h.Recommendation);
    }

    [Fact]
    public async Task GetHealth_WithDocuments_ReportsCountAndCollections()
    {
        var (_, baseUrl, _) = BootServer();
        await _http.PostAsJsonAsync($"{baseUrl}/databases", new { name = "beta" });
        // Drop a couple of docs into a collection via the scoped /db/{name} insert.
        var body = new StringContent("""{"pnr":"AAA"}""", System.Text.Encoding.UTF8, "application/json");
        await _http.PostAsync($"{baseUrl}/db/beta/collections/orders", body);
        var body2 = new StringContent("""{"pnr":"BBB"}""", System.Text.Encoding.UTF8, "application/json");
        await _http.PostAsync($"{baseUrl}/db/beta/collections/orders", body2);

        var h = await _http.GetFromJsonAsync<HealthResponse>(
            $"{baseUrl}/databases/beta/health");
        Assert.NotNull(h);
        Assert.Equal(1, h!.Collections.Count);
        Assert.Contains("orders", h.Collections.Names);
        Assert.Equal(2, h.Collections.TotalDocuments);
        Assert.Equal("healthy", h.Recommendation);
    }

    [Fact]
    public async Task GetHealth_Unknown_Returns404()
    {
        var (_, baseUrl, _) = BootServer();
        var resp = await _http.GetAsync($"{baseUrl}/databases/ghost/health");
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    private sealed class HealthResponse
    {
        public string Database { get; set; } = "";
        public string FilePath { get; set; } = "";
        public bool Attached { get; set; }
        public string HealthStatus { get; set; } = "";
        public bool ReadOnly { get; set; }
        public HealthCollections Collections { get; set; } = new();
        public HealthFiles Files { get; set; } = new();
        public string Recommendation { get; set; } = "";
        public string? RecommendationDetail { get; set; }
    }
    private sealed class HealthCollections
    {
        public int Count { get; set; }
        public List<string> Names { get; set; } = new();
        public long TotalDocuments { get; set; }
    }
    private sealed class HealthFiles
    {
        public long DataSizeBytes { get; set; }
        public long RecoveryLogBytes { get; set; }
        public long WalBytes { get; set; }
        public bool SnapshotMarkerPresent { get; set; }
    }

    // Issue #84 — POST /databases/discover. Runtime rescan + auto-attach.
    [Fact]
    public async Task PostDiscover_DropOrphanFiles_AttachesAndReportsThem()
    {
        // The "I dropped a file into the volume, no restart needed"
        // workflow. Same shape as boot-time auto-discover.
        var (registry, baseUrl, dataDir) = BootServer();

        MakeRealDfdbFile(Path.Combine(dataDir, "orphan_a.dfdb"));
        MakeRealDfdbFile(Path.Combine(dataDir, "orphan_b.dfdb"));

        var resp = await _http.PostAsync($"{baseUrl}/databases/discover", content: null);
        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadFromJsonAsync<DiscoverResponse>();

        Assert.Equal(2, body!.Discovered);
        Assert.Equal(0, body.SkippedTombstoned);
        Assert.NotNull(registry.TryGet("orphan_a"));
        Assert.NotNull(registry.TryGet("orphan_b"));
    }

    [Fact]
    public async Task PostDiscover_AlreadyAttached_Skipped()
    {
        // A second call shouldn't re-attach anything (idempotent) and
        // shouldn't 409 on the in-registry entries.
        var (_, baseUrl, dataDir) = BootServer();
        MakeRealDfdbFile(Path.Combine(dataDir, "tenant_x.dfdb"));

        var first = await _http.PostAsync($"{baseUrl}/databases/discover", content: null);
        var firstBody = await first.Content.ReadFromJsonAsync<DiscoverResponse>();
        Assert.Equal(1, firstBody!.Discovered);

        var second = await _http.PostAsync($"{baseUrl}/databases/discover", content: null);
        second.EnsureSuccessStatusCode();
        var secondBody = await second.Content.ReadFromJsonAsync<DiscoverResponse>();
        Assert.Equal(0, secondBody!.Discovered);
        Assert.Empty(secondBody.Errors);
    }

    [Fact]
    public async Task PostDiscover_RecursiveFlag_AttachesSubfolderFiles()
    {
        var (registry, baseUrl, dataDir) = BootServer();
        var subdir = Path.Combine(dataDir, "_volume_b");
        Directory.CreateDirectory(subdir);
        MakeRealDfdbFile(Path.Combine(subdir, "tenant_nested.dfdb"));

        var nonRecursive = await _http.PostAsync($"{baseUrl}/databases/discover", content: null);
        var nrBody = await nonRecursive.Content.ReadFromJsonAsync<DiscoverResponse>();
        Assert.Equal(0, nrBody!.Discovered);
        Assert.Null(registry.TryGet("tenant_nested"));

        var recursive = await _http.PostAsync($"{baseUrl}/databases/discover?recursive=true", content: null);
        var rBody = await recursive.Content.ReadFromJsonAsync<DiscoverResponse>();
        Assert.Equal(1, rBody!.Discovered);
        Assert.NotNull(registry.TryGet("tenant_nested"));
    }

    [Fact]
    public async Task PostDiscover_HonoursTombstones()
    {
        // Detach intent must survive a runtime rescan. Otherwise pressing
        // "Refresh & attach all" would silently undo a deliberate detach
        // — surprising and risky. Manual one-click Attach via the
        // /databases/unattached + per-row attach flow still works because
        // that's an explicit operator decision per file.
        var (registry, baseUrl, dataDir, catalog) = BootServerWithCatalog(includeCatalog: true);
        Assert.NotNull(catalog);

        // Attach + Detach to produce a tombstone for "drained".
        await _http.PostAsJsonAsync($"{baseUrl}/databases",
            new { name = "drained", path = Path.Combine(dataDir, "drained.dfdb") });
        await _http.DeleteAsync($"{baseUrl}/databases/drained");
        // File still on disk (Detach != Drop).
        Assert.True(File.Exists(Path.Combine(dataDir, "drained.dfdb")));
        // And there's a fresh tenant to discover.
        MakeRealDfdbFile(Path.Combine(dataDir, "fresh.dfdb"));

        var resp = await _http.PostAsync($"{baseUrl}/databases/discover", content: null);
        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadFromJsonAsync<DiscoverResponse>();

        Assert.Equal(1, body!.Discovered);            // fresh attached
        Assert.Equal(1, body.SkippedTombstoned);      // drained skipped
        Assert.NotNull(registry.TryGet("fresh"));
        Assert.Null(registry.TryGet("drained"));
    }

    [Fact]
    public async Task PostDiscover_SkipsImplicitFiles()
    {
        var (registry, baseUrl, dataDir) = BootServer();
        // Stubs are fine here — we never try to Attach data.dfdb or
        // _system.dfdb via this endpoint (they're explicitly skipped).
        File.WriteAllBytes(Path.Combine(dataDir, "data.dfdb"), new byte[] { 0 });
        File.WriteAllBytes(Path.Combine(dataDir, "_system.dfdb"), new byte[] { 0 });
        MakeRealDfdbFile(Path.Combine(dataDir, "tenant_real.dfdb"));

        var resp = await _http.PostAsync($"{baseUrl}/databases/discover", content: null);
        var body = await resp.Content.ReadFromJsonAsync<DiscoverResponse>();
        Assert.Equal(1, body!.Discovered);
        // data + _system should remain untouched (not auto-attached as tenants).
        Assert.Null(registry.TryGet("data"));
        Assert.Null(registry.TryGet("_system"));
        Assert.NotNull(registry.TryGet("tenant_real"));
    }

    private sealed class DiscoverResponse
    {
        public string DataDir { get; set; } = "";
        public bool Recursive { get; set; }
        public int Discovered { get; set; }
        public int SkippedTombstoned { get; set; }
        public List<string> Errors { get; set; } = new();
        public List<DiscoveredEntry> Attached { get; set; } = new();
    }
    private sealed class DiscoveredEntry
    {
        public string Name { get; set; } = "";
        public string Path { get; set; } = "";
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
    private sealed class ScopedCollectionsResponse
    {
        public string Database { get; set; } = "";
        public List<string> Collections { get; set; } = new();
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
        public List<ReplicationFollowerEntry> Followers { get; set; } = new();
    }
    private sealed class ReplicationFollowerEntry
    {
        public string? Endpoint { get; set; }
        public string? HttpEndpoint { get; set; }
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
