namespace DocumentForge.Engine;

public sealed class DatabaseOptions
{
    public int CacheSizeInPages { get; set; } = 1000; // 8MB default
    public bool EnableWal { get; set; } = true;

    /// <summary>
    /// Reclaim the on-disk lock even if the prior holder may still be alive.
    /// Default false — Open throws <see cref="Core.DatabaseLockedException"/>
    /// if another live process holds the lock. Set true only when you're
    /// certain no other writer is active (e.g. containerised redeploys
    /// where the previous container is gone but its pid isn't visible from
    /// the new one).
    /// </summary>
    public bool ForceUnlock { get; set; } = false;
}
