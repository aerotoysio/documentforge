using System.Net;
using System.Text;
using DocumentForge.Studio.Core.Connections;

namespace DocumentForge.Studio.Core.Tests;

/// <summary>Exercises response parsing and error mapping against canned
/// server payloads (shapes copied from ServeCommand/DatabaseEndpoints).
/// Live end-to-end coverage against a real dfdb serve node belongs to the
/// existing integration suite.</summary>
public sealed class HttpConnectionTests
{
    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Dictionary<string, (HttpStatusCode Status, string Body)> _routes = new();
        public List<HttpRequestMessage> Requests { get; } = new();

        public StubHandler Map(string pathAndQuery, string body, HttpStatusCode status = HttpStatusCode.OK)
        {
            _routes[pathAndQuery] = (status, body);
            return this;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request);
            var key = request.RequestUri!.PathAndQuery;
            if (!_routes.TryGetValue(key, out var route))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)
                {
                    Content = new StringContent($$"""{"error":"no stub for {{key}}"}""", Encoding.UTF8, "application/json"),
                });
            return Task.FromResult(new HttpResponseMessage(route.Status)
            {
                Content = new StringContent(route.Body, Encoding.UTF8, "application/json"),
            });
        }
    }

    private static HttpConnection Connect(StubHandler handler, string? apiKey = null) => new(
        new ConnectionDescriptor { Name = "stub", Kind = ConnectionKind.Http, Url = "http://stub:5001" },
        apiKey,
        handler);

    [Fact]
    public async Task GetDatabases_Parses_Registry_Shape()
    {
        var handler = new StubHandler().Map("/databases",
            """{"default":"orders","count":2,"databases":[{"name":"orders","filePath":"/data/orders.dfdb","isDefault":true},{"name":"crm","filePath":"/data/crm.dfdb","isDefault":false}]}""");
        await using var connection = Connect(handler);

        var databases = await connection.GetDatabasesAsync();

        Assert.Equal(2, databases.Count);
        Assert.True(databases[0].IsDefault);
        Assert.Equal("crm", databases[1].Name);
    }

    [Fact]
    public async Task Execute_Parses_Query_Envelope_And_Documents()
    {
        var handler = new StubHandler().Map("/db/orders/query",
            """{"database":"orders","success":true,"count":1,"affected":1,"plan":"INDEX_SCAN(idx_pnr)","executionTimeMs":0.42,"message":null,"documents":[{"_id":"abc","pnr":"ABC123"}]}""");
        await using var connection = Connect(handler);

        var result = await connection.ExecuteAsync("orders", "SELECT * FROM orders WHERE pnr = 'ABC123'");

        Assert.True(result.Success);
        Assert.Equal("INDEX_SCAN(idx_pnr)", result.Plan);
        Assert.Equal(0.42, result.ExecutionMs);
        Assert.Contains("ABC123", Assert.Single(result.Documents));
    }

    [Fact]
    public async Task Execute_Maps_Error_Body_To_Exception_Message()
    {
        var handler = new StubHandler().Map("/db/orders/query",
            """{"error":"Unknown collection 'nope'"}""", HttpStatusCode.BadRequest);
        await using var connection = Connect(handler);

        var ex = await Assert.ThrowsAsync<DfHttpException>(
            () => connection.ExecuteAsync("orders", "SELECT * FROM nope"));

        Assert.Equal(HttpStatusCode.BadRequest, ex.StatusCode);
        Assert.Equal("Unknown collection 'nope'", ex.Message);
    }

    [Fact]
    public async Task GetIndexes_Uses_Scoped_Route_And_Parses_Flat_Shape()
    {
        var handler = new StubHandler().Map("/db/crm/indexes/users",
            """{"database":"crm","collection":"users","indexes":[{"name":"idx_email","path":"email","unique":true}]}""");
        await using var connection = Connect(handler);

        var indexes = await connection.GetIndexesAsync("crm", "users");

        var index = Assert.Single(indexes);
        Assert.Equal("idx_email", index.Name);
        Assert.Equal("email", index.JsonPath);
        Assert.True(index.IsUnique);
    }

    [Fact]
    public async Task GetStats_Uses_Scoped_Route()
    {
        var handler = new StubHandler().Map("/db/crm/stats",
            """{"database":"crm","filePath":"/data/crm.dfdb","fileSizeBytes":8192,"fileSizeMb":0.01,"pageCount":1,"cachedPages":1,"dirtyPages":0,"collections":[{"name":"users","documentCount":3,"indexCount":1,"indexes":[]}]}""");
        await using var connection = Connect(handler);

        var stats = await connection.GetStatsAsync("crm");

        Assert.Equal(8192, stats.FileSize);
        var users = Assert.Single(stats.Collections);
        Assert.Equal(3, users.DocumentCount);
        Assert.Equal(1, users.IndexCount);
    }

    [Fact]
    public async Task Health_Degraded_503_Is_Reported_Not_Thrown()
    {
        var handler = new StubHandler().Map("/health",
            """{"status":"degraded","node":"n1","version":"1.4.0","health":{"state":"Failed","lastFailure":"disk full"}}""",
            HttpStatusCode.ServiceUnavailable);
        await using var connection = Connect(handler);

        var health = await connection.GetHealthAsync();

        Assert.False(health.Healthy);
        Assert.Equal("degraded", health.Status);
        Assert.Equal("disk full", health.Detail);
    }

    [Fact]
    public async Task ApiKey_Is_Sent_As_Bearer()
    {
        var handler = new StubHandler().Map("/databases",
            """{"default":null,"count":0,"databases":[]}""");
        await using var connection = Connect(handler, apiKey: "sekrit");

        await connection.GetDatabasesAsync();

        var request = Assert.Single(handler.Requests);
        Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
        Assert.Equal("sekrit", request.Headers.Authorization?.Parameter);
    }

    // Shape copied from ServeCommand.BuildConfigView (issue #111).
    private const string ConfigBody =
        """
        {
          "node": {"nodeName":"n1","port":5099,"dataDir":"/data","bindAllInterfaces":false,"insecureDevMode":false,"httpEndpoint":"http://n1:5099"},
          "security": {
            "adminKeyConfigured":true,"adminKeyFingerprint":"sha256:ab12cd34",
            "replicationSecretConfigured":false,"replicationSecretFingerprint":null,
            "tls":{"configured":false,"certPath":null,"certPasswordConfigured":false},
            "scopedKeys":[{"description":"ro key","scopes":["db:orders"],"keyFingerprint":"sha256:00ff00ff"}]
          },
          "replication": {"role":"leader","port":6000,"leaderHost":null,"leaderPort":null,"minSyncReplicas":1,"syncTimeoutSeconds":5,"autoFailover":null},
          "network": {"publicBaseUrl":null},
          "restartRequired": ["port","dataDir","bindAllInterfaces","insecureDevMode","nodeName","apiKey","replicationSecret","tls","scopedKeys","role","leaderHost","leaderPort","replicationPort","publicBaseUrl"],
          "liveEditable": ["minSyncReplicas","syncTimeoutSeconds"]
        }
        """;

    [Fact]
    public async Task GetServiceConfig_Parses_Redacted_View()
    {
        var handler = new StubHandler().Map("/admin/config", ConfigBody);
        await using var connection = Connect(handler);

        var config = await connection.GetServiceConfigAsync();

        Assert.Equal("n1", config.NodeName);
        Assert.Equal(5099, config.Port);
        Assert.Equal("/data", config.DataDir);
        Assert.True(config.AdminKeyConfigured);
        Assert.Equal("sha256:ab12cd34", config.AdminKeyFingerprint);
        Assert.False(config.ReplicationSecretConfigured);
        Assert.False(config.TlsConfigured);
        var key = Assert.Single(config.ScopedKeys);
        Assert.Equal("ro key", key.Description);
        Assert.Equal("db:orders", Assert.Single(key.Scopes));
        Assert.Equal("leader", config.ReplicationRole);
        Assert.Equal(6000, config.ReplicationPort);
        Assert.Equal(1, config.MinSyncReplicas);
        Assert.Equal(5, config.SyncTimeoutSeconds);
        Assert.Contains("port", config.RestartRequired);
        Assert.Contains("minSyncReplicas", config.LiveEditable);
    }

    [Fact]
    public async Task UpdateServiceConfig_Sends_Only_Provided_Fields_And_Parses_Result()
    {
        var handler = new StubHandler().Map("/admin/config",
            $$"""{"saved":true,"applied":["minSyncReplicas"],"config":{{ConfigBody}}}""");
        await using var connection = Connect(handler);

        var config = await connection.UpdateServiceConfigAsync(minSyncReplicas: 1, syncTimeoutSeconds: null);

        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Put, request.Method);
        var sent = await request.Content!.ReadAsStringAsync();
        Assert.Contains("minSyncReplicas", sent);
        Assert.DoesNotContain("syncTimeoutSeconds", sent);
        Assert.Equal(1, config.MinSyncReplicas);
    }

    [Fact]
    public async Task UpdateServiceConfig_With_No_Fields_Throws_Without_A_Request()
    {
        var handler = new StubHandler();
        await using var connection = Connect(handler);

        await Assert.ThrowsAsync<ArgumentException>(
            () => connection.UpdateServiceConfigAsync(null, null));
        Assert.Empty(handler.Requests);
    }

    // ── API-key normalisation ────────────────────────────────────────────────
    // Keys are hash/exact-matched server-side, so copy-paste whitespace or a
    // user-typed "Bearer " prefix (the old 401 text invited exactly that) used
    // to yield an unexplainable 401 with a perfectly valid key.

    [Theory]
    [InlineData("df_abc123", "df_abc123")]
    [InlineData("  df_abc123\r\n", "df_abc123")]                 // copy-paste whitespace
    [InlineData("Bearer df_abc123", "df_abc123")]                // typed the 401 hint verbatim
    [InlineData("bearer  df_abc123 ", "df_abc123")]              // case + inner spacing
    [InlineData("8340c13f386a2ef99bb32147", "8340c13f386a2ef99bb32147")] // legacy hex key untouched
    public async Task ApiKey_Is_Normalised_Before_The_Authorization_Header(string typed, string expected)
    {
        var handler = new StubHandler().Map("/health", """{"status":"ok"}""");
        await using var connection = Connect(handler, typed);

        await connection.GetHealthAsync();

        var auth = handler.Requests.Single().Headers.Authorization;
        Assert.NotNull(auth);
        Assert.Equal("Bearer", auth!.Scheme);
        Assert.Equal(expected, auth.Parameter);
    }

    [Fact]
    public async Task RestartServer_Posts_Admin_Restart_And_Returns_Message()
    {
        var handler = new StubHandler().Map("/admin/restart",
            """{"success":true,"message":"Flushed; the node is exiting for restart."}""",
            HttpStatusCode.Accepted);
        await using var connection = Connect(handler, "df_abc123");

        var message = await connection.RestartServerAsync();

        Assert.Contains("exiting for restart", message);
        Assert.Equal(HttpMethod.Post, handler.Requests.Single().Method);
    }
}
