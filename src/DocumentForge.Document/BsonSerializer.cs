using DocumentForge.Core;

namespace DocumentForge.Document;

/// <summary>
/// Binary serialization for BsonDocument. Format:
/// [TotalSize:4][FieldCount:2][Field1][Field2]...[0x00 terminator]
/// Field = [Type:1][KeyLen:2][KeyBytes:N][ValueBytes:M]
/// </summary>
public static class BsonSerializer
{
    public static byte[] Serialize(BsonDocument doc)
    {
        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms);

        // Placeholder for total size
        writer.Write((int)0);
        writer.Write((short)doc.Count);

        foreach (var field in doc)
        {
            writer.Write((byte)field.Value.Type);
            var keyBytes = System.Text.Encoding.UTF8.GetBytes(field.Key);
            writer.Write((short)keyBytes.Length);
            writer.Write(keyBytes);
            WriteValue(writer, field.Value);
        }

        writer.Write((byte)0xFF); // terminator

        // Go back and write total size
        var totalSize = (int)ms.Position;
        ms.Position = 0;
        writer.Write(totalSize);

        return ms.ToArray();
    }

    public static BsonDocument Deserialize(ReadOnlySpan<byte> data)
    {
        var doc = new BsonDocument();
        int offset = 0;

        var totalSize = BitConverter.ToInt32(data[offset..]);
        offset += 4;
        var fieldCount = BitConverter.ToInt16(data[offset..]);
        offset += 2;

        for (int i = 0; i < fieldCount; i++)
        {
            var type = (BsonType)data[offset++];
            var keyLen = BitConverter.ToInt16(data[offset..]);
            offset += 2;
            var key = System.Text.Encoding.UTF8.GetString(data.Slice(offset, keyLen));
            offset += keyLen;

            var (value, bytesRead) = ReadValue(data[offset..], type);
            offset += bytesRead;
            doc[key] = value;
        }

        return doc;
    }

    private static void WriteValue(BinaryWriter writer, BsonValue value)
    {
        switch (value.Type)
        {
            case BsonType.Null:
                break;
            case BsonType.Boolean:
                writer.Write(value.AsBoolean ? (byte)1 : (byte)0);
                break;
            case BsonType.Int32:
                writer.Write(value.AsInt32);
                break;
            case BsonType.Int64:
                writer.Write(value.AsInt64);
                break;
            case BsonType.Double:
                writer.Write(value.AsDouble);
                break;
            case BsonType.String:
                var strBytes = System.Text.Encoding.UTF8.GetBytes(value.AsString);
                writer.Write(strBytes.Length);
                writer.Write(strBytes);
                break;
            case BsonType.DateTime:
                writer.Write(value.AsDateTime.ToUnixTimeMilliseconds());
                break;
            case BsonType.ObjectId:
                writer.Write(value.AsObjectId.ToBytes());
                break;
            case BsonType.Document:
                var docBytes = Serialize(value.AsDocument);
                writer.Write(docBytes);
                break;
            case BsonType.Array:
                WriteArray(writer, value.AsArray);
                break;
        }
    }

    private static void WriteArray(BinaryWriter writer, BsonArray array)
    {
        writer.Write(array.Count);
        foreach (var item in array.Items)
        {
            writer.Write((byte)item.Type);
            WriteValue(writer, item);
        }
    }

    private static (BsonValue value, int bytesRead) ReadValue(ReadOnlySpan<byte> data, BsonType type)
    {
        int offset = 0;
        BsonValue value;

        switch (type)
        {
            case BsonType.Null:
                value = BsonValue.Null;
                break;
            case BsonType.Boolean:
                value = BsonValue.FromBool(data[offset++] == 1);
                break;
            case BsonType.Int32:
                value = BsonValue.FromInt32(BitConverter.ToInt32(data[offset..]));
                offset += 4;
                break;
            case BsonType.Int64:
                value = BsonValue.FromInt64(BitConverter.ToInt64(data[offset..]));
                offset += 8;
                break;
            case BsonType.Double:
                value = BsonValue.FromDouble(BitConverter.ToDouble(data[offset..]));
                offset += 8;
                break;
            case BsonType.String:
                var strLen = BitConverter.ToInt32(data[offset..]);
                offset += 4;
                var str = System.Text.Encoding.UTF8.GetString(data.Slice(offset, strLen));
                offset += strLen;
                value = BsonValue.FromString(str);
                break;
            case BsonType.DateTime:
                var ticks = BitConverter.ToInt64(data[offset..]);
                offset += 8;
                value = BsonValue.FromDateTime(DateTimeOffset.FromUnixTimeMilliseconds(ticks));
                break;
            case BsonType.ObjectId:
                value = BsonValue.FromObjectId(DocumentId.FromBytes(data.Slice(offset, 16)));
                offset += 16;
                break;
            case BsonType.Document:
                var docSize = BitConverter.ToInt32(data[offset..]);
                var doc = Deserialize(data.Slice(offset, docSize));
                offset += docSize;
                value = BsonValue.FromDocument(doc);
                break;
            case BsonType.Array:
                var (arr, arrBytes) = ReadArray(data[offset..]);
                offset += arrBytes;
                value = BsonValue.FromArray(arr);
                break;
            default:
                value = BsonValue.Null;
                break;
        }

        return (value, offset);
    }

    private static (BsonArray array, int bytesRead) ReadArray(ReadOnlySpan<byte> data)
    {
        int offset = 0;
        var count = BitConverter.ToInt32(data[offset..]);
        offset += 4;
        var array = new BsonArray();

        for (int i = 0; i < count; i++)
        {
            var itemType = (BsonType)data[offset++];
            var (value, bytesRead) = ReadValue(data[offset..], itemType);
            offset += bytesRead;
            array.Add(value);
        }

        return (array, offset);
    }
}
