namespace DocumentForge.Core;

public readonly struct CollectionName : IEquatable<CollectionName>
{
    public string Value { get; }

    public CollectionName(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("Collection name cannot be empty.", nameof(value));
        if (value.Length > 128)
            throw new ArgumentException("Collection name cannot exceed 128 characters.", nameof(value));
        Value = value.ToLowerInvariant();
    }

    public bool Equals(CollectionName other) => Value == other.Value;
    public override bool Equals(object? obj) => obj is CollectionName other && Equals(other);
    public override int GetHashCode() => Value.GetHashCode();
    public override string ToString() => Value;

    public static bool operator ==(CollectionName left, CollectionName right) => left.Equals(right);
    public static bool operator !=(CollectionName left, CollectionName right) => !left.Equals(right);
    public static implicit operator string(CollectionName name) => name.Value;
    public static explicit operator CollectionName(string name) => new(name);
}
