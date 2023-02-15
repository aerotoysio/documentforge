using System.Collections;
using System.Text.Json;
using DocumentForge.Core;

namespace DocumentForge.Document;

public sealed class BsonDocument : IEnumerable<KeyValuePair<string, BsonValue>>
{
    private readonly List<KeyValuePair<string, BsonValue>> _fields = new();

    public int Count => _fields.Count;

    public BsonValue this[string key]
    {
        get => _fields.FirstOrDefault(f => f.Key == key).Value ?? BsonValue.Null;
        set
        {
            var index = _fields.FindIndex(f => f.Key == key);
            if (index >= 0)
                _fields[index] = new KeyValuePair<string, BsonValue>(key, value);
            else
                _fields.Add(new KeyValuePair<string, BsonValue>(key, value));
        }
    }

    public bool ContainsKey(string key) => _fields.Any(f => f.Key == key);
    public bool Remove(string key)
    {
        var index = _fields.FindIndex(f => f.Key == key);
        if (index < 0) return false;
        _fields.RemoveAt(index);
        return true;
    }

    public IReadOnlyList<string> Keys => _fields.Select(f => f.Key).ToList();

    public DocumentId GetId()
    {
        var idVal = this["_id"];
        if (idVal.IsNull) return DocumentId.Empty;
        return idVal.Type == BsonType.ObjectId ? idVal.AsObjectId : DocumentId.Empty;
    }

    public void SetId(DocumentId id)
    {
        this["_id"] = BsonValue.FromObjectId(id);
    }

    public void EnsureId()
    {
        if (GetId().IsEmpty)
            SetId(DocumentId.NewId());
    }

    public static BsonDocument FromJson(string json)
    {
        using var jsonDoc = JsonDocument.Parse(json);
        return FromJsonElement(jsonDoc.RootElement);
    }

    internal static BsonDocument FromJsonElement(JsonElement element)
    {
        var doc = new BsonDocument();
        foreach (var prop in element.EnumerateObject())
        {
            doc[prop.Name] = ConvertJsonValue(prop.Value);
        }
        return doc;
    }

    private static BsonValue ConvertJsonValue(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.Null => BsonValue.Null,
            JsonValueKind.True => BsonValue.FromBool(true),
            JsonValueKind.False => BsonValue.FromBool(false),
            JsonValueKind.String when DateTimeOffset.TryParse(element.GetString(), out var dt) &&
                element.GetString()!.Contains('T') => BsonValue.FromDateTime(dt),
            JsonValueKind.String => BsonValue.FromString(element.GetString()!),
            JsonValueKind.Number when element.TryGetInt32(out var i) => BsonValue.FromInt32(i),
            JsonValueKind.Number when element.TryGetInt64(out var l) => BsonValue.FromInt64(l),
            JsonValueKind.Number => BsonValue.FromDouble(element.GetDouble()),
            JsonValueKind.Object => BsonValue.FromDocument(FromJsonElement(element)),
            JsonValueKind.Array => BsonValue.FromArray(BsonArray.FromJsonElement(element)),
            _ => BsonValue.Null
        };
    }

    public string ToJson()
    {
        using var stream = new MemoryStream();
        using var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = false });
        WriteJson(writer);
        writer.Flush();
        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }

    public void WriteJson(Utf8JsonWriter writer)
    {
        writer.WriteStartObject();
        foreach (var field in _fields)
        {
            writer.WritePropertyName(field.Key);
            WriteJsonValue(writer, field.Value);
        }
        writer.WriteEndObject();
    }

    internal static void WriteJsonValue(Utf8JsonWriter writer, BsonValue value)
    {
        switch (value.Type)
        {
            case BsonType.Null: writer.WriteNullValue(); break;
            case BsonType.Boolean: writer.WriteBooleanValue(value.AsBoolean); break;
            case BsonType.Int32: writer.WriteNumberValue(value.AsInt32); break;
            case BsonType.Int64: writer.WriteNumberValue(value.AsInt64); break;
            case BsonType.Double: writer.WriteNumberValue(value.AsDouble); break;
            case BsonType.String: writer.WriteStringValue(value.AsString); break;
            case BsonType.DateTime: writer.WriteStringValue(value.AsDateTime.ToString("O")); break;
            case BsonType.ObjectId: writer.WriteStringValue(value.AsObjectId.ToString()); break;
            case BsonType.Document: value.AsDocument.WriteJson(writer); break;
            case BsonType.Array: value.AsArray.WriteJson(writer); break;
        }
    }

    public IEnumerator<KeyValuePair<string, BsonValue>> GetEnumerator() => _fields.GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
