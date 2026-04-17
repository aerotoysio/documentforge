using DocumentForge.Document;

namespace DocumentForge.Index;

/// <summary>
/// A comparable key for index storage. Supports single-field and composite (multi-field) keys.
/// Ordering: Null &lt; Boolean &lt; Numbers &lt; String &lt; DateTime. Composite keys compare component-wise.
/// </summary>
public sealed class IndexKey : IComparable<IndexKey>, IEquatable<IndexKey>
{
    // For single-field keys, Components has one element. For composite, it has N.
    public IReadOnlyList<BsonValue> Components { get; }

    public BsonValue Value => Components[0]; // convenience for single-field

    public IndexKey(BsonValue value) => Components = new[] { value };
    public IndexKey(IReadOnlyList<BsonValue> components) => Components = components;

    public static IndexKey FromBsonValue(BsonValue value) => new(value);
    public static readonly IndexKey Null = new(BsonValue.Null);

    public int CompareTo(IndexKey? other)
    {
        if (other is null) return 1;
        int count = Math.Min(Components.Count, other.Components.Count);
        for (int i = 0; i < count; i++)
        {
            int cmp = Components[i].CompareTo(other.Components[i]);
            if (cmp != 0) return cmp;
        }
        return Components.Count.CompareTo(other.Components.Count);
    }

    public bool Equals(IndexKey? other) => other is not null && CompareTo(other) == 0;
    public override bool Equals(object? obj) => obj is IndexKey other && Equals(other);
    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (var c in Components) hash.Add(c.GetHashCode());
        return hash.ToHashCode();
    }
    public override string ToString() =>
        Components.Count == 1 ? Components[0].ToString() : "(" + string.Join(",", Components) + ")";

    public static bool operator <(IndexKey left, IndexKey right) => left.CompareTo(right) < 0;
    public static bool operator >(IndexKey left, IndexKey right) => left.CompareTo(right) > 0;
    public static bool operator <=(IndexKey left, IndexKey right) => left.CompareTo(right) <= 0;
    public static bool operator >=(IndexKey left, IndexKey right) => left.CompareTo(right) >= 0;
    public static bool operator ==(IndexKey left, IndexKey right) => left.Equals(right);
    public static bool operator !=(IndexKey left, IndexKey right) => !left.Equals(right);
}
