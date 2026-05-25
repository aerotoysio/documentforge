using System.Text;
using System.Text.Json;
using DocumentForge.Core;

namespace DocumentForge.Storage;

/// <summary>
/// On-disk lock that prevents two engine instances from opening the same data
/// file. Without this, deploy races (k8s rolling restart, systemd restart, an
/// accidentally-mounted-twice volume) silently produce two writers on the same
/// pages — page-level CRC32 catches the resulting corruption AFTER it happens
/// but nothing prevents it.
///
/// <para>
/// Implementation: open <c>{datafile}.lock</c> with <c>FileShare.None</c>. If
/// the OS rejects (sharing violation), parse the existing lock file's JSON
/// header to surface the holder's pid + hostname in the error. If the holder
/// is dead — its pid no longer exists locally — auto-reclaim the lock.
/// </para>
///
/// <para>
/// Limitations: on networked filesystems (NFS, SMB), <c>FileShare.None</c>
/// semantics aren't reliably enforced across hosts. Two processes on different
/// machines can still race past this. Use a clustered file system or only
/// mount the data dir on a single host. We document this rather than try to
/// solve it.
/// </para>
/// </summary>
public sealed class DatabaseLock : IDisposable
{
    private readonly FileStream _stream;
    private readonly string _path;
    private bool _disposed;

    public string LockFilePath => _path;

    private DatabaseLock(FileStream stream, string path)
    {
        _stream = stream;
        _path = path;
    }

    /// <summary>
    /// Acquire the lock for <paramref name="dataFilePath"/>. Throws
    /// <see cref="DatabaseLockedException"/> if another live process is
    /// currently holding the file open with <c>FileShare.None</c>.
    ///
    /// <para>
    /// As of #85 (1.2.0) the OS-level file handle IS the source of truth.
    /// The on-disk <c>.lock</c> file's JSON metadata is kept for diagnostic
    /// purposes — it tells you who LAST held the lock — but the hostname
    /// in it is no longer used to gate Acquire. That earlier behaviour
    /// fired false positives on every Docker container redeploy (each
    /// container gets a fresh random hostname even on the same physical
    /// host), leaving OOM-killed databases permanently un-openable until
    /// the operator manually <c>rm</c>'d the lock file. The OS releases
    /// the FileShare.None handle automatically when the holder process
    /// exits (even on SIGKILL), so reclaiming a stale lock is just a
    /// matter of opening it again.
    /// </para>
    ///
    /// <para>
    /// Networked filesystems caveat: NFS and SMB don't propagate
    /// FileShare.None semantics reliably across hosts. If you mount the
    /// same data dir from two machines simultaneously, both can pass
    /// this check and you'll get dual-writer corruption. Use a clustered
    /// filesystem (e.g. CephFS with proper locking) or only mount the
    /// data dir on one host at a time.
    /// </para>
    /// </summary>
    /// <param name="force">Legacy flag — pre-1.2.0 it bypassed the
    /// hostname check. Since that check is gone, <c>force</c> now only
    /// affects what happens if FileShare.None still fails: it triggers
    /// a brief retry to handle a previous container that's still in its
    /// Dispose phase. Setting it cannot override a genuinely-live holder
    /// — that would risk dual writers and the OS won't let us anyway.</param>
    public static DatabaseLock Acquire(string dataFilePath, bool force = false)
    {
        var lockPath = dataFilePath + ".lock";

        // First attempt — the common path. FileShare.None is the truth.
        // If the previous holder died (OOM, SIGKILL, host crash), the OS
        // released its handle and this just works, regardless of what
        // stale text content sits in the file.
        try { return Open(lockPath); }
        catch (IOException firstEx)
        {
            // Someone has the file genuinely open right now. Retry briefly
            // if the caller asked us to (handles "previous container is
            // still in Dispose" — a real edge case during fast restarts).
            if (force)
            {
                for (int i = 0; i < 5; i++)
                {
                    System.Threading.Thread.Sleep(200);
                    try { return Open(lockPath); }
                    catch (IOException) { /* still held — retry */ }
                }
            }

            // Read the diagnostic metadata so the error tells the operator
            // WHO is holding it. The hostname in here may or may not match
            // ours — that's fine, we're no longer using it for gating.
            var holder = TryReadHolder(lockPath);
            var msg = holder is null
                ? $"Database '{dataFilePath}' is locked by another live process (and the lock file's metadata is unreadable: {firstEx.Message})."
                : $"Database '{dataFilePath}' is locked by pid {holder.Value.Pid} on host '{holder.Value.Host}', opened {holder.Value.OpenedAtUtc:o}.";
            throw new DatabaseLockedException(dataFilePath,
                holder?.Pid ?? 0, holder?.Host ?? "unknown", msg);
        }
    }

    private static DatabaseLock Open(string lockPath)
    {
        // FileShare.None is the load-bearing piece — it's what makes a second
        // Open from another process raise IOException. We pre-create the file
        // if it doesn't exist (DeleteOnClose is intentionally NOT used because
        // it's POSIX-flaky and we'd rather leave the file behind on crash so
        // operators can see "yes, this DB has been opened before").
        var stream = new FileStream(
            lockPath,
            FileMode.OpenOrCreate,
            FileAccess.ReadWrite,
            FileShare.None,
            bufferSize: 256,
            options: FileOptions.None);

        // Truncate any stale content from a prior holder and stamp our own.
        stream.SetLength(0);
        var holder = new HolderInfo
        {
            Pid = Environment.ProcessId,
            Host = Environment.MachineName,
            OpenedAtUtc = DateTime.UtcNow,
        };
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(holder));
        stream.Write(bytes, 0, bytes.Length);
        stream.Flush(true);

        return new DatabaseLock(stream, lockPath);
    }

    private static HolderInfo? TryReadHolder(string lockPath)
    {
        try
        {
            // FileShare.ReadWrite — even though the holder has it open
            // FileShare.None for ITS handle, we (a different process) can
            // still open with read-share when reading the metadata. On
            // Windows this works because we're requesting a disjoint share
            // mode; on POSIX where shares aren't enforced the read just
            // succeeds. Either way, peek at the JSON.
            using var fs = new FileStream(lockPath, FileMode.Open,
                FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(fs, Encoding.UTF8);
            var json = reader.ReadToEnd();
            if (string.IsNullOrWhiteSpace(json)) return null;
            return JsonSerializer.Deserialize<HolderInfo>(json);
        }
        catch
        {
            return null;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        // Release the file handle first; the OS only un-shares the file when
        // the handle is closed. Then delete the lock file so the next opener
        // finds a clean slate. If delete fails (another process raced and is
        // already opening it), that's fine — they'll truncate it themselves.
        try { _stream.Dispose(); } catch { }
        try { File.Delete(_path); } catch { }
    }

    private record struct HolderInfo
    {
        public int Pid { get; init; }
        public string Host { get; init; }
        public DateTime OpenedAtUtc { get; init; }
    }
}
