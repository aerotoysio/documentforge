namespace DocumentForge.Document;

public enum BsonType : byte
{
    Null = 0x00,
    Boolean = 0x01,
    Int32 = 0x02,
    Int64 = 0x03,
    Double = 0x04,
    String = 0x05,
    Binary = 0x06,
    Document = 0x07,
    Array = 0x08,
    DateTime = 0x09,
    ObjectId = 0x0A
}
