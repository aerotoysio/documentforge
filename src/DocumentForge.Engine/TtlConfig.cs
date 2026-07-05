namespace DocumentForge.Engine;

/// <summary>
/// Issue #104 — a time-to-live rule: documents in <see cref="Collection"/>
/// expire (and are deleted by the background sweep) when their
/// <see cref="Field"/> holds a timestamp at or before "now". The field may be
/// a DateTime value (ISO-8601 string, stored as a DateTime) or a number
/// interpreted as Unix epoch <b>milliseconds</b>. A document whose field is
/// absent or non-temporal is never expired, so a hold with no expiry set is
/// simply never swept.
/// </summary>
public sealed record TtlConfig(string Collection, string Field, TimeSpan SweepInterval)
{
    /// <summary>Default cadence for the background sweep when a caller doesn't
    /// specify one. Holds are coarse-grained, so a minute keeps overhead low.</summary>
    public static readonly TimeSpan DefaultSweepInterval = TimeSpan.FromSeconds(60);

    /// <summary>Floor on the sweep cadence — a runaway 0s timer would peg the
    /// write lock. Callers asking for less are clamped up to this.</summary>
    public static readonly TimeSpan MinSweepInterval = TimeSpan.FromSeconds(1);
}
