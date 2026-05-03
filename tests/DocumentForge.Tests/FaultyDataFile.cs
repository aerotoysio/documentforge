using DocumentForge.Core;
using DocumentForge.Storage;

namespace DocumentForge.Tests;

/// <summary>
/// Test-only <see cref="IDataFile"/> decorator that wraps a real
/// <see cref="DataFile"/> and injects failures at chosen points. The point of
/// the harness (issue #28) is to verify that the engine's claimed recovery
/// invariants — recovery log replay, page-checksum corruption detection,
/// fsync discipline — actually survive realistic failure modes.
///
/// <para>
/// The harness is in the test project rather than the main library because the
/// faults it injects (controlled <see cref="IOException"/> on a specific page
/// write, mid-operation crash) have no production use. The seam that lets it
/// reach the engine is <see cref="DocumentForge.Engine.DocumentForgeDb.OpenWithDataFile"/>,
/// which is <c>internal</c> and exposed via <c>InternalsVisibleTo</c>.
/// </para>
///
/// <para>
/// Usage:
/// <code>
/// using var underlying = DataFile.Open(path);
/// var faulty = new FaultyDataFile(underlying)
/// {
///     ThrowOnNthWrite = 5, // fail the 5th WritePage call
/// };
/// using var db = DocumentForgeDb.OpenWithDataFile(path, faulty);
/// // ... drive a workload, expect the failure, then re-open with a real
/// // DataFile and verify recovery left the state consistent.
/// </code>
/// </para>
/// </summary>
internal sealed class FaultyDataFile : IDataFile
{
    private readonly IDataFile _inner;
    private int _writeCount;

    public FaultyDataFile(IDataFile inner) => _inner = inner;

    /// <summary>
    /// Throw a synthetic <see cref="IOException"/> on the Nth call to
    /// <see cref="WritePage"/>. Default 0 = no injection. The throw happens
    /// BEFORE the underlying write, so the data file is in the state it
    /// would be after N-1 successful writes — same as a power loss would
    /// leave behind.
    /// </summary>
    public int ThrowOnNthWrite { get; init; }

    /// <summary>
    /// Throw on any write to a specific page. Useful for "what if the index
    /// catalog page write fails" scenarios. Default Invalid = no injection.
    /// </summary>
    public PageId ThrowAtPageId { get; init; } = PageId.Invalid;

    /// <summary>
    /// Throw on the next call to <see cref="Flush"/>. Simulates an fsync
    /// failure (rare but real on networked filesystems and full disks).
    /// Resets to false after the throw.
    /// </summary>
    public bool ThrowOnNextFlush { get; set; }

    /// <summary>
    /// Throw on the next call to <see cref="SetIndexCatalogPage"/>. The
    /// failure mode for the original #24 fsync gap: header pointer in the
    /// OS write cache when power dies.
    /// </summary>
    public bool ThrowOnNextSetIndexCatalogPage { get; set; }

    /// <summary>Number of successful WritePage calls (for assertions about
    /// "the harness fired exactly when it should").</summary>
    public int SuccessfulWriteCount { get; private set; }

    /// <summary>Last exception synthesised. Tests can assert on the type / msg.</summary>
    public IOException? LastInjectedFault { get; private set; }

    public byte[] ReadPage(PageId pageId) => _inner.ReadPage(pageId);
    public uint PageCount => _inner.PageCount;
    public PageId AllocateNewPage() => _inner.AllocateNewPage();
    public PageId GetIndexCatalogPage() => _inner.GetIndexCatalogPage();

    public void WritePage(PageId pageId, byte[] data)
    {
        _writeCount++;
        if (ThrowOnNthWrite > 0 && _writeCount == ThrowOnNthWrite)
            throw Inject($"injected: write #{_writeCount} (pageId {pageId})");
        if (ThrowAtPageId.IsValid && pageId.Value == ThrowAtPageId.Value)
            throw Inject($"injected: page {pageId} write");
        _inner.WritePage(pageId, data);
        SuccessfulWriteCount++;
    }

    public void Flush()
    {
        if (ThrowOnNextFlush)
        {
            ThrowOnNextFlush = false;
            throw Inject("injected: flush");
        }
        _inner.Flush();
    }

    public void SetIndexCatalogPage(PageId pageId)
    {
        if (ThrowOnNextSetIndexCatalogPage)
        {
            ThrowOnNextSetIndexCatalogPage = false;
            throw Inject($"injected: set index catalog page {pageId}");
        }
        _inner.SetIndexCatalogPage(pageId);
    }

    public void Dispose() => _inner.Dispose();

    private IOException Inject(string detail)
    {
        var ex = new IOException($"[FaultyDataFile] {detail}");
        LastInjectedFault = ex;
        return ex;
    }
}
