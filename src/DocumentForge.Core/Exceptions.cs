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

public class DuplicateKeyException : DocumentForgeException
{
    public string IndexName { get; }
    public DuplicateKeyException(string indexName, string keyValue)
        : base($"Duplicate key '{keyValue}' in index '{indexName}'") => IndexName = indexName;
}

public class CollectionNotFoundException : DocumentForgeException
{
    public string CollectionName { get; }
    public CollectionNotFoundException(string collectionName)
        : base($"Collection '{collectionName}' not found.") => CollectionName = collectionName;
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
