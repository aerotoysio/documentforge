using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
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

    // Response DTOs — narrow shapes for deserialization in tests.
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
}
