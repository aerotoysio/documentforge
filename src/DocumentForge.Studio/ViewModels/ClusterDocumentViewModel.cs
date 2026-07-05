using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DocumentForge.Studio.Core.Cluster;
using DocumentForge.Studio.Services;

namespace DocumentForge.Studio.ViewModels;

public sealed partial class ClusterShardRow : ObservableObject
{
    public ClusterShardRow(ShardDescriptor shard) => Shard = shard;

    public ShardDescriptor Shard { get; }
    public string Name => Shard.Name;
    public string LeaderEndpoint => Shard.LeaderEndpoint;
    public int FollowerCount => Shard.Followers.Count;

    [ObservableProperty] private string _health = "not checked";
}

public sealed class ClusterCollectionRow
{
    public ClusterCollectionRow(string name, CollectionPolicyDescriptor policy)
    {
        Name = name;
        Strategy = policy.Strategy.ToString();
        ShardKeyPath = policy.Strategy == ShardingStrategy.Hash ? policy.ShardKeyPath ?? "(missing shard key)" : "—";
    }

    public string Name { get; }
    public string Strategy { get; }
    public string ShardKeyPath { get; }
}

/// <summary>Edits a DocumentForge cluster.json (shards + per-collection sharding
/// strategy) and health-checks each shard. Client-only: it manipulates the file
/// and pings endpoints; the engine still owns cluster behaviour.</summary>
public sealed partial class ClusterDocumentViewModel : DocumentViewModel
{
    private readonly ClusterConfig _config;
    private readonly IDialogService _dialogs;

    public ClusterDocumentViewModel(ClusterConfig config, string? filePath, IDialogService dialogs,
        IReadOnlyList<string>? knownEndpoints = null)
    {
        _config = config;
        _dialogs = dialogs;
        FilePath = filePath;
        ContentId = filePath is null ? $"cluster:new:{Guid.NewGuid():N}" : $"cluster:{filePath}";
        UpdateTitle();
        ReloadRows();
        NewCollectionStrategy = ShardingStrategy.Hash;

        // Suggest shard endpoints from the user's saved connections + any endpoints
        // already in this config, so they can pick instead of typing a URL.
        foreach (var e in (knownEndpoints ?? Array.Empty<string>())
                     .Concat(_config.Shards.Select(s => s.LeaderEndpoint))
                     .Where(e => !string.IsNullOrWhiteSpace(e))
                     .Distinct(StringComparer.OrdinalIgnoreCase))
            ShardEndpointSuggestions.Add(e);
    }

    public ObservableCollection<ClusterShardRow> Shards { get; } = new();
    public ObservableCollection<ClusterCollectionRow> Collections { get; } = new();
    public IReadOnlyList<ShardingStrategy> Strategies { get; } = new[] { ShardingStrategy.Hash, ShardingStrategy.Replicated };

    /// <summary>Endpoint URLs offered in the "add shard" box (saved connections + existing shards).</summary>
    public ObservableCollection<string> ShardEndpointSuggestions { get; } = new();

    /// <summary>Collection names discovered on healthy shards during a health check.</summary>
    public ObservableCollection<string> CollectionSuggestions { get; } = new();

    [ObservableProperty] private string? _filePath;
    [ObservableProperty] private string _statusMessage = "";
    [ObservableProperty] private bool _isBusy;

    // Add-shard inputs
    [ObservableProperty] private string _newShardName = "";
    [ObservableProperty] private string _newShardEndpoint = "";

    // Add-collection inputs
    [ObservableProperty] private string _newCollectionName = "";
    [ObservableProperty] private ShardingStrategy _newCollectionStrategy;
    [ObservableProperty] private string _newCollectionShardKey = "";

    /// <summary>Only a hash-sharded collection needs a shard key.</summary>
    public bool NeedsShardKey => NewCollectionStrategy == ShardingStrategy.Hash;

    partial void OnNewCollectionStrategyChanged(ShardingStrategy value) => OnPropertyChanged(nameof(NeedsShardKey));

    public string SummaryText =>
        $"{Shards.Count} shard(s) · {Collections.Count} collection policy(ies) · {_config.VirtualNodesPerShard} vnodes/shard";

    private void ReloadRows()
    {
        Shards.Clear();
        foreach (var s in _config.Shards) Shards.Add(new ClusterShardRow(s));
        Collections.Clear();
        foreach (var (name, policy) in _config.Collections) Collections.Add(new ClusterCollectionRow(name, policy));
        OnPropertyChanged(nameof(SummaryText));
    }

    private void UpdateTitle() =>
        Title = FilePath is null ? "Cluster (unsaved)" : $"Cluster — {Path.GetFileName(FilePath)}";

    [RelayCommand]
    private void AddShard()
    {
        var name = NewShardName.Trim();
        var endpoint = NewShardEndpoint.Trim();
        if (name.Length == 0 || endpoint.Length == 0)
        {
            StatusMessage = "Enter a shard name and endpoint.";
            return;
        }
        if (_config.Shards.Any(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
        {
            StatusMessage = $"A shard named '{name}' already exists.";
            return;
        }
        _config.Shards.Add(new ShardDescriptor { Name = name, Endpoint = endpoint });
        NewShardName = "";
        NewShardEndpoint = "";
        ReloadRows();
        StatusMessage = $"Added shard '{name}'. Save to write cluster.json.";
    }

    [RelayCommand]
    private void RemoveShard(ClusterShardRow? row)
    {
        if (row is null) return;
        _config.Shards.RemoveAll(s => ReferenceEquals(s, row.Shard));
        ReloadRows();
        StatusMessage = $"Removed shard '{row.Name}'.";
    }

    [RelayCommand]
    private void AddCollection()
    {
        var name = NewCollectionName.Trim();
        if (name.Length == 0) { StatusMessage = "Enter a collection name."; return; }
        var key = NewCollectionShardKey.Trim();
        if (NewCollectionStrategy == ShardingStrategy.Hash && key.Length == 0)
        {
            StatusMessage = "A hash-sharded collection needs a shard key path.";
            return;
        }
        _config.Collections[name] = new CollectionPolicyDescriptor
        {
            Strategy = NewCollectionStrategy,
            ShardKeyPath = NewCollectionStrategy == ShardingStrategy.Hash ? key : null,
        };
        NewCollectionName = "";
        NewCollectionShardKey = "";
        ReloadRows();
        StatusMessage = $"Set '{name}' to {NewCollectionStrategy}. Save to write cluster.json.";
    }

    [RelayCommand]
    private void RemoveCollection(ClusterCollectionRow? row)
    {
        if (row is null) return;
        _config.Collections.Remove(row.Name);
        ReloadRows();
        StatusMessage = $"Removed policy for '{row.Name}'.";
    }

    [RelayCommand]
    private void Save()
    {
        var path = FilePath ?? _dialogs.PickSaveFile("Cluster config (*.json)|*.json|All files (*.*)|*.*", "cluster.json");
        if (path is null) return;
        try
        {
            _config.Save(path);
            FilePath = path;
            UpdateTitle();
            StatusMessage = $"Saved to {path}";
        }
        catch (Exception ex)
        {
            _dialogs.ShowError("Save failed", ex.Message);
        }
    }

    [RelayCommand]
    private async Task CheckHealthAsync()
    {
        IsBusy = true;
        var up = 0;
        try
        {
            var discovered = new SortedSet<string>(CollectionSuggestions, StringComparer.OrdinalIgnoreCase);
            foreach (var row in Shards)
            {
                row.Health = "checking…";
                var h = await ClusterHealth.PingAsync(row.LeaderEndpoint, TimeSpan.FromSeconds(5));
                if (h.Reachable && h.Healthy)
                {
                    up++;
                    row.Health = $"🟢 up{(h.Version is null ? "" : $" · v{h.Version}")}";
                    // Best-effort: learn the collection names living on this shard
                    // so the "add collection" box can suggest them.
                    foreach (var c in await ClusterHealth.TryGetCollectionsAsync(row.LeaderEndpoint, TimeSpan.FromSeconds(5)))
                        discovered.Add(c);
                }
                else if (h.Reachable)
                {
                    row.Health = $"🟡 degraded · {h.Status}";
                }
                else
                {
                    row.Health = $"🔴 unreachable · {h.Error}";
                }
            }
            if (discovered.Count != CollectionSuggestions.Count)
            {
                CollectionSuggestions.Clear();
                foreach (var c in discovered) CollectionSuggestions.Add(c);
            }
            StatusMessage = Shards.Count == 0
                ? "No shards to check — add some first."
                : $"{up}/{Shards.Count} shard(s) healthy.";
        }
        finally
        {
            IsBusy = false;
        }
    }
}
