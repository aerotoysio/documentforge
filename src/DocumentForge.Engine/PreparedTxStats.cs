namespace DocumentForge.Engine;

/// <summary>
/// In-process counters for the participant-side 2PC machinery on a single
/// shard. Read via <see cref="DocumentForgeDb.GetPreparedTxStats"/>; the
/// HTTP endpoint <c>GET /tx/stats</c> exposes them so an operator dashboard
/// (or a Prometheus textfile collector) can scrape them.
///
/// <para>
/// Phase E.2 keeps this deliberately simple — long counters incremented
/// via <see cref="System.Threading.Interlocked"/> on the worker thread's
/// transitions. Phase F could swap in an OpenTelemetry meter or histogram
/// if production needs it.
/// </para>
/// </summary>
public sealed class PreparedTxStats
{
    /// <summary>Every PREPARE message the participant has accepted on the wire (PREPARED + ABORTED votes both counted here).</summary>
    public long PrepareTotal;
    /// <summary>PREPARE that returned ABORT — validation conflict, busy slot, or other failure before the lock-hold.</summary>
    public long PrepareAbortedTotal;
    /// <summary>PREPARED tx that was finalized via COMMIT.</summary>
    public long CommittedTotal;
    /// <summary>PREPARED tx that was finalized via ROLLBACK (coordinator-initiated or operator-driven).</summary>
    public long RolledBackTotal;
    /// <summary>PREPARED tx that self-aborted because the PREPARE timeout elapsed (Phase D).</summary>
    public long TimedOutTotal;

    /// <summary>
    /// Currently PREPARED tx on this shard. Phase B's invariant is at-most-
    /// one, so this is 0 or 1; gauges allow operators to alert on stuck
    /// PREPARED states (a value of 1 for &gt;30s is suspicious).
    /// </summary>
    public long InFlightPrepared;

    public PreparedTxStats Snapshot() => new()
    {
        PrepareTotal = Interlocked.Read(ref PrepareTotal),
        PrepareAbortedTotal = Interlocked.Read(ref PrepareAbortedTotal),
        CommittedTotal = Interlocked.Read(ref CommittedTotal),
        RolledBackTotal = Interlocked.Read(ref RolledBackTotal),
        TimedOutTotal = Interlocked.Read(ref TimedOutTotal),
        InFlightPrepared = Interlocked.Read(ref InFlightPrepared),
    };
}
