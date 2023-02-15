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
