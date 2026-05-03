using System.Diagnostics;
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
    /// <see cref="DatabaseLockedException"/> if another live process holds it
    /// or the prior holder was on a different machine (which we can't probe).
    /// </summary>
    /// <param name="force">Reclaim the lock regardless of whether the prior
    /// holder still appears alive. Use only when you're certain no other
    /// writer is active (e.g. a containerised redeploy where the previous
    /// container has been terminated but its pid isn't visible from the new
    /// one, or a different-host stale lock you've manually verified).</param>
    public static DatabaseLock Acquire(string dataFilePath, bool force = false)
    {
        var lockPath = dataFilePath + ".lock";

        // Two distinct checks. Both have to pass.
        //
        // (1) Stale-file metadata check. If a lock file already exists on disk,
        // its content tells us who PREVIOUSLY held it. FileShare.None will not
        // raise on a stale lock file (the holder is dead, no handle is open),
        // so without this peek a different-host crashed holder would silently
        // get its lock taken — exactly the cross-host accident we're trying
        // to prevent.
        //
        // (2) FileShare.None acquire. Catches the case where the prior holder
        // is still alive on this machine (or, on POSIX, where another process
        // happened to hit the file between (1) and (2)).
        if (!force && File.Exists(lockPath))
        {
            var holder = TryReadHolder(lockPath);
            if (holder is not null)
            {
                bool sameHost = string.Equals(holder.Value.Host, Environment.MachineName,
                    StringComparison.OrdinalIgnoreCase);

                if (!sameHost)
                {
                    // Cross-host stale lock — we can't tell whether the foreign
                    // process is still alive. Refuse rather than risk dual writers.
                    throw new DatabaseLockedException(dataFilePath,
                        holder.Value.Pid, holder.Value.Host,
                        $"Database '{dataFilePath}' has a lock file from pid {holder.Value.Pid} on host '{holder.Value.Host}' " +
                        $"(opened {holder.Value.OpenedAtUtc:o}). Cross-host liveness can't be probed; if that process is " +
                        $"no longer running, delete '{lockPath}' or pass DatabaseOptions.ForceUnlock = true.");
                }

                if (sameHost && IsProcessAlive(holder.Value.Pid))
                {
                    throw new DatabaseLockedException(dataFilePath,
                        holder.Value.Pid, holder.Value.Host,
                        $"Database '{dataFilePath}' is locked by pid {holder.Value.Pid} on this host " +
                        $"(opened {holder.Value.OpenedAtUtc:o}). Either stop that process or pass " +
                        $"DatabaseOptions.ForceUnlock = true if you're sure it's gone.");
                }

                // Same host, dead pid → stale local lock from a crashed run.
                // Delete the file so Open below starts clean. If a race leaves
                // the file held by a third party, the Open will throw and we
                // surface the error fresh.
                try { File.Delete(lockPath); } catch { }
            }
            // If TryReadHolder returned null (corrupt/empty/unparseable), we
            // fall through to the Open attempt — FileShare.None will catch a
            // genuine concurrent holder.
        }

        try
        {
            return Open(lockPath);
        }
        catch (IOException ex)
        {
            // The peek above said nothing was holding it, but Open still failed.
            // That means a concurrent Acquire raced past us and took it. Surface
            // the holder info we have at THIS instant — it'll be the new holder.
            var holder = TryReadHolder(lockPath);
            var msg = holder is null
                ? $"Database '{dataFilePath}' is locked by another process (and the lock file is unreadable: {ex.Message})."
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

    private static bool IsProcessAlive(int pid)
    {
        if (pid <= 0) return false;
        try
        {
            using var p = Process.GetProcessById(pid);
            return !p.HasExited;
        }
        catch (ArgumentException)
        {
            // GetProcessById throws ArgumentException if no such process.
            return false;
        }
        catch
        {
            // Permission error or similar — be conservative and say "alive"
            // so we don't reclaim a lock we can't actually verify is dead.
            return true;
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
