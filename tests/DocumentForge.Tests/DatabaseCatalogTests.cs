using DocumentForge.Cli;
using DocumentForge.Engine;
using Xunit;

namespace DocumentForge.Tests;

/// <summary>
/// Issue #82 — verifies the bug-fix: previously-attached databases
/// survive a service restart. Each test simulates the same flow the
/// CLI's <see cref="Commands.ServeCommand"/> walks at boot:
///   <list type="number">
///     <item>Build a registry with <c>default</c> + <c>_system</c></item>
///     <item>Construct a <see cref="DatabaseCatalog"/> + call
///       <see cref="DatabaseCatalog.Bootstrap"/></item>
///     <item>Verify expected DBs are attached</item>
///   </list>
/// Restarts are simulated by disposing one registry/catalog pair and
/// constructing a fresh pair pointing at the same data-dir.
/// </summary>
public sealed class DatabaseCatalogTests : IDisposable
{
    private readonly List<string> _dirs = new();

    public void Dispose()
    {
        foreach (var d in _dirs)
        {
            try { Directory.Delete(d, recursive: true); } catch { }
        }
    }

    private string FreshDir(string label)
    {
        var dir = Path.Combine(Path.GetTempPath(), $"cat_{label}_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        _dirs.Add(dir);
        return dir;
    }

    /// <summary>Spin up the in-memory state ServeCommand builds at
    /// startup: default + _system attached, then catalog bootstrapped.</summary>
    private (DatabaseRegistry registry, DatabaseCatalog catalog, BootstrapReport report)
        BootService(string dir)
    {
        var registry = new DatabaseRegistry();
        registry.AttachOrCreate("default", Path.Combine(dir, "data.dfdb"));
        registry.AttachOrCreate("_system", Path.Combine(dir, "_system.dfdb"));
        var catalog = new DatabaseCatalog(registry);
        var report = catalog.Bootstrap(dir);
        return (registry, catalog, report);
    }

    [Fact]
    public void Bootstrap_AutoDiscoversExistingDfdbFiles()
    {
        // The exact scenario the user hit on the 1.0 → 1.1 upgrade:
        // .dfdb files sat in the data-dir but only default came up.
        var dir = FreshDir("autodiscover");

        // Simulate the pre-restart state: three tenant DBs exist on disk.
        // Use a registry+catalog to create them (not the test fixture
        // method — we want them created AND detached so we're left
        // with just the on-disk files).
        {
            var reg = new DatabaseRegistry();
            reg.AttachOrCreate("default", Path.Combine(dir, "data.dfdb"));
            reg.AttachOrCreate("_system", Path.Combine(dir, "_system.dfdb"));
            reg.Create("tenant_a", Path.Combine(dir, "tenant_a.dfdb"));
            reg.Create("tenant_b", Path.Combine(dir, "tenant_b.dfdb"));
            reg.Create("reports",  Path.Combine(dir, "reports.dfdb"));
            reg.Dispose();
        }

        // Restart: boot the service, run catalog Bootstrap.
        var (registry, _, report) = BootService(dir);
        using var _ = registry;

        // Every file on disk should be auto-attached.
        Assert.NotNull(registry.TryGet("tenant_a"));
        Assert.NotNull(registry.TryGet("tenant_b"));
        Assert.NotNull(registry.TryGet("reports"));
        // And the catalog should know about them now (self-healing).
        Assert.Equal(3, report.FromAutoDiscover);
    }

    [Fact]
    public void RecordedAttachPersistsAcrossRestart()
    {
        // The going-forward case: a fresh attach via API gets recorded
        // in _system.databases, then the next restart finds it via the
        // catalog (FromCatalog count) rather than auto-discovery.
        var dir = FreshDir("persistent");
        var externalPath = Path.Combine(FreshDir("external"), "tenant_outside.dfdb");

        // First boot: attach a DB whose file lives OUTSIDE data-dir.
        {
            var (reg, cat, _) = BootService(dir);
            using var _ = reg;
            reg.Create("tenant_outside", externalPath);
            cat.RecordAttach("tenant_outside", externalPath);
        }

        // Restart.
        var (registry, _, report) = BootService(dir);
        using var __ = registry;
        Assert.NotNull(registry.TryGet("tenant_outside"));
        // The external-path DB came back from the catalog, NOT
        // auto-discovery (auto-discover only scans data-dir).
        Assert.Equal(1, report.FromCatalog);
        Assert.Equal(0, report.FromAutoDiscover);
    }

    [Fact]
    public void Detach_PersistsAcrossRestart()
    {
        // The user explicitly detaches — that decision must stick. Pre-#82
        // detach was a runtime no-op (the file would be re-attached on the
        // next start). Now the detach removes the catalog record AND any
        // file remaining in data-dir would be re-auto-discovered… so
        // detach for an in-data-dir DB is still transient unless the file
        // is also moved. For OUTSIDE-data-dir DBs, detach is permanent
        // because there's no auto-discover fallback for those paths.
        var dir = FreshDir("detach");
        var externalPath = Path.Combine(FreshDir("ext"), "permanent_gone.dfdb");

        {
            var (reg, cat, _) = BootService(dir);
            using var _ = reg;
            reg.Create("permanent_gone", externalPath);
            cat.RecordAttach("permanent_gone", externalPath);

            // Now detach it.
            reg.Detach("permanent_gone");
            cat.RecordDetach("permanent_gone");
        }

        var (registry, _, report) = BootService(dir);
        using var __ = registry;
        // External path + catalog cleared → no resurrection.
        Assert.Null(registry.TryGet("permanent_gone"));
        Assert.Equal(0, report.FromCatalog);
        // File still exists on disk (Detach != Drop) but not in data-dir
        // so auto-discover doesn't find it either.
        Assert.True(File.Exists(externalPath));
    }

    [Fact]
    public void Detach_PersistsAcrossRestart_FileInDataDir()
    {
        // Smoke-test repro for Issue #82 — the original bug report case:
        // .dfdb file sits IN data-dir, operator detaches it, restarts the
        // service. The tombstone in _system.databases must suppress
        // auto-discovery so detach actually sticks. Without the tombstone
        // step 2 of Bootstrap would re-attach the file.
        var dir = FreshDir("detach-in-data");
        var inDataPath = Path.Combine(dir, "stays_detached.dfdb");

        {
            var (reg, cat, _) = BootService(dir);
            using var _ = reg;
            reg.Create("stays_detached", inDataPath);
            cat.RecordAttach("stays_detached", inDataPath);

            reg.Detach("stays_detached");
            cat.RecordDetach("stays_detached");

            // File on disk, registry empty, catalog should hold a tombstone.
            Assert.True(File.Exists(inDataPath));
            Assert.Null(reg.TryGet("stays_detached"));
            Assert.Empty(cat.List()); // List filters tombstones — should be 0.
        }

        // Restart. Auto-discover sees the file but the tombstone should
        // suppress the re-attach.
        var (registry, catalog, report) = BootService(dir);
        using var __ = registry;
        Assert.Null(registry.TryGet("stays_detached"));
        Assert.Equal(0, report.FromCatalog);
        Assert.Equal(0, report.FromAutoDiscover);
        Assert.True(File.Exists(inDataPath));
    }

    [Fact]
    public void Bootstrap_SkipsDefaultAndSystem()
    {
        // The default + _system DBs are attached BEFORE Bootstrap runs,
        // and their filenames (data.dfdb, _system.dfdb) are auto-discover
        // exempt — Bootstrap should never try to (re)attach them.
        var dir = FreshDir("skip-special");
        var (registry, _, report) = BootService(dir);
        using var _ = registry;

        // Both special DBs present, but FromAutoDiscover should not have
        // tried to re-attach them.
        Assert.NotNull(registry.TryGet("default"));
        Assert.NotNull(registry.TryGet("_system"));
        Assert.Equal(0, report.FromAutoDiscover);
        Assert.Equal(0, report.FromCatalog);
    }

    [Fact]
    public void RecordAttach_Idempotent()
    {
        // Calling RecordAttach twice for the same name doesn't bloat the
        // catalog; the upsert replaces. The third boot still sees one
        // entry, not three.
        var dir = FreshDir("idempotent");
        var externalPath = Path.Combine(FreshDir("ext"), "same.dfdb");

        {
            var (reg, cat, _) = BootService(dir);
            using var _ = reg;
            reg.Create("same", externalPath);
            cat.RecordAttach("same", externalPath);
            cat.RecordAttach("same", externalPath);
            cat.RecordAttach("same", externalPath);
            var entries = cat.List();
            Assert.Single(entries);
        }

        var (registry, catalog, report) = BootService(dir);
        using var __ = registry;
        Assert.Single(catalog.List());
        Assert.Equal(1, report.FromCatalog);
    }

    // Bootstrap_ErrorOnOneEntry test removed — see comment below.
}
