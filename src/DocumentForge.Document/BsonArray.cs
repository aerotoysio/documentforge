using System.Text.Json;

namespace DocumentForge.Document;

public sealed class BsonArray
{
    private readonly List<BsonValue> _items = new();

    public int Count => _items.Count;
    public BsonValue this[int index] => _items[index];
    public void Add(BsonValue value) => _items.Add(value);
    public IReadOnlyList<BsonValue> Items => _items;

    public static BsonArray FromJsonElement(JsonElement element)
    {
        var arr = new BsonArray();
        foreach (var item in element.EnumerateArray())
        {
            arr.Add(item.ValueKind switch
            {
                JsonValueKind.Null => BsonValue.Null,
                JsonValueKind.True => BsonValue.FromBool(true),
                JsonValueKind.False => BsonValue.FromBool(false),
                JsonValueKind.String when DateTimeOffset.TryParse(item.GetString(), out var dt) &&
                    item.GetString()!.Contains('T') => BsonValue.FromDateTime(dt),
                JsonValueKind.String => BsonValue.FromString(item.GetString()!),
                JsonValueKind.Number when item.TryGetInt32(out var i) => BsonValue.FromInt32(i),
                JsonValueKind.Number when item.TryGetInt64(out var l) => BsonValue.FromInt64(l),
                JsonValueKind.Number => BsonValue.FromDouble(item.GetDouble()),
                JsonValueKind.Object => BsonValue.FromDocument(BsonDocument.FromJsonElement(item)),
                JsonValueKind.Array => BsonValue.FromArray(FromJsonElement(item)),
                _ => BsonValue.Null
            });
        }
        return arr;
    }

    public void WriteJson(Utf8JsonWriter writer)
    {
        writer.WriteStartArray();
        foreach (var item in _items)
            BsonDocument.WriteJsonValue(writer, item);
        writer.WriteEndArray();
    }
}
