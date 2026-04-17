namespace DocumentForge.Core;

public readonly struct DocumentId : IEquatable<DocumentId>, IComparable<DocumentId>
{
    private static long _counter;
    public static readonly DocumentId Empty = new(Guid.Empty);

    public Guid Value { get; }

    public DocumentId(Guid value) => Value = value;

    public static DocumentId NewId()
    {
        // Sequential GUID: timestamp-based prefix for B-tree locality
        var timestamp = BitConverter.GetBytes(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        var counter = BitConverter.GetBytes(Interlocked.Increment(ref _counter));
        var bytes = new byte[16];
        // First 8 bytes: timestamp (big-endian for sort order)
        if (BitConverter.IsLittleEndian)
            Array.Reverse(timestamp);
        Array.Copy(timestamp, 0, bytes, 0, 8);
        // Last 8 bytes: counter
        Array.Copy(counter, 0, bytes, 8, 8);
        return new DocumentId(new Guid(bytes));
    }

    public bool IsEmpty => Value == Guid.Empty;
    public byte[] ToBytes() => Value.ToByteArray();
    public static DocumentId FromBytes(ReadOnlySpan<byte> bytes) => new(new Guid(bytes));

    public bool Equals(DocumentId other) => Value.Equals(other.Value);
    public override bool Equals(object? obj) => obj is DocumentId other && Equals(other);
    public override int GetHashCode() => Value.GetHashCode();
    public int CompareTo(DocumentId other) => Value.CompareTo(other.Value);
    public override string ToString() => Value.ToString("N");

    public static bool operator ==(DocumentId left, DocumentId right) => left.Equals(right);
    public static bool operator !=(DocumentId left, DocumentId right) => !left.Equals(right);
}
