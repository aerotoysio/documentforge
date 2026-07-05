using System.Net.Http.Json;
using System.Text.Json;
using DocumentForge.Cli;
using DocumentForge.Cli.Router;
using Xunit;

namespace DocumentForge.Tests;

/// <summary>
/// Real-services router integration. Boots two child <c>dfdb serve</c>
/// processes (via <see cref="ServiceManager"/>), points a <see cref="Router"/>
/// at them, and asserts that hash-sharded inserts distribute correctly and
/// that fan-out queries see every document.
///
/// <para>
/// Slower than the unit tests (~3-5s for both child boots) but covers
/// the round-trip path applications actually use: client → router →
/// pick shard → forward HTTP → engine writes → fan-out read → merge.
/// </para>
/// </summary>
public sealed class RouterIntegrationTests : IDisposable
{
    private readonly ServiceManager _services = new();
    private readonly List<string> _dirs = new();
    private readonly List<ClusterRouter> _routers = new();

    public void Dispose()
    {
        foreach (var r in _routers) try { r.Dispose(); } catch { }
        _services.Dispose();
        foreach (var d in _dirs)
        {
            try { Directory.Delete(d, recursive: true); } catch { }
        }
    }

    private string FreshDir(string label)
    {
        var dir = Path.Combine(Path.GetTempPath(), $"router_{label}_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        _dirs.Add(dir);
        return dir;
    }

    // Resolve the dfdb binary from the CLI project bin output. Same
    // pattern as ServiceManagerTests since `dotnet test` runs in the
    // test runner, not the dfdb apphost.
    private static string ResolveBinary()
    {
        var asmDir = Path.GetDirectoryName(typeof(RouterIntegrationTests).Assembly.Location)!;
        var conf = new DirectoryInfo(asmDir).Parent!.Name;
        var path = Path.GetFullPath(Path.Combine(
            asmDir, "..", "..", "..", "..", "..",
            "src", "DocumentForge.Cli", "bin", conf, "net9.0",
            OperatingSystem.IsWindows() ? "dfdb.exe" : "dfdb"));
        if (!File.Exists(path))
            throw new InvalidOperationException(
                $"dfdb binary not found at {path} — build src/DocumentForge.Cli first.");
        return path;
    }

    [Fact]
    public void Router_HashStrategy_DistributesInsertsAcrossShards()
    {
        // Spawn two backing dfdb instances on free ports.
        var binary = ResolveBinary();
        var ringA = _services.Spawn(new SpawnOptions { DataDir = FreshDir("ring-a"), BinaryPath = binary, InsecureDevMode = true });
        var ringB = _services.Spawn(new SpawnOptions { DataDir = FreshDir("ring-b"), BinaryPath = binary, InsecureDevMode = true });
        Assert.True(ringA.Ready, "ring-a never became ready");
        Assert.True(ringB.Ready, "ring-b never became ready");

        // Cluster config: orders is hash-sharded across the two ring leaders.
        var config = new ClusterConfig
        {
            Shards =
            {
                new ShardConfig { Name = "ring-a", Leader = new ShardEndpoint { BaseUrl = ringA.BaseUrl } },
                new ShardConfig { Name = "ring-b", Leader = new ShardEndpoint { BaseUrl = ringB.BaseUrl } },
            },
            Collections =
            {
                new CollectionConfig { Name = "orders", Strategy = ShardStrategy.Hash, ShardKeyPath = "pnr" },
            },
        };
        var router = new ClusterRouter(config);
        _routers.Add(router);

        // Insert 100 unique-pnr docs through the router.
        for (int i = 0; i < 100; i++)
        {
            using var doc = JsonDocument.Parse($$"""{"pnr":"PNR-{{i:D4}}","amount":{{i}}}""");
            var resp = router.InsertAsync("orders", doc.RootElement).GetAwaiter().GetResult();
            Assert.True(resp.IsSuccessStatusCode, $"insert {i} failed: {resp.StatusCode}");
        }

        // Probe each ring directly: every doc must land on exactly one
        // of the two rings (no replication, no duplication). Distribution
        // should be roughly even — FNV-1a over 100 distinct keys.
        var ringADocs = CountOrdersOn(ringA.BaseUrl);
        var ringBDocs = CountOrdersOn(ringB.BaseUrl);
        Assert.Equal(100, ringADocs + ringBDocs);
        // Loose distribution bound — chi-square would be overkill; just
        // catch egregious "all docs on one ring" failures.
        Assert.InRange(ringADocs, 30, 70);
        Assert.InRange(ringBDocs, 30, 70);

        // Fan-out query via the router merges both rings' results.
        var queryResult = router.QueryAsync("SELECT * FROM orders").GetAwaiter().GetResult();
        Assert.True(queryResult.GetProperty("documents").GetArrayLength() == 100,
            $"fan-out merge returned {queryResult.GetProperty("documents").GetArrayLength()} docs, expected 100");
        Assert.Equal(100, queryResult.GetProperty("count").GetInt32());
        Assert.Equal(2, queryResult.GetProperty("shards").GetInt32());
    }

    [Fact]
    public void Router_ReplicatedStrategy_FansInsertToEveryShard()
    {
        // For lookup tables and reference data, every ring needs the
        // same content. The router fans the insert to every leader.
        var binary = ResolveBinary();
        var ringA = _services.Spawn(new SpawnOptions { DataDir = FreshDir("ring-a"), BinaryPath = binary, InsecureDevMode = true });
        var ringB = _services.Spawn(new SpawnOptions { DataDir = FreshDir("ring-b"), BinaryPath = binary, InsecureDevMode = true });
        Assert.True(ringA.Ready && ringB.Ready);

        var config = new ClusterConfig
        {
            Shards =
            {
                new ShardConfig { Name = "ring-a", Leader = new ShardEndpoint { BaseUrl = ringA.BaseUrl } },
                new ShardConfig { Name = "ring-b", Leader = new ShardEndpoint { BaseUrl = ringB.BaseUrl } },
            },
            Collections =
            {
                new CollectionConfig { Name = "lookup", Strategy = ShardStrategy.Replicated },
            },
        };
        var router = new ClusterRouter(config);
        _routers.Add(router);

        for (int i = 0; i < 5; i++)
        {
            using var doc = JsonDocument.Parse($$"""{"code":"L{{i}}","label":"Label {{i}}"}""");
            var resp = router.InsertAsync("lookup", doc.RootElement).GetAwaiter().GetResult();
            Assert.True(resp.IsSuccessStatusCode);
        }

        // Each ring should hold a full copy.
        var ringACount = CountLookupOn(ringA.BaseUrl);
        var ringBCount = CountLookupOn(ringB.BaseUrl);
        Assert.Equal(5, ringACount);
        Assert.Equal(5, ringBCount);

        // Replicated read goes to one ring (the first) — no fan-out
        // since the data is identical everywhere.
        var queryResult = router.QueryAsync("SELECT * FROM lookup").GetAwaiter().GetResult();
        Assert.Equal(5, queryResult.GetProperty("documents").GetArrayLength());
    }

    [Fact]
    public void Router_UndeclaredCollection_DefaultsToSingleShard()
    {
        // Casual workflow: you wire up a router but haven't declared
        // every collection's strategy yet. Undeclared collections
        // should pin to shards[0] rather than crash — keeps the
        // "spin up a router and start poking" UX smooth.
        var binary = ResolveBinary();
        var ringA = _services.Spawn(new SpawnOptions { DataDir = FreshDir("ring-a"), BinaryPath = binary, InsecureDevMode = true });
        var ringB = _services.Spawn(new SpawnOptions { DataDir = FreshDir("ring-b"), BinaryPath = binary, InsecureDevMode = true });
        Assert.True(ringA.Ready && ringB.Ready);

        var config = new ClusterConfig
        {
            Shards =
            {
                new ShardConfig { Name = "ring-a", Leader = new ShardEndpoint { BaseUrl = ringA.BaseUrl } },
                new ShardConfig { Name = "ring-b", Leader = new ShardEndpoint { BaseUrl = ringB.BaseUrl } },
            },
            // No collection declarations.
        };
        var router = new ClusterRouter(config);
        _routers.Add(router);

        using var doc = JsonDocument.Parse("""{"name":"Acme","tier":"gold"}""");
        var resp = router.InsertAsync("customers", doc.RootElement).GetAwaiter().GetResult();
        Assert.True(resp.IsSuccessStatusCode);

        Assert.Equal(1, CountCustomersOn(ringA.BaseUrl));
        Assert.Equal(0, CountCustomersOn(ringB.BaseUrl));
    }

    // -------------------------------------------------------------- helpers

    private static int CountOrdersOn(string baseUrl) => CountCollectionOn(baseUrl, "orders");
    private static int CountLookupOn(string baseUrl) => CountCollectionOn(baseUrl, "lookup");
    private static int CountCustomersOn(string baseUrl) => CountCollectionOn(baseUrl, "customers");
    private static int CountCollectionOn(string baseUrl, string collection)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
        // SELECT * FROM <col> is the simplest portable query. Counting
        // .documents.length avoids depending on a "SELECT COUNT(*)" path
        // that might not be implemented yet.
        var req = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/query")
        {
            Content = JsonContent.Create(new { sql = $"SELECT * FROM {collection}" }),
        };
        var resp = http.SendAsync(req).GetAwaiter().GetResult();
        var body = resp.Content.ReadAsStringAsync().GetAwaiter().GetResult();
        if (!resp.IsSuccessStatusCode) return 0; // collection might not exist yet on this ring
        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.GetProperty("documents").GetArrayLength();
    }
}
