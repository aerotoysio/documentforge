using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DocumentForge.Studio.Core.Connections;
using DocumentForge.Studio.Core.Models;

namespace DocumentForge.Studio.ViewModels;

/// <summary>A per-database replication panel (a document tab). Shows role /
/// sequence / followers / lag and drives the per-DB actions that the service
/// exposes: start as leader, start as follower, promote.</summary>
public sealed partial class ReplicationDocumentViewModel : DocumentViewModel
{
    private readonly IDfConnection _connection;

    public ReplicationDocumentViewModel(IDfConnection connection, string database)
    {
        _connection = connection;
        Database = database;
        Title = $"Replication — {database}";
        ContentId = $"repl:{connection.Descriptor.Id}:{database}";
        LeaderPort = 5500;
        FollowerHost = "localhost";
        FollowerPort = 5500;
        PromotePort = 5500;
    }

    public string Database { get; }
    public string ConnectionName => _connection.Descriptor.Name;

    public ObservableCollection<ReplicationFollowerInfo> Followers { get; } = new();

    [ObservableProperty] private string _roleText = "—";
    [ObservableProperty] private string _summary = "Loading…";
    [ObservableProperty] private string _statusMessage = "";
    [ObservableProperty] private bool _isBusy;

    [ObservableProperty] private int _leaderPort;
    [ObservableProperty] private string _followerHost = "localhost";
    [ObservableProperty] private int _followerPort;
    [ObservableProperty] private int _promotePort;

    [RelayCommand]
    public async Task RefreshAsync()
    {
        IsBusy = true;
        try
        {
            var s = await _connection.GetReplicationStatusAsync(Database);
            RoleText = s.Role.ToUpperInvariant();

            Followers.Clear();
            foreach (var f in s.Followers) Followers.Add(f);

            var lines = new List<string>
            {
                $"Role: {s.Role}    Read-only: {(s.ReadOnly ? "yes" : "no")}",
            };
            if (s.Role == "leader")
            {
                lines.Add($"Leading on port {s.LeaderPort}, current sequence {s.CurrentSeq:N0}");
                lines.Add($"Followers connected: {s.FollowerCount}");
            }
            else if (s.Role == "follower")
            {
                lines.Add($"Following: {s.FollowerLeaderEndpoint ?? "(unknown)"}");
                lines.Add($"Last applied sequence: {s.FollowerLastAppliedSeq:N0}   ops applied: {s.OpsApplied:N0}");
                if (s.GapsDetected) lines.Add("⚠ Sequence gaps detected — this follower may have missed writes.");
                if (s.AutoFailoverPromoted) lines.Add("This node was auto-promoted after leader silence.");
            }
            else
            {
                lines.Add("Not participating in replication. Start it as a leader or a follower below.");
            }
            Summary = string.Join(Environment.NewLine, lines);
            StatusMessage = "Refreshed.";
        }
        catch (Exception ex)
        {
            Summary = "Could not read replication status.";
            StatusMessage = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private Task StartLeaderAsync() =>
        RunAsync(() => _connection.StartReplicationLeaderAsync(Database, LeaderPort),
            $"Started as leader on port {LeaderPort}.");

    [RelayCommand]
    private Task StartFollowerAsync() =>
        RunAsync(() => _connection.StartReplicationFollowerAsync(Database, FollowerHost, FollowerPort),
            $"Started as follower of {FollowerHost}:{FollowerPort}. (A fresh follower snapshots from the leader, replacing local data.)");

    [RelayCommand]
    private Task PromoteAsync() =>
        RunAsync(() => _connection.PromoteReplicaAsync(Database, PromotePort),
            $"Promoted to leader on port {PromotePort}.");

    private async Task RunAsync(Func<Task> action, string okMessage)
    {
        IsBusy = true;
        try
        {
            await action();
            StatusMessage = okMessage;
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }
}
