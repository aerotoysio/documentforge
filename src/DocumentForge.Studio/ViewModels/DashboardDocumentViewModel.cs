using System.Collections.ObjectModel;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DocumentForge.Studio.Core.Connections;

namespace DocumentForge.Studio.ViewModels;

public sealed record DashboardCollectionRow(string Name, long DocumentCount, int IndexCount);

/// <summary>A database dashboard (a document tab): size/pages/cache stats, a
/// health panel with the engine's recommendation state, and a per-collection
/// breakdown. Client-only over existing stats + health endpoints.</summary>
public sealed partial class DashboardDocumentViewModel : DocumentViewModel
{
    private readonly IDfConnection _connection;

    private static readonly Brush OkFill = Freeze(Color.FromRgb(0xE6, 0xF7, 0xEC));
    private static readonly Brush OkStroke = Freeze(Color.FromRgb(0x2E, 0x8B, 0x57));
    private static readonly Brush WarnFill = Freeze(Color.FromRgb(0xFF, 0xF8, 0xE6));
    private static readonly Brush WarnStroke = Freeze(Color.FromRgb(0xC7, 0x9A, 0x00));
    private static readonly Brush ErrFill = Freeze(Color.FromRgb(0xFD, 0xEC, 0xEA));
    private static readonly Brush ErrStroke = Freeze(Color.FromRgb(0xC0, 0x39, 0x2B));

    public DashboardDocumentViewModel(IDfConnection connection, string database)
    {
        _connection = connection;
        Database = database;
        Title = $"Dashboard — {database}";
        ContentId = $"dash:{connection.Descriptor.Id}:{database}";
    }

    public string Database { get; }

    public ObservableCollection<DashboardCollectionRow> Collections { get; } = new();

    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _fileSizeText = "—";
    [ObservableProperty] private string _pagesText = "—";
    [ObservableProperty] private string _cacheText = "—";
    [ObservableProperty] private string _collectionsText = "—";
    [ObservableProperty] private string _documentsText = "—";

    [ObservableProperty] private string _healthLine = "Loading…";
    [ObservableProperty] private string _recommendation = "";
    [ObservableProperty] private string? _recommendationDetail;
    [ObservableProperty] private string _filesBreakdown = "";
    [ObservableProperty] private string? _lockHolder;
    [ObservableProperty] private Brush _healthFill = OkFill;
    [ObservableProperty] private Brush _healthStroke = OkStroke;

    [RelayCommand]
    public async Task RefreshAsync()
    {
        IsBusy = true;
        try
        {
            var stats = await _connection.GetStatsAsync(Database);
            FileSizeText = FormatBytes(stats.FileSize);
            PagesText = $"{stats.PageCount:N0}";
            CacheText = $"{stats.CachedPages:N0} cached · {stats.DirtyPages:N0} dirty";
            CollectionsText = $"{stats.Collections.Count:N0}";
            DocumentsText = $"{stats.Collections.Sum(c => c.DocumentCount):N0}";

            Collections.Clear();
            foreach (var c in stats.Collections.OrderBy(c => c.Name))
                Collections.Add(new DashboardCollectionRow(c.Name, c.DocumentCount, c.IndexCount));

            var health = await _connection.GetDatabaseHealthAsync(Database);
            HealthLine = $"Engine: {health.HealthStatus}    Read-only: {(health.ReadOnly ? "yes" : "no")}";
            LockHolder = health.LockHolder is null ? null : $"Lock held by {health.LockHolder}";
            FilesBreakdown =
                $"Data {FormatBytes(health.DataSizeBytes)}   ·   Recovery log {FormatBytes(health.RecoveryLogBytes)}   ·   WAL {FormatBytes(health.WalBytes)}" +
                (health.SnapshotMarkerPresent ? "   ·   snapshot pending" : "");

            (Recommendation, RecommendationDetail, HealthFill, HealthStroke) = health.Recommendation switch
            {
                "healthy" => ("✓ Healthy", health.RecommendationDetail, OkFill, OkStroke),
                "recovery-pending" => ("⏳ Recovery pending", health.RecommendationDetail, WarnFill, WarnStroke),
                "rebuild-catalog" => ("⚠ Catalog rebuild recommended", health.RecommendationDetail, WarnFill, WarnStroke),
                "engine-degraded" => ("✘ Engine degraded", health.RecommendationDetail, ErrFill, ErrStroke),
                _ => (health.Recommendation, health.RecommendationDetail, WarnFill, WarnStroke),
            };
        }
        catch (Exception ex)
        {
            HealthLine = "Could not load dashboard.";
            Recommendation = "✘ Error";
            RecommendationDetail = ex.Message;
            HealthFill = ErrFill;
            HealthStroke = ErrStroke;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private static string FormatBytes(long bytes) => bytes switch
    {
        >= 1L << 30 => $"{bytes / (double)(1L << 30):F2} GB",
        >= 1L << 20 => $"{bytes / (double)(1L << 20):F2} MB",
        >= 1L << 10 => $"{bytes / (double)(1L << 10):F1} KB",
        _ => $"{bytes:N0} B",
    };

    private static Brush Freeze(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }
}
