using System.Text.Json;
using DocumentForge.Engine;

namespace DocumentForge.Cli;

/// <summary>
/// Issue #82 — persistent registry of attached databases.
///
/// <para>
/// Before this, every <c>POST /databases</c> attached the DB in
/// process memory only — a service restart lost the registry and
/// only <c>default</c> + <c>_system</c> reappeared. This class
/// holds the attach list in <c>_system.databases</c> so on the
/// next boot every previously-attached DB is wired back up.
/// </para>
///
/// <para>
/// Bootstrap (on startup):
/// <list type="number">
///   <item>Read every doc from <c>_system.databases</c> →
///     <see cref="DatabaseRegistry.AttachOrCreate"/> each one.
///     Handles paths OUTSIDE the data-dir (the only way some
///     deployments land DBs on a different volume).</item>
///   <item>Auto-discover <c>*.dfdb</c> files in the data-dir
///     that aren't already attached (skipping <c>data.dfdb</c>
///     and <c>_system.dfdb</c>). Attach them AND insert a
///     catalog record — self-healing for upgrades from 1.0.x
///     where attach state lived in memory only.</item>
/// </list>
/// </para>
///
/// <para>
/// Runtime: every <c>POST /databases</c> / <c>DELETE /databases</c>
/// call into <see cref="RecordAttach"/> / <see cref="RecordDetach"/>
/// to keep the catalog in sync. Detach is now PERSISTENT (records
/// the removal so the next restart honours it), as is Drop.
/// </para>
/// </summary>
public sealed class DatabaseCatalog
{
    private const string SystemDbName = "_system";
    private const string CatalogCollection = "databases";

    private readonly DatabaseRegistry _registry;
    private readonly object _lock = new();

    public DatabaseCatalog(DatabaseRegistry registry)
    {
        _registry = registry;
    }

    /// <summary>Read the catalog + auto-discover from disk. Attach
    /// everything in one pass. Idempotent on the registry side
    /// (AttachOrCreate skips already-attached entries). Returns a
    /// summary so the startup banner can print "N restored, M
    /// discovered, K errored".</summary>
    public BootstrapReport Bootstrap(string dataDir)
    {
        var fromCatalog = 0;
        var fromDiscover = 0;
        var errors = new List<string>();

        // Pull the catalog once so step 1 and the step-2 dedup against
        // tombstones share one read.
        var catalogEntries = ReadCatalogAll();
        // Tombstones — names the operator explicitly detached.
        // Auto-discover honours these even if the .dfdb file is still
        // sitting in data-dir; otherwise Detach would be useless on
        // files in the data-dir (auto-discover would just re-attach).
        var detachedNames = catalogEntries
            .Where(e => e.Detached)
            .Select(e => e.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Step 1 — replay the live catalog entries. These take priority
        // because they may point to paths outside data-dir (the catalog
        // is the only place that knows about non-co-located DB files).
        foreach (var entry in catalogEntries.Where(e => !e.Detached))
        {
            if (IsInternalName(entry.Name)) continue;
            try
            {
                _registry.AttachOrCreate(entry.Name, entry.Path);
                fromCatalog++;
            }
            catch (Exception ex)
            {
                errors.Add($"catalog '{entry.Name}' @ {entry.Path}: {ex.Message}");
            }
        }

        // Step 2 — auto-discover *.dfdb in data-dir for anything not
        // already attached AND not tombstoned. Self-heals 1.0.x → 1.1.x
        // upgrades and covers ad-hoc "drop a file into the data-dir"
        // workflows. Skipping tombstoned names keeps Detach honest.
        if (!Directory.Exists(dataDir)) return new(fromCatalog, fromDiscover, errors);
        foreach (var path in Directory.EnumerateFiles(dataDir, "*.dfdb"))
        {
            var name = Path.GetFileNameWithoutExtension(path);
            if (name == "data" || IsInternalName(name)) continue;
            if (detachedNames.Contains(name)) continue;  // explicit detach respected
            if (_registry.TryGet(name) is not null) continue;
            try
            {
                _registry.AttachOrCreate(name, path);
                RecordAttach(name, path);   // self-heal the catalog
                fromDiscover++;
            }
            catch (Exception ex)
            {
                errors.Add($"discover '{name}' @ {path}: {ex.Message}");
            }
        }

        return new BootstrapReport(fromCatalog, fromDiscover, errors);
    }

    /// <summary>Persist an attach record. Called by the /databases
    /// REST handlers after a successful registry attach so the next
    /// restart finds the DB.</summary>
    public void RecordAttach(string name, string path)
    {
        if (IsInternalName(name) || name == "data") return; // never record the implicit ones
        var systemDb = _registry.TryGet(SystemDbName);
        if (systemDb is null) return;

        lock (_lock)
        {
            // Upsert: drop any existing record with this name, insert fresh.
            // The query engine doesn't have parameterised SQL yet, so we
            // restrict the field set we splice in. Database names are
            // validated upstream to letters/digits/underscore/dash only.
            try { systemDb.Execute($"DELETE FROM {CatalogCollection} WHERE name = '{name}'"); }
            catch { /* collection may not exist yet — the insert will create it */ }

            var json = JsonSerializer.Serialize(new
            {
                name,
                path = Path.GetFullPath(path),
                attachedAt = DateTime.UtcNow.ToString("O"),
            });
            systemDb.Insert(CatalogCollection, json);
            // Catalog mutations are infrequent (per attach/detach, not in
            // the hot data path), so we flush immediately. Without this,
            // a Stop-Process -Force / power loss between an attach and
            // the engine's lazy page flush would lose the catalog record
            // — which is exactly the Issue #82 failure mode we're fixing.
            systemDb.Flush();
        }
    }

    /// <summary>Tombstone the record so the next restart skips both
    /// the catalog replay AND auto-discovery for this name. Used by
    /// Detach (file stays on disk, operator wants it out of the
    /// registry persistently).</summary>
    public void RecordDetach(string name)
    {
        if (IsInternalName(name) || name == "data") return;
        var systemDb = _registry.TryGet(SystemDbName);
        if (systemDb is null) return;

        lock (_lock)
        {
            // Read the existing path so the tombstone keeps it (helpful
            // for diagnostics / "where did this go?" questions).
            string path = "";
            try
            {
                var r = systemDb.Execute($"SELECT * FROM {CatalogCollection} WHERE name = '{name}'");
                if (r.Success && r.Documents.Count > 0 && r.Documents[0].ContainsKey("path"))
                    path = r.Documents[0]["path"].AsString;
            }
            catch { /* fresh collection */ }

            try { systemDb.Execute($"DELETE FROM {CatalogCollection} WHERE name = '{name}'"); }
            catch { /* nothing to remove */ }

            // Write the tombstone — `detached: true` marks the name as
            // "operator-removed; do not re-discover."
            systemDb.Insert(CatalogCollection, JsonSerializer.Serialize(new
            {
                name, path,
                detached = true,
                detachedAt = DateTime.UtcNow.ToString("O"),
            }));
            systemDb.Flush(); // see RecordAttach for the durability rationale.
        }
    }

    /// <summary>Hard-remove an attach record (no tombstone). Called by
    /// Drop because the file is also being deleted — there's no need
    /// to remember "do not re-attach" when the file itself is gone.</summary>
    public void RecordDrop(string name)
    {
        if (IsInternalName(name) || name == "data") return;
        var systemDb = _registry.TryGet(SystemDbName);
        if (systemDb is null) return;

        lock (_lock)
        {
            try
            {
                systemDb.Execute($"DELETE FROM {CatalogCollection} WHERE name = '{name}'");
                systemDb.Flush();
            }
            catch { /* nothing to remove */ }
        }
    }

    /// <summary>Snapshot of LIVE attach records (no tombstones).
    /// Used by the startup banner + Studio.</summary>
    public IReadOnlyList<CatalogEntry> List() => ReadCatalogAll()
        .Where(e => !e.Detached)
        .Select(e => new CatalogEntry(e.Name, e.Path))
        .ToList();

    // ----------------------------------------------------------------

    /// <summary>Reads everything — live records AND tombstones. The
    /// bootstrap loop needs both. Public callers see the filtered view
    /// via <see cref="List"/>.</summary>
    private IReadOnlyList<RawCatalogRow> ReadCatalogAll()
    {
        var systemDb = _registry.TryGet(SystemDbName);
        if (systemDb is null) return Array.Empty<RawCatalogRow>();
        var entries = new List<RawCatalogRow>();
        try
        {
            var result = systemDb.Execute($"SELECT * FROM {CatalogCollection}");
            if (!result.Success) return entries;
            foreach (var doc in result.Documents)
            {
                if (!doc.ContainsKey("name")) continue;
                var name = doc["name"].AsString;
                var path = doc.ContainsKey("path") ? doc["path"].AsString : "";
                var detached = doc.ContainsKey("detached")
                    && doc["detached"].Type == Document.BsonType.Boolean
                    && doc["detached"].AsBoolean;
                entries.Add(new RawCatalogRow(name, path, detached));
            }
        }
        catch { /* collection doesn't exist yet — fresh boot */ }
        return entries;
    }

    private sealed record RawCatalogRow(string Name, string Path, bool Detached);

    private static bool IsInternalName(string name) =>
        !string.IsNullOrEmpty(name) && name.StartsWith("_", StringComparison.Ordinal);
}

public sealed record CatalogEntry(string Name, string Path);
public sealed record BootstrapReport(int FromCatalog, int FromAutoDiscover, IReadOnlyList<string> Errors);
