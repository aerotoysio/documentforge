namespace DocumentForge.Studio.Core.Connections;

public static class ConnectionFactory
{
    /// <summary>Builds a live (not yet opened) connection. The API key is
    /// passed explicitly so callers can use ephemeral keys that were never
    /// saved to the secret store (e.g. the connect dialog's Test button).</summary>
    public static IDfConnection Create(ConnectionDescriptor descriptor, string? apiKey) => descriptor.Kind switch
    {
        ConnectionKind.File => new DirectFileConnection(descriptor),
        ConnectionKind.Http => new HttpConnection(descriptor, apiKey),
        _ => throw new ArgumentOutOfRangeException(nameof(descriptor), $"Unknown connection kind {descriptor.Kind}"),
    };
}
