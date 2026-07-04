namespace DocumentForge.Studio.Core.Connections;

/// <summary>Thrown when an ETag-guarded document write loses an optimistic
/// concurrency race — the document changed since it was read. Carries both
/// ETags so the UI can offer reload-and-retry.</summary>
public sealed class EtagConflictException : Exception
{
    public EtagConflictException(string? expectedEtag, string? actualEtag, string message) : base(message)
    {
        ExpectedEtag = expectedEtag;
        ActualEtag = actualEtag;
    }

    public string? ExpectedEtag { get; }
    public string? ActualEtag { get; }
}
