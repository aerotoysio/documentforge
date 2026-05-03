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
