using DocumentForge.Core;

namespace DocumentForge.Transactions;

public enum TransactionState
{
    Active,
    Committed,
    RolledBack
}

public sealed class Transaction : IDisposable
{
    private readonly TransactionManager _manager;
    public ulong Id { get; }
    public TransactionState State { get; private set; } = TransactionState.Active;

    internal Transaction(ulong id, TransactionManager manager)
    {
        Id = id;
        _manager = manager;
    }

    public void Commit()
    {
        if (State != TransactionState.Active)
            throw new TransactionException($"Cannot commit transaction {Id}: state is {State}");
        _manager.CommitTransaction(this);
        State = TransactionState.Committed;
    }

    public void Rollback()
    {
        if (State != TransactionState.Active)
            throw new TransactionException($"Cannot rollback transaction {Id}: state is {State}");
        _manager.RollbackTransaction(this);
        State = TransactionState.RolledBack;
    }

    public void Dispose()
    {
        if (State == TransactionState.Active)
            Rollback();
    }
}

public sealed class TransactionManager
{
    private readonly WalWriter? _wal;
    private readonly ReaderWriterLockSlim _dbLock = new();
    private ulong _nextTransactionId = 1;

    public TransactionManager(WalWriter? wal = null)
    {
        _wal = wal;
    }

    public Transaction BeginTransaction()
    {
        var id = Interlocked.Increment(ref _nextTransactionId);
        _wal?.WriteBegin(id);
        return new Transaction(id, this);
    }

    internal void CommitTransaction(Transaction tx)
    {
        _wal?.WriteCommit(tx.Id);
    }

    internal void RollbackTransaction(Transaction tx)
    {
        _wal?.WriteRollback(tx.Id);
    }

    public void AcquireReadLock() => _dbLock.EnterReadLock();
    public void ReleaseReadLock() => _dbLock.ExitReadLock();
    public void AcquireWriteLock() => _dbLock.EnterWriteLock();
    public void ReleaseWriteLock() => _dbLock.ExitWriteLock();
}
