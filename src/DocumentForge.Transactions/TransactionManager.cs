namespace DocumentForge.Transactions;

/// <summary>
/// Owns the database-wide reader/writer lock and hands out logical
/// transaction IDs that the engine threads through WAL records. The lock
/// is taken per-operation by single-statement APIs and once-around-the-batch
/// at <see cref="DocumentForge.Engine.DocumentForgeDb"/>.Commit time for
/// multi-doc transactions.
/// </summary>
public sealed class TransactionManager
{
    private readonly WalWriter? _wal;
    private readonly ReaderWriterLockSlim _dbLock = new();
    private ulong _nextTransactionId = 1;

    public TransactionManager(WalWriter? wal = null) => _wal = wal;

    public ulong NextTransactionId() => Interlocked.Increment(ref _nextTransactionId);

    public void WriteBegin(ulong txId) => _wal?.WriteBegin(txId);
    public void WriteCommit(ulong txId) => _wal?.WriteCommit(txId);
    public void WriteRollback(ulong txId) => _wal?.WriteRollback(txId);

    public void AcquireReadLock() => _dbLock.EnterReadLock();
    public void ReleaseReadLock() => _dbLock.ExitReadLock();
    public void AcquireWriteLock() => _dbLock.EnterWriteLock();
    public void ReleaseWriteLock() => _dbLock.ExitWriteLock();
}
