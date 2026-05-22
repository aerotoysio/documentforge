using DocumentForge.Core;

namespace DocumentForge.Engine;

/// <summary>
/// Hosts multiple <see cref="DocumentForgeDb"/> instances inside a single
/// service process — the foundation for Issue #66 (LocalDB-style multi-database
/// hosting). Each registered database remains a fully independent engine
/// (own WAL, recovery log, lock file, page cache, index catalog, shard
/// ring, replication followers); the registry adds nothing more than a
/// thread-safe name→instance dictionary and lifecycle management.
///
/// <para>
/// Phase 1 (this class): attach/create/detach/drop/get/list verbs and a
/// designated "default" database for back-compat with single-DB clients
/// hitting flat <c>/collections/...</c> routes. Lazy open, per-DB quotas,
/// auth scopes, and SQL <c>USE</c> are later phases of the same issue.
/// </para>
///
/// <para>
/// Naming rules: case-insensitive on lookup, alphanumeric / underscore /
/// dash, must start with a letter, 1-64 chars. Operator-supplied casing
/// is preserved for display.
/// </para>
///
/// <para>
/// Thread safety: a single internal lock guards the dictionary. Registry
/// ops (attach, detach) are infrequent compared to data-plane traffic;
/// once <see cref="Get"/> returns a <see cref="DocumentForgeDb"/>, all
/// further work runs on the engine's own synchronisation. No lock
/// contention in the hot path.
/// </para>
/// </summary>
public sealed class DatabaseRegistry : IDisposable
{
    private readonly object _lock = new();
    private readonly Dictionary<string, RegisteredDatabase> _databases
        = new(StringComparer.OrdinalIgnoreCase);
    private string? _defaultName;
    private bool _disposed;

    // Path equality follows the host filesystem: Windows is case-insensitive,
    // Linux/macOS are case-sensitive. Mis-matching this on Windows would let
    // the same .dfdb be attached twice (and both engine instances would race
    // on the lock file).
    private static readonly StringComparer PathComparer =
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    /// <summary>
    /// Name of the database that flat REST routes (<c>/collections/...</c>,
    /// <c>/query</c>, etc.) resolve against. <c>null</c> if no database is
    /// attached yet. Set automatically to the first attached database;
    /// override via <see cref="SetDefault"/>.
    /// </summary>
    public string? DefaultDatabaseName
    {
        get { lock (_lock) return _defaultName; }
    }

    /// <summary>
    /// Snapshot of every attached database (immutable copy — safe to
    /// enumerate without holding the lock).
    /// </summary>
    public IReadOnlyList<DatabaseInfo> List()
    {
        lock (_lock)
        {
            return _databases.Values
                .Select(e => new DatabaseInfo(
                    Name: e.Name,
                    FilePath: e.FilePath,
                    IsDefault: string.Equals(e.Name, _defaultName, StringComparison.OrdinalIgnoreCase)))
                .ToList();
        }
    }

    /// <summary>
    /// Attach an existing <c>.dfdb</c> file under <paramref name="name"/>.
    /// Throws if the name is taken, the canonical file path is already
    /// attached under a different name, or the engine refuses to open the
    /// file (corruption, lock contention, missing file).
    /// </summary>
    public DocumentForgeDb Attach(string name, string filePath, DatabaseOptions? options = null)
    {
        ValidateName(name);
        if (string.IsNullOrWhiteSpace(filePath))
            throw new ArgumentException("File path is required.", nameof(filePath));

        var canonicalPath = Path.GetFullPath(filePath);

        lock (_lock)
        {
            ThrowIfDisposed();
            if (_databases.ContainsKey(name))
                throw new DocumentForgeException(
                    $"Database '{name}' is already attached.");

            foreach (var existing in _databases.Values)
            {
                if (PathComparer.Equals(existing.FilePath, canonicalPath))
                    throw new DocumentForgeException(
                        $"File '{canonicalPath}' is already attached as database '{existing.Name}'.");
            }

            // Open OUTSIDE the lock? — no. Open holds the .lock file for the
            // lifetime of the engine; concurrent Attach calls for *different*
            // paths could be parallelised, but the simplicity of one global
            // lock vs. the cost (Attach is called once per DB at service
            // startup or via admin endpoint) doesn't justify the complexity.
            var db = DocumentForgeDb.Open(canonicalPath, options);
            _databases[name] = new RegisteredDatabase(name, canonicalPath, db);
            _defaultName ??= name;
            return db;
        }
    }

    /// <summary>
    /// Create a new <c>.dfdb</c> file at <paramref name="filePath"/> and
    /// attach it under <paramref name="name"/>. Equivalent to SQL Server's
    /// <c>CREATE DATABASE</c>.
    /// </summary>
    public DocumentForgeDb Create(string name, string filePath, DatabaseOptions? options = null)
    {
        ValidateName(name);
        if (string.IsNullOrWhiteSpace(filePath))
            throw new ArgumentException("File path is required.", nameof(filePath));

        var canonicalPath = Path.GetFullPath(filePath);
        if (File.Exists(canonicalPath))
            throw new DocumentForgeException(
                $"Cannot create database '{name}': file '{canonicalPath}' already exists. " +
                $"Use Attach instead, or choose a different path.");

        // Ensure the directory exists so the engine's Create call doesn't
        // fail with a cryptic "path not found" deep in DataFile.Create.
        var dir = Path.GetDirectoryName(canonicalPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        lock (_lock)
        {
            ThrowIfDisposed();
            if (_databases.ContainsKey(name))
                throw new DocumentForgeException(
                    $"Database '{name}' is already attached.");

            var db = DocumentForgeDb.OpenOrCreate(canonicalPath, options);
            _databases[name] = new RegisteredDatabase(name, canonicalPath, db);
            _defaultName ??= name;
            return db;
        }
    }

    /// <summary>
    /// Attach if the file exists; create + attach otherwise. The natural
    /// verb for service startup ("open the DB at this path; create if
    /// first time") and admin endpoints where the caller doesn't know
    /// whether the operator pre-seeded the file.
    /// </summary>
    public DocumentForgeDb AttachOrCreate(string name, string filePath, DatabaseOptions? options = null)
    {
        if (string.IsNullOrWhiteSpace(filePath))
            throw new ArgumentException("File path is required.", nameof(filePath));
        var canonicalPath = Path.GetFullPath(filePath);
        return File.Exists(canonicalPath)
            ? Attach(name, canonicalPath, options)
            : Create(name, canonicalPath, options);
    }

    /// <summary>
    /// Unregister and dispose the named database. The <c>.dfdb</c> file
    /// stays on disk — re-attach later, or move it to another host.
    /// Returns <c>false</c> if no database with that name was registered.
    /// </summary>
    public bool Detach(string name)
    {
        lock (_lock)
        {
            ThrowIfDisposed();
            if (!_databases.TryGetValue(name, out var entry))
                return false;

            _databases.Remove(entry.Name);
            if (string.Equals(entry.Name, _defaultName, StringComparison.OrdinalIgnoreCase))
                _defaultName = _databases.Keys.FirstOrDefault();

            // Dispose AFTER removing from the dictionary so a concurrent
            // Get with the same name returns "not found" rather than a
            // half-disposed engine.
            entry.Db.Dispose();
            return true;
        }
    }

    /// <summary>
    /// Detach the database and delete its on-disk files (<c>.dfdb</c>, <c>.wal</c>,
    /// <c>.recovery</c>, <c>.lock</c>, <c>.followerseq</c>). Irreversible.
    /// Returns <c>false</c> if no database with that name was registered.
    /// </summary>
    public bool Drop(string name)
    {
        string? filePath;
        lock (_lock)
        {
            ThrowIfDisposed();
            if (!_databases.TryGetValue(name, out var entry))
                return false;

            filePath = entry.FilePath;
            _databases.Remove(entry.Name);
            if (string.Equals(entry.Name, _defaultName, StringComparison.OrdinalIgnoreCase))
                _defaultName = _databases.Keys.FirstOrDefault();
            entry.Db.Dispose();
        }

        // File deletion outside the lock — slow I/O has no business blocking
        // other registry ops, and at this point the engine is already
        // disposed so the files aren't in use.
        DeleteSidecarFiles(filePath);
        return true;
    }

    /// <summary>
    /// Resolve <paramref name="name"/> to its engine instance. Throws
    /// <see cref="DocumentForgeException"/> if not found — the routing
    /// layer should translate to 404.
    /// </summary>
    public DocumentForgeDb Get(string name)
    {
        var db = TryGet(name);
        if (db is null)
            throw new DocumentForgeException($"Database '{name}' is not attached.");
        return db;
    }

    /// <summary>
    /// Non-throwing variant of <see cref="Get"/>; returns <c>null</c> when
    /// the name isn't registered.
    /// </summary>
    public DocumentForgeDb? TryGet(string name)
    {
        lock (_lock)
        {
            ThrowIfDisposed();
            return _databases.TryGetValue(name, out var entry) ? entry.Db : null;
        }
    }

    /// <summary>
    /// Returns the default database. Throws if no database is attached.
    /// Used by flat REST routes (<c>/collections/...</c>) for back-compat
    /// with single-DB clients written before Issue #66.
    /// </summary>
    public DocumentForgeDb GetDefault()
    {
        lock (_lock)
        {
            ThrowIfDisposed();
            if (_defaultName is null)
                throw new DocumentForgeException(
                    "No databases are attached. " +
                    "Attach one via the registry before issuing data-plane requests.");
            return _databases[_defaultName].Db;
        }
    }

    /// <summary>
    /// Designate <paramref name="name"/> as the database that flat routes
    /// resolve against. Throws if the name isn't currently attached.
    /// </summary>
    public void SetDefault(string name)
    {
        lock (_lock)
        {
            ThrowIfDisposed();
            if (!_databases.ContainsKey(name))
                throw new DocumentForgeException(
                    $"Cannot set '{name}' as default: not attached.");
            // Preserve the operator's exact casing from the dictionary's
            // canonical entry (lookup was case-insensitive).
            _defaultName = _databases.Keys.First(k =>
                string.Equals(k, name, StringComparison.OrdinalIgnoreCase));
        }
    }

    /// <summary>
    /// Dispose every attached database. After this call the registry
    /// rejects all further operations.
    /// </summary>
    public void Dispose()
    {
        List<RegisteredDatabase> toDispose;
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
            toDispose = _databases.Values.ToList();
            _databases.Clear();
            _defaultName = null;
        }

        // Dispose outside the lock — a slow disk flush in one DB must not
        // block disposal of the others.
        foreach (var entry in toDispose)
        {
            try { entry.Db.Dispose(); }
            catch (Exception ex)
            {
                Console.Error.WriteLine(
                    $"[DocumentForge] WARNING: error disposing database '{entry.Name}': {ex.Message}");
            }
        }
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(DatabaseRegistry));
    }

    /// <summary>
    /// Names must start with a letter or underscore and contain only
    /// letters, digits, underscores, and dashes. Max 64 chars. Constrained
    /// on purpose — names appear in URLs and SQL, so spaces / quotes /
    /// dots would be nightmares. Underscore-prefix is reserved by
    /// convention for engine-internal databases (Issue #73 — _system);
    /// the registry permits the syntax but the routing layer hides
    /// underscore-prefixed names from the public /databases listing.
    /// </summary>
    private static void ValidateName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Database name is required.", nameof(name));
        if (name.Length > 64)
            throw new ArgumentException(
                $"Database name '{name}' exceeds 64 characters.", nameof(name));
        if (!char.IsLetter(name[0]) && name[0] != '_')
            throw new ArgumentException(
                $"Database name '{name}' must start with a letter or underscore.", nameof(name));
        foreach (var c in name)
        {
            if (!(char.IsLetterOrDigit(c) || c == '_' || c == '-'))
                throw new ArgumentException(
                    $"Database name '{name}' contains invalid character '{c}'. " +
                    $"Allowed: letters, digits, underscore, dash.", nameof(name));
        }
    }

    private static void DeleteSidecarFiles(string dataFilePath)
    {
        foreach (var ext in new[] { "", ".wal", ".recovery", ".lock", ".followerseq" })
        {
            try { File.Delete(dataFilePath + ext); }
            catch (Exception ex)
            {
                Console.Error.WriteLine(
                    $"[DocumentForge] WARNING: failed to delete '{dataFilePath}{ext}': {ex.Message}");
            }
        }
    }

    private sealed record RegisteredDatabase(string Name, string FilePath, DocumentForgeDb Db);
}

/// <summary>
/// Public view of an attached database — exposed via <see cref="DatabaseRegistry.List"/>
/// and the <c>GET /databases</c> endpoint (Phase 2).
/// </summary>
public sealed record DatabaseInfo(string Name, string FilePath, bool IsDefault);
