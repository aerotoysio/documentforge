namespace DocumentForge.Core;

public class DocumentForgeException : Exception
{
    public DocumentForgeException(string message) : base(message) { }
    public DocumentForgeException(string message, Exception innerException) : base(message, innerException) { }
}

public class PageCorruptionException : DocumentForgeException
{
    public PageId PageId { get; }
    public PageCorruptionException(PageId pageId, string message)
        : base($"Page {pageId} corruption: {message}") => PageId = pageId;
}

/// <summary>
/// Thrown by <see cref="Storage.DataFile.Open"/> when the data file's
/// header (page 0) is missing, truncated, has wrong magic bytes, or has an
/// unsupported format version. Distinct from <see cref="PageCorruptionException"/>
/// (which fires on a single page mid-operation) because a corrupt header
/// means the database itself is unsafe to open at all — the right operator
/// response is restore-from-backup, not retry.
///
/// <para>
/// Issue #57: pre-fix, header corruption silently fell through to the catalog
/// loader, which followed corrupt page-chain pointers into a cycle and hung
/// the whole process indefinitely. SQL-Server-grade behaviour is to refuse
/// the open with a clear, typed exception so the operator sees the problem
/// instantly rather than waiting on a deadlock.
/// </para>
/// </summary>
public class DatabaseCorruptedException : DocumentForgeException
{
    public string FilePath { get; }
    public DatabaseCorruptedException(string filePath, string message)
        : base($"Database file '{filePath}' is corrupted: {message}")
    {
        FilePath = filePath;
    }
}

public class DuplicateKeyException : DocumentForgeException
{
    public string IndexName { get; }
    public DuplicateKeyException(string indexName, string keyValue)
        : base($"Duplicate key '{keyValue}' in index '{indexName}'") => IndexName = indexName;
}

/// <summary>
/// Issue #106 — a document failed the opt-in schema/constraint validation
/// configured on its collection (missing required field, wrong field type, or a
/// failed CHECK). Thrown before the write touches storage, so the on-disk state
/// is untouched; the HTTP layer maps it to 400.
/// </summary>
public class SchemaViolationException : DocumentForgeException
{
    public string Collection { get; }
    public SchemaViolationException(string collection, string message)
        : base($"Schema violation in '{collection}': {message}") => Collection = collection;
}

public class CollectionNotFoundException : DocumentForgeException
{
    public string CollectionName { get; }
    public CollectionNotFoundException(string collectionName)
        : base($"Collection '{collectionName}' not found.") => CollectionName = collectionName;
}

/// <summary>
/// Issue #151 — a delete was refused because another document still references
/// the target (onDelete=restrict), or a setNull rewrite would break the
/// referencing collection's own schema. Raised during the plan phase, before
/// any mutation, so the on-disk state is untouched; the HTTP layer maps it
/// to 409 Conflict.
/// </summary>
public class ReferentialIntegrityException : DocumentForgeException
{
    public string Collection { get; }
    public ReferentialIntegrityException(string collection, string message)
        : base($"Referential integrity violation in '{collection}': {message}") => Collection = collection;
}

public class QueryParseException : DocumentForgeException
{
    public int Position { get; }
    public QueryParseException(string message, int position)
        : base($"Query parse error at position {position}: {message}") => Position = position;
}

public class TransactionException : DocumentForgeException
{
    public TransactionException(string message) : base(message) { }
}

/// <summary>
/// Engine-wide health state. <see cref="Healthy"/> is the normal mode.
/// <see cref="Failed"/> means a prior write hit an IOException (full disk,
/// transient I/O error, networked-filesystem hiccup) and the on-disk state
/// may be inconsistent in a way the engine can't safely continue past.
/// New writes throw <see cref="DatabaseHealthException"/>; the only recovery
/// is Dispose + Open, which runs the recovery-log replay and resets state.
/// Issue #25.
/// </summary>
public enum DatabaseHealthStatus
{
    Healthy,
    Failed,
}

/// <summary>
/// Thrown when a write is attempted against an engine that's flipped to
/// <see cref="DatabaseHealthStatus.Failed"/> by an earlier IOException.
/// Failing fast prevents the engine from continuing into a corrupted state;
/// the caller should Dispose and Open fresh, which lets the recovery log
/// repair anything torn.
/// </summary>
public class DatabaseHealthException : DocumentForgeException
{
    public DatabaseHealthException(string message) : base(message) { }
    public DatabaseHealthException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>
/// Thrown by <c>ReplaceIfEtag</c> when the document's stored <c>_etag</c>
/// doesn't match the value the caller asserted. The HTTP layer turns this
/// into a 412 Precondition Failed (issue #18 — optimistic concurrency).
/// </summary>
public class EtagMismatchException : DocumentForgeException
{
    public string ExpectedEtag { get; }
    public string ActualEtag { get; }
    public EtagMismatchException(string expected, string actual)
        : base($"ETag mismatch: caller asserted '{expected}', current is '{actual}'.")
    {
        ExpectedEtag = expected;
        ActualEtag = actual;
    }
}

/// <summary>
/// Thrown when DocumentForgeDb.Open / Create cannot acquire the on-disk
/// lock for the data file because another process is holding it. The
/// message names the holder (pid + hostname + open time) so operators can
/// kill the right process before retrying.
/// </summary>
/// <summary>
/// Issue #95 — semi-sync replication: an acknowledged write must reach a
/// configured number of followers before the client is told it succeeded.
/// Thrown when that quorum wasn't ACKed within the timeout. The write IS
/// durable on the leader (WAL-committed); this signals that it is not yet
/// safely replicated, so the caller must not treat it as cluster-durable.
/// </summary>
public class ReplicationTimeoutException : DocumentForgeException
{
    public ulong Seq { get; }
    public int Required { get; }
    public int Achieved { get; }
    public ReplicationTimeoutException(ulong seq, int required, int achieved)
        : base($"Semi-sync replication timed out for seq {seq}: needed {required} follower ack(s), got {achieved}. " +
               "The write is durable on the leader but not yet confirmed on a replica.")
    {
        Seq = seq; Required = required; Achieved = achieved;
    }
}

public class DatabaseLockedException : DocumentForgeException
{
    public string FilePath { get; }
    public int HolderPid { get; }
    public string HolderHost { get; }
    public DatabaseLockedException(string filePath, int holderPid, string holderHost, string message)
        : base(message)
    {
        FilePath = filePath;
        HolderPid = holderPid;
        HolderHost = holderHost;
    }
}
