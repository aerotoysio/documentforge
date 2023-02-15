using DocumentForge.Document;

namespace DocumentForge.Index;

/// <summary>
/// A comparable key extracted from a document for index storage.
/// Ordering: Null < Boolean < Numbers < String < DateTime
/// </summary>
public sealed class IndexKey : IComparable<IndexKey>, IEquatable<IndexKey>
{
    public BsonValue Value { get; }

    public IndexKey(BsonValue value) => Value = value;

    public static IndexKey FromBsonValue(BsonValue value) => new(value);
    public static readonly IndexKey Null = new(BsonValue.Null);

    public int CompareTo(IndexKey? other)
    {
        if (other is null) return 1;
        return Value.CompareTo(other.Value);
    }

    public bool Equals(IndexKey? other) => other is not null && CompareTo(other) == 0;
    public override bool Equals(object? obj) => obj is IndexKey other && Equals(other);
    public override int GetHashCode() => Value.GetHashCode();
    public override string ToString() => Value.ToString();

    public static bool operator <(IndexKey left, IndexKey right) => left.CompareTo(right) < 0;
    public static bool operator >(IndexKey left, IndexKey right) => left.CompareTo(right) > 0;
    public static bool operator <=(IndexKey left, IndexKey right) => left.CompareTo(right) <= 0;
    public static bool operator >=(IndexKey left, IndexKey right) => left.CompareTo(right) >= 0;
    public static bool operator ==(IndexKey left, IndexKey right) => left.Equals(right);
    public static bool operator !=(IndexKey left, IndexKey right) => !left.Equals(right);
}
