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
    private readonly PreparedTxStats _stats;
    private readonly Channel<PreparedTxCommand> _queue;
    private Thread? _worker;
    private readonly object _startLock = new();
    private bool _disposed;

    // Worker-thread-only state. The pre-computed working sets are held here
    // so HandleResolve can apply them without rebuilding (and without a
    // re-scan for DeleteByField — which would race other threads since we
    // hold the write lock for writes only, not reads).
    private InFlightTx? _active;

    public PreparedTxCoordinator(DocumentForgeDb db, TransactionManager txManager, PreparedTxLog log, PreparedTxStats stats)
    {
        _db = db;
        _txManager = txManager;
        _log = log;
        _stats = stats;
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

    public PrepareResult Prepare(string txId, string coordinatorShardId, IReadOnlyList<ShardTxOp> ops, TimeSpan timeout)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(PreparedTxCoordinator));
        EnsureWorker();
        var cmd = new PrepareCommand(txId, coordinatorShardId, ops, timeout);
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
                            case TimeoutCommand t:
                                HandleTimeout(t);
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
            // lock + cancel its timeout so the engine can dispose cleanly.
            // The on-disk PREPARED record stays (Recover decides what to
            // do with it on the next Open).
            if (_active is not null)
            {
                try { _active.TimeoutCts?.Cancel(); } catch { }
                try { _active.TimeoutCts?.Dispose(); } catch { }
                try { _txManager.ReleaseWriteLock(); } catch { }
                _active = null;
            }
        }
    }

    private void HandlePrepare(PrepareCommand cmd)
    {
        Interlocked.Increment(ref _stats.PrepareTotal);

        if (_active is not null)
        {
            Interlocked.Increment(ref _stats.PrepareAbortedTotal);
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

            // Set up the Phase D self-abort timer. The CTS is canceled by
            // HandleResolve on a clean commit/rollback so we don't leak the
            // pending Task.Delay. If the deadline fires first, the
            // continuation enqueues a TimeoutCommand that the worker reads
            // and self-aborts on.
            var timeoutCts = new CancellationTokenSource();
            _active = new InFlightTx(cmd.TxId, cmd.CoordinatorShardId, cmd.Ops, workingSets, timeoutCts);
            Interlocked.Increment(ref _stats.InFlightPrepared);
            cmd.Result.SetResult(PrepareResult.Yes());
            // Lock stays held — released by HandleResolve or HandleTimeout.
            lockHeld = false; // mark to avoid release in catch

            var capturedTxId = cmd.TxId;
            var capturedTimeout = cmd.Timeout;
            _ = Task.Delay(capturedTimeout, timeoutCts.Token).ContinueWith(t =>
            {
                if (t.IsCanceled) return;
                if (_disposed) return;
                _queue.Writer.TryWrite(new TimeoutCommand(capturedTxId));
            }, TaskScheduler.Default);
        }
        catch (Exception ex)
        {
            Interlocked.Increment(ref _stats.PrepareAbortedTotal);
            cmd.Result.SetResult(PrepareResult.No(ex.Message));
        }
        finally
        {
            if (lockHeld) _txManager.ReleaseWriteLock();
        }
    }

    private void HandleResolve(ResolveCommand cmd)
    {
        // Recovery path: if there's no in-memory _active state but the txId
        // appears in the on-disk prepared.log, rebuild and acquire the
        // write lock fresh. This is what the cluster Recover sweep takes
        // after a restart — the worker thread that originally held the
        // lock is gone, but the durable PREPARED record is still on disk.
        bool reacquiredFresh = false;
        if (_active is null)
        {
            var inFlight = _log.ScanInFlight();
            var rec = inFlight.FirstOrDefault(r => r.TxId == cmd.TxId);
            if (rec is null)
            {
                cmd.Done.SetException(new InvalidOperationException(
                    $"No prepared tx with id '{cmd.TxId}' on this shard."));
                return;
            }
            _txManager.AcquireWriteLock();
            try
            {
                var workingSets = _db.BuildWorkingSetsFromShardTxOps(rec.Ops);
                _active = new InFlightTx(rec.TxId, rec.CoordinatorShardId, rec.Ops, workingSets);
                Interlocked.Increment(ref _stats.InFlightPrepared);
                reacquiredFresh = true;
            }
            catch
            {
                _txManager.ReleaseWriteLock();
                throw;
            }
        }

        if (_active.TxId != cmd.TxId)
        {
            cmd.Done.SetException(new InvalidOperationException(
                $"Prepared tx on this shard is '{_active.TxId}', not '{cmd.TxId}'."));
            // Don't release the lock — the OTHER prepared tx still holds it.
            // Just don't touch _active either.
            if (reacquiredFresh) { /* unreachable: we just set _active to cmd.TxId */ }
            return;
        }

        Exception? failure = null;
        try
        {
            if (cmd.Commit)
            {
                _db.ApplyWorkingSetsLocked(_active.WorkingSets);
                // Issues #89/#91 — seal the applied working set in the WAL
                // before acknowledging the coordinator; a participant must
                // not report "committed" for state that could evaporate.
                _db.CommitDurableLocked();
                Interlocked.Increment(ref _stats.CommittedTotal);
            }
            else
            {
                Interlocked.Increment(ref _stats.RolledBackTotal);
            }
            _log.AppendResolved(_active.TxId, cmd.Commit);
        }
        catch (Exception ex) { failure = ex; }

        // Cleanup BEFORE signaling the caller. With
        // RunContinuationsAsynchronously the awaiter can resume on a
        // different thread the instant SetResult is called — if cleanup
        // ran in finally, an observer's GetPreparedTxStats() could race
        // and see InFlightPrepared still at 1 even though the resolve
        // returned. Cleaning up first means the observed state matches
        // the API contract.
        try { _active?.TimeoutCts?.Cancel(); } catch { }
        try { _active?.TimeoutCts?.Dispose(); } catch { }
        try { _txManager.ReleaseWriteLock(); } catch { }
        _active = null;
        Interlocked.Decrement(ref _stats.InFlightPrepared);

        if (failure is not null) cmd.Done.SetException(failure);
        else cmd.Done.SetResult(null);
    }

    /// <summary>
    /// Phase D liveness: the prepare timeout fired before COMMIT or ROLLBACK
    /// arrived. Issue #97 — the participant must NOT unilaterally decide the
    /// outcome here. It cannot reach the coordinator's decision log, so a blind
    /// ROLLBACK could tear a transaction the coordinator already committed.
    ///
    /// <para>Instead we free the write lock (so the shard isn't wedged) but
    /// leave the PREPARED record in-doubt on disk — no RESOLVED marker. The
    /// authoritative resolution comes later from either a late
    /// CommitPrepared/RollbackPrepared (the coordinator's decision arriving
    /// after the deadline), or the cluster recovery sweep, which consults the
    /// coordinator log and only aborts when NO decision was recorded. Both use
    /// <see cref="HandleResolve"/>'s log-rebuild path, since <c>_active</c> is
    /// cleared here.</para>
    /// </summary>
    private void HandleTimeout(TimeoutCommand cmd)
    {
        if (_active is null || _active.TxId != cmd.TxId)
        {
            // Resolve raced and won; nothing to do.
            return;
        }

        // Do NOT AppendResolved — leaving the record PREPARED (in-doubt) is what
        // lets the coordinator's decision win. Just release the lock + drop the
        // in-memory slot; the durable prepared.log record survives for recovery.
        Interlocked.Increment(ref _stats.TimedOutTotal);
        try { _active?.TimeoutCts?.Dispose(); } catch { }
        try { _txManager.ReleaseWriteLock(); } catch { }
        _active = null;
        Interlocked.Decrement(ref _stats.InFlightPrepared);
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
        public TimeSpan Timeout { get; }
        public TaskCompletionSource<PrepareResult> Result { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public PrepareCommand(string txId, string coordinatorShardId, IReadOnlyList<ShardTxOp> ops, TimeSpan timeout)
        {
            TxId = txId;
            CoordinatorShardId = coordinatorShardId;
            Ops = ops;
            Timeout = timeout;
        }

        public override void Fail(Exception ex) => Result.TrySetException(ex);
    }

    private sealed class TimeoutCommand : PreparedTxCommand
    {
        public string TxId { get; }
        public TimeoutCommand(string txId) { TxId = txId; }
        public override void Fail(Exception ex) { /* internal — nobody waiting on this */ }
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
        IReadOnlyDictionary<string, DocumentForge.Transactions.CollectionWorkingSet> WorkingSets,
        // Null when this in-flight state was rebuilt during a Recover sweep
        // — recovery resolves immediately so no timer is set.
        CancellationTokenSource? TimeoutCts = null);
}
