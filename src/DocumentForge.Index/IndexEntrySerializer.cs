using DocumentForge.Core;
using DocumentForge.Document;

namespace DocumentForge.Index;

/// <summary>
/// Binary serialization for index entries: (key, docId, isDeleted).
/// Format: [Flags:1][KeyType:1][KeyLen:2][KeyBytes:N][DocId:16]
/// Flags: 0x01 = Deleted (tombstone)
/// </summary>
public static class IndexEntrySerializer
{
    public const byte FlagDeleted = 0x01;

    public static byte[] Serialize(IndexKey key, DocumentId docId, bool isDeleted = false)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);

        w.Write((byte)(isDeleted ? FlagDeleted : 0));
        w.Write((byte)key.Value.Type);

        var keyBytes = SerializeKeyValue(key.Value);
        w.Write((short)keyBytes.Length);
        w.Write(keyBytes);

        w.Write(docId.ToBytes());

        return ms.ToArray();
    }

    public static (IndexKey Key, DocumentId DocId, bool IsDeleted) Deserialize(ReadOnlySpan<byte> data)
    {
        int offset = 0;
        var flags = data[offset++];
        bool isDeleted = (flags & FlagDeleted) != 0;
        var keyType = (BsonType)data[offset++];
        var keyLen = BitConverter.ToInt16(data[offset..]);
        offset += 2;
        var keyValue = DeserializeKeyValue(keyType, data.Slice(offset, keyLen));
        offset += keyLen;
        var docId = DocumentId.FromBytes(data.Slice(offset, 16));

        return (new IndexKey(keyValue), docId, isDeleted);
    }

    private static byte[] SerializeKeyValue(BsonValue value)
    {
        return value.Type switch
        {
            BsonType.Null => Array.Empty<byte>(),
            BsonType.Boolean => new[] { value.AsBoolean ? (byte)1 : (byte)0 },
            BsonType.Int32 => BitConverter.GetBytes(value.AsInt32),
            BsonType.Int64 => BitConverter.GetBytes(value.AsInt64),
            BsonType.Double => BitConverter.GetBytes(value.AsDouble),
            BsonType.String => System.Text.Encoding.UTF8.GetBytes(value.AsString),
            BsonType.DateTime => BitConverter.GetBytes(value.AsDateTime.ToUnixTimeMilliseconds()),
            BsonType.ObjectId => value.AsObjectId.ToBytes(),
            _ => Array.Empty<byte>()
        };
    }

    private static BsonValue DeserializeKeyValue(BsonType type, ReadOnlySpan<byte> data)
    {
        return type switch
        {
            BsonType.Null => BsonValue.Null,
            BsonType.Boolean => BsonValue.FromBool(data[0] == 1),
            BsonType.Int32 => BsonValue.FromInt32(BitConverter.ToInt32(data)),
            BsonType.Int64 => BsonValue.FromInt64(BitConverter.ToInt64(data)),
            BsonType.Double => BsonValue.FromDouble(BitConverter.ToDouble(data)),
            BsonType.String => BsonValue.FromString(System.Text.Encoding.UTF8.GetString(data)),
            BsonType.DateTime => BsonValue.FromDateTime(DateTimeOffset.FromUnixTimeMilliseconds(BitConverter.ToInt64(data))),
            BsonType.ObjectId => BsonValue.FromObjectId(DocumentId.FromBytes(data)),
            _ => BsonValue.Null
        };
    }
}
