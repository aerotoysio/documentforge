using DocumentForge.Core;

namespace DocumentForge.Document;

public class BsonValue : IComparable<BsonValue>
{
    public BsonType Type { get; }
    private readonly object? _value;

    private BsonValue(BsonType type, object? value) { Type = type; _value = value; }

    // Factory methods
    public static readonly BsonValue Null = new(BsonType.Null, null);
    public static BsonValue FromBool(bool value) => new(BsonType.Boolean, value);
    public static BsonValue FromInt32(int value) => new(BsonType.Int32, value);
    public static BsonValue FromInt64(long value) => new(BsonType.Int64, value);
    public static BsonValue FromDouble(double value) => new(BsonType.Double, value);
    public static BsonValue FromString(string value) => new(BsonType.String, value);
    public static BsonValue FromDocument(BsonDocument value) => new(BsonType.Document, value);
    public static BsonValue FromArray(BsonArray value) => new(BsonType.Array, value);
    public static BsonValue FromDateTime(DateTimeOffset value) => new(BsonType.DateTime, value);
    public static BsonValue FromObjectId(DocumentId value) => new(BsonType.ObjectId, value);

    // Accessors
    public bool AsBoolean => (bool)_value!;
    public int AsInt32 => (int)_value!;
    public long AsInt64 => _value is int i ? i : (long)_value!;
    public double AsDouble => _value switch { int i => i, long l => l, _ => (double)_value! };
    public string AsString => (string)_value!;
    public BsonDocument AsDocument => (BsonDocument)_value!;
    public BsonArray AsArray => (BsonArray)_value!;
    public DateTimeOffset AsDateTime => (DateTimeOffset)_value!;
    public DocumentId AsObjectId => (DocumentId)_value!;

    public bool IsNull => Type == BsonType.Null;
    public bool IsNumeric => Type is BsonType.Int32 or BsonType.Int64 or BsonType.Double;

    public double ToDouble() => _value switch
    {
        int i => i,
        long l => l,
        double d => d,
        _ => throw new InvalidCastException($"Cannot convert {Type} to double")
    };

    public int CompareTo(BsonValue? other)
    {
        if (other is null) return 1;
        if (Type == BsonType.Null && other.Type == BsonType.Null) return 0;
        if (Type == BsonType.Null) return -1;
        if (other.Type == BsonType.Null) return 1;

        // Compare numerics across types
        if (IsNumeric && other.IsNumeric)
            return ToDouble().CompareTo(other.ToDouble());

        // Same type comparison
        if (Type != other.Type)
            return Type.CompareTo(other.Type);

        return Type switch
        {
            BsonType.Boolean => AsBoolean.CompareTo(other.AsBoolean),
            BsonType.Int32 => AsInt32.CompareTo(other.AsInt32),
            BsonType.Int64 => AsInt64.CompareTo(other.AsInt64),
            BsonType.Double => AsDouble.CompareTo(other.AsDouble),
            BsonType.String => string.Compare(AsString, other.AsString, StringComparison.Ordinal),
            BsonType.DateTime => AsDateTime.CompareTo(other.AsDateTime),
            BsonType.ObjectId => AsObjectId.CompareTo(other.AsObjectId),
            _ => 0
        };
    }

    public override bool Equals(object? obj) => obj is BsonValue other && CompareTo(other) == 0;
    public override int GetHashCode() => _value?.GetHashCode() ?? 0;
    public override string ToString() => _value?.ToString() ?? "null";

    // Implicit conversions for convenience
    public static implicit operator BsonValue(string value) => FromString(value);
    public static implicit operator BsonValue(int value) => FromInt32(value);
    public static implicit operator BsonValue(long value) => FromInt64(value);
    public static implicit operator BsonValue(double value) => FromDouble(value);
    public static implicit operator BsonValue(bool value) => FromBool(value);
}
