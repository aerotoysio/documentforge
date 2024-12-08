using DocumentForge.Engine;
using DocumentForge.Engine.Cluster;

namespace DocumentForge.Api;

/// <summary>
/// REST endpoints that back <see cref="HttpShardTransport"/>'s 2PC wire ops
/// (issue #14 Phase E.1). Extracted so the same setup can be wired into
/// integration tests against a temporary <see cref="DocumentForgeDb"/>.
///
/// <para>
/// Endpoints (Bearer auth and TLS apply via the same middleware as the
/// rest of the API):
/// </para>
/// <list type="bullet">
///   <item><c>POST /tx/execute</c> — single-shard fast path</item>
///   <item><c>POST /tx/prepare</c> — 2PC phase 1</item>
///   <item><c>POST /tx/{txId}/commit</c> — 2PC phase 2 commit</item>
///   <item><c>POST /tx/{txId}/rollback</c> — 2PC phase 2 rollback</item>
///   <item><c>POST /tx/{txId}/coord-decision</c> — coordinator decision log</item>
///   <item><c>POST /tx/{txId}/coord-done</c> — coordinator done log</item>
///   <item><c>GET /tx/in-flight</c> — recovery scan (PREPARED records)</item>
///   <item><c>GET /tx/{txId}/coord-state</c> — recovery query for decision</item>
/// </list>
/// </summary>
public static class TransactionEndpoints
{
    public static void Map(WebApplication app, DocumentForgeDb db)
    {
        app.MapPost("/tx/execute", (ExecuteTransactionRequest request) =>
        {
            if (request?.Ops is null) return Results.BadRequest(new { error = "Missing 'ops'" });
            try
            {
                var ops = HttpWire.FromDtos(request.Ops);
                var dummyTransport = new InProcessShardTransport("self", db);
                dummyTransport.ExecuteTransaction(ops);
                return Results.Ok(new { success = true });
            }
            catch (Exception ex) { return Results.BadRequest(new { error = ex.Message }); }
        });

        app.MapPost("/tx/prepare", (PrepareRequest request) =>
        {
            if (string.IsNullOrEmpty(request?.TxId)) return Results.BadRequest(new { error = "Missing 'txId'" });
            try
            {
                var ops = HttpWire.FromDtos(request.Ops);
                var timeout = TimeSpan.FromMilliseconds(request.TimeoutMs > 0 ? request.TimeoutMs : 30_000);
                var result = db.PrepareTransaction(request.TxId, request.CoordinatorShardId, ops, timeout);
                return Results.Ok(new PrepareResponse
                {
                    Vote = result.Vote.ToString(),
                    AbortReason = result.AbortReason
                });
            }
            catch (Exception ex) { return Results.BadRequest(new { error = ex.Message }); }
        });

        app.MapPost("/tx/{txId}/commit", (string txId) =>
        {
            try { db.CommitPreparedTransaction(txId); return Results.Ok(new { success = true }); }
            catch (Exception ex) { return Results.BadRequest(new { error = ex.Message }); }
        });

        app.MapPost("/tx/{txId}/rollback", (string txId) =>
        {
            try { db.RollbackPreparedTransaction(txId); return Results.Ok(new { success = true }); }
            catch (Exception ex) { return Results.BadRequest(new { error = ex.Message }); }
        });

        app.MapPost("/tx/{txId}/coord-decision", (string txId, CoordinatorDecisionRequest request) =>
        {
            try { db.RecordCoordinatorDecision(txId, request?.Commit ?? false); return Results.Ok(new { success = true }); }
            catch (Exception ex) { return Results.BadRequest(new { error = ex.Message }); }
        });

        app.MapPost("/tx/{txId}/coord-done", (string txId) =>
        {
            try { db.RecordCoordinatorDone(txId); return Results.Ok(new { success = true }); }
            catch (Exception ex) { return Results.BadRequest(new { error = ex.Message }); }
        });

        app.MapGet("/tx/in-flight", () =>
        {
            try
            {
                var records = db.ScanInFlightPreparedTransactions();
                return Results.Ok(new InFlightPreparedResponse
                {
                    Records = records.Select(r => new PreparedTxRecordDto
                    {
                        TxId = r.TxId,
                        CoordinatorShardId = r.CoordinatorShardId,
                        Ops = HttpWire.ToDtos(r.Ops).ToList()
                    }).ToList()
                });
            }
            catch (Exception ex) { return Results.BadRequest(new { error = ex.Message }); }
        });

        app.MapGet("/tx/{txId}/coord-state", (string txId) =>
        {
            var state = db.GetCoordinatorTxState(txId);
            if (state is null) return Results.NotFound();
            return Results.Ok(new CoordinatorTxStateDto
            {
                TxId = state.TxId,
                Decided = state.Decided,
                Done = state.Done
            });
        });
    }
}
