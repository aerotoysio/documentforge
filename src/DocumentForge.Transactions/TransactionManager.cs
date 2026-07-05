namespace DocumentForge.Transactions;

/// <summary>
/// Owns the database-wide reader/writer lock and hands out logical
/// transaction IDs. The lock is taken per-operation by single-statement
/// APIs and once-around-the-batch at
/// <see cref="DocumentForge.Engine.DocumentForgeDb"/>.Commit time for
/// multi-doc transactions. Durability lives in the recovery log's commit
/// markers (issues #89/#90), not here — the old marker-only WalWriter
/// was dead code and is gone.
/// </summary>
public sealed class TransactionManager
{
    private readonly ReaderWriterLockSlim _dbLock = new();
    private ulong _nextTransactionId = 1;

    public ulong NextTransactionId() => Interlocked.Increment(ref _nextTransactionId);

    public void AcquireReadLock() => _dbLock.EnterReadLock();
    public void ReleaseReadLock() => _dbLock.ExitReadLock();
    public void AcquireWriteLock() => _dbLock.EnterWriteLock();
    public void ReleaseWriteLock() => _dbLock.ExitWriteLock();
}
