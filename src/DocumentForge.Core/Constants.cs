namespace DocumentForge.Core;

public static class Constants
{
    public const int PageSize = 8192;
    public const int PageHeaderSize = 32;
    public const int UsablePageSpace = PageSize - PageHeaderSize;
    public const int SlotEntrySize = 4; // 2 bytes offset + 2 bytes length
    public const uint InvalidPageId = uint.MaxValue;
    public const string FileExtension = ".dfdb";
    public const string WalExtension = ".dfdb.wal";
    public static readonly byte[] MagicBytes = "DFDB"u8.ToArray();
    public const int FileFormatVersion = 1;
    public const int DefaultCacheSize = 1000; // pages (8MB)
}
