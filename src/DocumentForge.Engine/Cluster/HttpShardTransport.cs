using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using DocumentForge.Core;
using DocumentForge.Document;
using DocumentForge.Query;

namespace DocumentForge.Engine.Cluster;

/// <summary>
/// Shard transport that talks to a remote DocumentForge.Api instance over HTTP.
///
/// Pairs with the DocumentForge.Api sample - the same endpoints the REPL and
/// Postman hit. Each shard in a distributed cluster runs its own Api process;
/// the cluster router creates an HttpShardTransport per shard.
/// </summary>
public sealed class HttpShardTransport : IShardTransport
{
    private readonly HttpClient _client;
    private readonly bool _ownsClient;

    public string ShardName { get; }
    public Uri BaseAddress => _client.BaseAddress!;

    public HttpShardTransport(string shardName, string baseUrl, HttpClient? httpClient = null, string? apiKey = null)
    {
        ShardName = shardName;
        if (httpClient is null)
        {
            _client = new HttpClient { BaseAddress = new Uri(baseUrl) };
            _ownsClient = true;
        }
        else
        {
            _client = httpClient;
            _client.BaseAddress ??= new Uri(baseUrl);
            _ownsClient = false;
        }
        if (!string.IsNullOrEmpty(apiKey))
            _client.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);
    }

    public QueryResult Execute(string sql)
    {
        var request = new { sql };
        var response = _client.PostAsJsonAsync("/query", request).GetAwaiter().GetResult();
        var body = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();

        if (!response.IsSuccessStatusCode)
            return QueryResult.Error($"[{ShardName}] HTTP {(int)response.StatusCode}: {body}");

        using var json = JsonDocument.Parse(body);
        var root = json.RootElement;

        var docs = new List<BsonDocument>();
        if (root.TryGetProperty("documents", out var docsEl) && docsEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var d in docsEl.EnumerateArray())
                docs.Add(BsonDocument.FromJson(d.GetRawText()));
        }

        var plan = root.TryGetProperty("plan", out var p) ? p.GetString() : null;
        var affected = root.TryGetProperty("affected", out var a) && a.ValueKind == JsonValueKind.Number
            ? a.GetInt64() : 0;
        var execMs = root.TryGetProperty("executionTimeMs", out var t) && t.ValueKind == JsonValueKind.Number
            ? t.GetDouble() : 0;

        return new QueryResult
        {
            Documents = docs,
            AffectedCount = affected,
            QueryPlan = plan,
            Success = true,
            ExecutionTime = TimeSpan.FromMilliseconds(execMs)
        };
    }

    public DocumentId Insert(string collectionName, BsonDocument doc)
    {
        // Make sure _id is set BEFORE sending so the server uses our id (not its own)
        doc.EnsureId();
        var id = doc.GetId();
        var json = doc.ToJson();
        var content = new StringContent(json, Encoding.UTF8, "application/json");

        var response = _client.PostAsync($"/collections/{collectionName}", content).GetAwaiter().GetResult();
        if (!response.IsSuccessStatusCode)
        {
            var err = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            throw new DocumentForgeException($"[{ShardName}] Insert failed: HTTP {(int)response.StatusCode}: {err}");
        }
        return id;
    }

    public bool DeleteById(string collectionName, DocumentId id)
    {
        var response = _client.DeleteAsync($"/collections/{collectionName}/{id}").GetAwaiter().GetResult();
        return response.IsSuccessStatusCode;
    }

    // --- 2PC wire ops over HTTP (issue #14 Phase E.1) ---
    //
    // These mirror the participant-side methods on DocumentForgeDb, sending
    // the same ops over the network instead of in-process. The cluster
    // client doesn't care which transport it's talking to — both implement
    // IShardTransport identically.

    public void ExecuteTransaction(IReadOnlyList<ShardTxOp> ops)
    {
        var req = new ExecuteTransactionRequest { Ops = HttpWire.ToDtos(ops).ToList() };
        var response = _client.PostAsJsonAsync("/tx/execute", req, HttpWire.JsonOptions).GetAwaiter().GetResult();
        ThrowIfNotSuccess(response, nameof(ExecuteTransaction));
    }

    public PrepareResult Prepare(string txId, string coordinatorShardId, IReadOnlyList<ShardTxOp> ops, TimeSpan timeout)
    {
        var req = new PrepareRequest
        {
            TxId = txId,
            CoordinatorShardId = coordinatorShardId,
            Ops = HttpWire.ToDtos(ops).ToList(),
            TimeoutMs = (long)timeout.TotalMilliseconds
        };
        var response = _client.PostAsJsonAsync("/tx/prepare", req, HttpWire.JsonOptions).GetAwaiter().GetResult();
        var body = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
        if (!response.IsSuccessStatusCode)
            throw new DocumentForgeException($"[{ShardName}] Prepare failed: HTTP {(int)response.StatusCode}: {body}");
        var dto = JsonSerializer.Deserialize<PrepareResponse>(body, HttpWire.JsonOptions)!;
        var vote = dto.Vote == "Prepared" ? PrepareVote.Prepared : PrepareVote.Aborted;
        return new PrepareResult(vote, dto.AbortReason);
    }

    public PrepareResult Prepare(string txId, string coordinatorShardId, IReadOnlyList<ShardTxOp> ops) =>
        Prepare(txId, coordinatorShardId, ops, TimeSpan.FromSeconds(30));

    public void CommitPrepared(string txId)
    {
        var response = _client.PostAsync($"/tx/{txId}/commit", null).GetAwaiter().GetResult();
        ThrowIfNotSuccess(response, nameof(CommitPrepared));
    }

    public void RollbackPrepared(string txId)
    {
        var response = _client.PostAsync($"/tx/{txId}/rollback", null).GetAwaiter().GetResult();
        ThrowIfNotSuccess(response, nameof(RollbackPrepared));
    }

    public void RecordCoordinatorDecision(string txId, bool commit)
    {
        var req = new CoordinatorDecisionRequest { Commit = commit };
        var response = _client.PostAsJsonAsync($"/tx/{txId}/coord-decision", req, HttpWire.JsonOptions).GetAwaiter().GetResult();
        ThrowIfNotSuccess(response, nameof(RecordCoordinatorDecision));
    }

    public void RecordCoordinatorDone(string txId)
    {
        var response = _client.PostAsync($"/tx/{txId}/coord-done", null).GetAwaiter().GetResult();
        ThrowIfNotSuccess(response, nameof(RecordCoordinatorDone));
    }

    public IReadOnlyList<PreparedTxRecord> ScanInFlightPrepared()
    {
        var response = _client.GetAsync("/tx/in-flight").GetAwaiter().GetResult();
        var body = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
        if (!response.IsSuccessStatusCode)
            throw new DocumentForgeException($"[{ShardName}] ScanInFlightPrepared failed: HTTP {(int)response.StatusCode}: {body}");
        var dto = JsonSerializer.Deserialize<InFlightPreparedResponse>(body, HttpWire.JsonOptions)!;
        return dto.Records.Select(r => new PreparedTxRecord(
            r.TxId,
            r.CoordinatorShardId,
            HttpWire.FromDtos(r.Ops))).ToList();
    }

    public CoordinatorTxState? GetCoordinatorTxState(string txId)
    {
        var response = _client.GetAsync($"/tx/{txId}/coord-state").GetAwaiter().GetResult();
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
        var body = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
        if (!response.IsSuccessStatusCode)
            throw new DocumentForgeException($"[{ShardName}] GetCoordinatorTxState failed: HTTP {(int)response.StatusCode}: {body}");
        var dto = JsonSerializer.Deserialize<CoordinatorTxStateDto>(body, HttpWire.JsonOptions)!;
        return new CoordinatorTxState(dto.TxId, dto.Decided, dto.Done);
    }

    private void ThrowIfNotSuccess(HttpResponseMessage response, string opName)
    {
        if (response.IsSuccessStatusCode) return;
        var body = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
        throw new DocumentForgeException($"[{ShardName}] {opName} failed: HTTP {(int)response.StatusCode}: {body}");
    }

    // --- Operator endpoints (Phase E.2) ---
    // Not on IShardTransport — these are administrative, not routing.
    // Available on the concrete HttpShardTransport for ergonomics; the
    // C# API has equivalents on DocumentForgeDb directly.

    /// <summary>
    /// Operator-driven manual abort. For the rare case where a participant
    /// is stuck PREPARED past the timeout and an operator wants to free
    /// the lock manually. The server refuses (409 Conflict) if this shard
    /// recorded COMMIT_DECISION for the tx — aborting after a decision
    /// would make the cluster inconsistent.
    /// </summary>
    public void OperatorAbort(string txId)
    {
        var response = _client.PostAsync($"/tx/{txId}/abort", null).GetAwaiter().GetResult();
        if (response.StatusCode == System.Net.HttpStatusCode.Conflict)
        {
            var body = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            throw new DocumentForgeException($"[{ShardName}] OperatorAbort refused: {body}");
        }
        ThrowIfNotSuccess(response, nameof(OperatorAbort));
    }

    /// <summary>Operator-facing snapshot of participant-side 2PC counters.</summary>
    public PreparedTxStats GetStats()
    {
        var response = _client.GetAsync("/tx/stats").GetAwaiter().GetResult();
        var body = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
        if (!response.IsSuccessStatusCode)
            throw new DocumentForgeException($"[{ShardName}] GetStats failed: HTTP {(int)response.StatusCode}: {body}");
        using var json = JsonDocument.Parse(body);
        var root = json.RootElement;
        long Read(string name) => root.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.Number ? p.GetInt64() : 0;
        return new PreparedTxStats
        {
            PrepareTotal = Read("prepareTotal"),
            PrepareAbortedTotal = Read("prepareAbortedTotal"),
            CommittedTotal = Read("committedTotal"),
            RolledBackTotal = Read("rolledBackTotal"),
            TimedOutTotal = Read("timedOutTotal"),
            InFlightPrepared = Read("inFlightPrepared")
        };
    }

    public DatabaseStatistics GetStatistics()
    {
        var response = _client.GetAsync("/stats").GetAwaiter().GetResult();
        var body = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
        if (!response.IsSuccessStatusCode)
            throw new DocumentForgeException($"[{ShardName}] Stats failed: HTTP {(int)response.StatusCode}: {body}");

        // Minimal parsing - we don't need every field round-tripped perfectly
        using var json = JsonDocument.Parse(body);
        var root = json.RootElement;
        var stats = new DatabaseStatistics
        {
            FilePath = root.TryGetProperty("filePath", out var fp) ? (fp.GetString() ?? "") : "",
            FileSize = root.TryGetProperty("fileSizeMb", out var fs) && fs.ValueKind == JsonValueKind.Number
                ? (long)(fs.GetDouble() * 1024 * 1024) : 0,
            PageCount = root.TryGetProperty("pageCount", out var pc) && pc.ValueKind == JsonValueKind.Number
                ? pc.GetUInt32() : 0,
            CachedPages = root.TryGetProperty("cachedPages", out var cp) && cp.ValueKind == JsonValueKind.Number
                ? cp.GetInt32() : 0
        };
        return stats;
    }

    public void Dispose()
    {
        if (_ownsClient) _client.Dispose();
    }
}
