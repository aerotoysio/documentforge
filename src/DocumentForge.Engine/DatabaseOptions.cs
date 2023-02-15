namespace DocumentForge.Engine;

public sealed class DatabaseOptions
{
    public int CacheSizeInPages { get; set; } = 1000; // 8MB default
    public bool EnableWal { get; set; } = true;
}
