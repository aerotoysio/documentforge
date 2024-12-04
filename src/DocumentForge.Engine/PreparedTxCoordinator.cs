using System.Threading.Channels;
using DocumentForge.Engine.Cluster;
using DocumentForge.Transactions;

namespace DocumentForge.Engine;

/// <summary>
/// Outcome of a Prepare attempt on a participant.
/// </summary>
public enum PrepareVote
{
    Prepared,
    Aborted
}

public sealed record PrepareResult(PrepareVote Vote, string? AbortReason)
{
    public static PrepareResult Yes() => new(PrepareVote.Prepared, null);
    public static PrepareResult No(string reason) => new(PrepareVote.Aborted, reason);
}

/// <summary>
/// Per-shard owner of in-flight prepared transactions.
///
/// <para>
/// 2PC requires that a participant hold its write lock continuously from
/// PREPARE through COMMIT/ROLLBACK so concurrent writers can't invalidate
/// the staged validation. .NET's <see cref="ReaderWriterLockSlim"/> has
/// thread affinity (you can't acquire on thread A and release on thread B),
/// which would force us to either replace it (regressing every read path)
/// or layer a SemaphoreSlim on top (regressing every write path).
/// </para>
///
/// <para>
/// Instead we use the actor pattern: a single background thread owns the
/// prepared-tx lifecycle. PREPARE/COMMIT/ROLLBACK are messages on a queue;
/// the worker acquires the existing write lock when it picks up a PREPARE,
/// holds it while waiting for the COMMIT/ROLLBACK message, then applies +
/// releases. The non-tx hot paths (single-doc Insert, single-node tx
/// commit) are unchanged — the lock primitive is the same one they
/// already use; the worker thread doesn't even exist until the first
/// PREPARE arrives.
/// </para>
///
/// <para>
/// Phase B happy path: at most one prepared tx in flight at a time. A
/// concurrent PREPARE while one's already in flight gets ABORT(busy) —
/// a clean retry signal for the coordinator. Phase D revisits this when
/// adding the timeout policy.
/// </para>
/// </summary>
internal sealed class PreparedTxCoordinator : IDisposable
{
    private readonly DocumentForgeDb _db;
    private readonly TransactionManager _txManager;
    private readonly PreparedTxLog _log;
    private readonly Channel<PreparedTxCommand> _queue;
    private Thread? _worker;
    private readonly object _startLock = new();
    private bool _disposed;

    // Worker-thread-only state. The pre-computed working sets are held here
    // so HandleResolve can apply them without rebuilding (and without a
    // re-scan for DeleteByField — which would race other threads since we
    // hold the write lock for writes only, not reads).
    private InFlightTx? _active;

    public PreparedTxCoordinator(DocumentForgeDb db, TransactionManager txManager, PreparedTxLog log)
    {
        _db = db;
        _txManager = txManager;
        _log = log;
        _queue = Channel.CreateUnbounded<PreparedTxCommand>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false
        });
    }

    private void EnsureWorker()
    {
        if (_worker is not null) return;
        lock (_startLock)
        {
            if (_worker is not null) return;
            _worker = new Thread(WorkerLoop)
            {
                IsBackground = true,
                Name = $"prepared-tx ({_log.Path})"
            };
            _worker.Start();
        }
    }

    public PrepareResult Prepare(string txId, string coordinatorShardId, IReadOnlyList<ShardTxOp> ops)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(PreparedTxCoordinator));
        EnsureWorker();
        var cmd = new PrepareCommand(txId, coordinatorShardId, ops);
        if (!_queue.Writer.TryWrite(cmd))
            throw new InvalidOperationException("Could not enqueue prepare command");
        return cmd.Result.Task.GetAwaiter().GetResult();
    }

    public void CommitPrepared(string txId)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(PreparedTxCoordinator));
        EnsureWorker();
        var cmd = new ResolveCommand(txId, commit: true);
        if (!_queue.Writer.TryWrite(cmd))
            throw new InvalidOperationException("Could not enqueue commit command");
        cmd.Done.Task.GetAwaiter().GetResult();
    }

    public void RollbackPrepared(string txId)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(PreparedTxCoordinator));
        EnsureWorker();
        var cmd = new ResolveCommand(txId, commit: false);
        if (!_queue.Writer.TryWrite(cmd))
            throw new InvalidOperationException("Could not enqueue rollback command");
        cmd.Done.Task.GetAwaiter().GetResult();
    }

    private void WorkerLoop()
    {
        try
        {
            while (true)
            {
                if (!_queue.Reader.WaitToReadAsync().AsTask().GetAwaiter().GetResult())
                    return; // queue closed
                while (_queue.Reader.TryRead(out var cmd))
                {
                    try
                    {
                        switch (cmd)
                        {
                            case PrepareCommand p:
                                HandlePrepare(p);
                                break;
                            case ResolveCommand r:
                                HandleResolve(r);
                                break;
                        }
                    }
                    catch (Exception ex)
                    {
                        // Ensure we don't deadlock the caller — surface failures.
                        cmd.Fail(ex);
                    }
                }
            }
        }
        finally
        {
            // If we're shutting down with an active prepared tx, release its
            // lock so the engine can dispose cleanly. The on-disk record stays
            // (recovery in Phase C decides what to do with it).
            if (_active is not null)
            {
                try { _txManager.ReleaseWriteLock(); } catch { }
                _active = null;
            }
        }
    }

    private void HandlePrepare(PrepareCommand cmd)
    {
        if (_active is not null)
        {
            cmd.Result.SetResult(PrepareResult.No(
                $"another tx is already prepared on this shard ({_active.TxId})"));
            return;
        }

        _txManager.AcquireWriteLock();
        bool lockHeld = true;
        try
        {
            // Build working sets first — DeleteByField needs to scan the
            // collection while the lock is held so races can't add new
            // matching docs after our snapshot.
            var workingSets = _db.BuildWorkingSetsFromShardTxOps(cmd.Ops);

            // Validate against the current committed state, simulating the
            // post-apply effect of the staged ops. Reuses the same validator
            // Phase 1's single-node tx commit path uses.
            _db.ValidateUniqueIndexesForWorkingSets(workingSets);

            // Persist before responding PREPARED. Once the coordinator hears
            // PREPARED, this record must outlive a crash — that's the whole
            // point of the durability fence.
            _log.AppendPrepared(cmd.TxId, cmd.CoordinatorShardId, cmd.Ops);

            _active = new InFlightTx(cmd.TxId, cmd.CoordinatorShardId, cmd.Ops, workingSets);
            cmd.Result.SetResult(PrepareResult.Yes());
            // Lock stays held — released by HandleResolve.
            lockHeld = false; // mark to avoid release in catch
        }
        catch (Exception ex)
        {
            cmd.Result.SetResult(PrepareResult.No(ex.Message));
        }
        finally
        {
            if (lockHeld) _txManager.ReleaseWriteLock();
        }
    }

    private void HandleResolve(ResolveCommand cmd)
    {
        if (_active is null || _active.TxId != cmd.TxId)
        {
            cmd.Done.SetException(new InvalidOperationException(
                $"No prepared tx with id '{cmd.TxId}' on this shard."));
            return;
        }

        try
        {
            if (cmd.Commit)
            {
                _db.ApplyWorkingSetsLocked(_active.WorkingSets);
            }
            _log.AppendResolved(_active.TxId, cmd.Commit);
            cmd.Done.SetResult(null);
        }
        catch (Exception ex)
        {
            cmd.Done.SetException(ex);
        }
        finally
        {
            try { _txManager.ReleaseWriteLock(); } catch { }
            _active = null;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _queue.Writer.TryComplete();
        try { _worker?.Join(TimeSpan.FromSeconds(5)); } catch { }
    }

    private abstract class PreparedTxCommand
    {
        public abstract void Fail(Exception ex);
    }

    private sealed class PrepareCommand : PreparedTxCommand
    {
        public string TxId { get; }
        public string CoordinatorShardId { get; }
        public IReadOnlyList<ShardTxOp> Ops { get; }
        public TaskCompletionSource<PrepareResult> Result { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public PrepareCommand(string txId, string coordinatorShardId, IReadOnlyList<ShardTxOp> ops)
        {
            TxId = txId;
            CoordinatorShardId = coordinatorShardId;
            Ops = ops;
        }

        public override void Fail(Exception ex) => Result.TrySetException(ex);
    }

    private sealed class ResolveCommand : PreparedTxCommand
    {
        public string TxId { get; }
        public bool Commit { get; }
        public TaskCompletionSource<object?> Done { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ResolveCommand(string txId, bool commit)
        {
            TxId = txId;
            Commit = commit;
        }

        public override void Fail(Exception ex) => Done.TrySetException(ex);
    }

    private sealed record InFlightTx(
        string TxId,
        string CoordinatorShardId,
        IReadOnlyList<ShardTxOp> Ops,
        IReadOnlyDictionary<string, DocumentForge.Transactions.CollectionWorkingSet> WorkingSets);
}
