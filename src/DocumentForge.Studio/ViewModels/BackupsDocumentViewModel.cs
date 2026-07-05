using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DocumentForge.Studio.Core.Connections;

namespace DocumentForge.Studio.ViewModels;

public sealed record BackupRow(string Id, string Database, string SizeText, string? Created, string? Kind);

/// <summary>Backups admin (a document tab): list, take, restore-as-new, delete.
/// Server connections only.</summary>
public sealed partial class BackupsDocumentViewModel : DocumentViewModel
{
    private readonly IDfConnection _connection;

    public BackupsDocumentViewModel(IDfConnection connection)
    {
        _connection = connection;
        Title = $"Backups — {connection.Descriptor.Name}";
        ContentId = $"backups:{connection.Descriptor.Id}";
    }

    public ObservableCollection<BackupRow> Backups { get; } = new();
    public ObservableCollection<string> Databases { get; } = new();

    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _statusMessage = "";
    [ObservableProperty] private string? _selectedDatabase;
    [ObservableProperty] private BackupRow? _selectedBackup;
    [ObservableProperty] private string _restoreAsName = "";

    [RelayCommand]
    public async Task RefreshAsync()
    {
        IsBusy = true;
        try
        {
            Databases.Clear();
            foreach (var db in await _connection.GetDatabasesAsync()) Databases.Add(db.Name);
            SelectedDatabase ??= Databases.FirstOrDefault();

            var backups = await _connection.GetBackupsAsync();
            Backups.Clear();
            foreach (var b in backups.OrderByDescending(b => b.CreatedAtUtc))
                Backups.Add(new BackupRow(b.Id, b.Database, FormatBytes(b.SizeBytes), b.CreatedAtUtc, b.Kind));
            StatusMessage = $"{Backups.Count} backup(s).";
        }
        catch (DfHttpException ex) when (ex.Message.Contains("Backup manager not configured", StringComparison.OrdinalIgnoreCase))
        {
            Backups.Clear();
            StatusMessage = "Backups aren't configured on this server. Set a backup directory in node.json (or PUT /admin/backup/config) and restart, then refresh.";
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

    [RelayCommand]
    private async Task TakeBackupAsync()
    {
        if (SelectedDatabase is null) { StatusMessage = "Pick a database to back up."; return; }
        IsBusy = true;
        try
        {
            var record = await _connection.TakeBackupAsync(SelectedDatabase);
            StatusMessage = $"Backed up '{record.Database}' → {record.Id} ({FormatBytes(record.SizeBytes)}).";
            await RefreshAsync();
        }
        catch (Exception ex) { StatusMessage = ex.Message; }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private async Task RestoreAsync()
    {
        if (SelectedBackup is null) { StatusMessage = "Select a backup to restore."; return; }
        var name = RestoreAsName.Trim();
        if (name.Length == 0) { StatusMessage = "Enter a new database name to restore into (won't overwrite an existing DB)."; return; }
        IsBusy = true;
        try
        {
            var path = await _connection.RestoreBackupAsync(SelectedBackup.Id, name);
            RestoreAsName = "";
            StatusMessage = $"Restored backup {SelectedBackup.Id} as '{name}' ({path}). Refresh Object Explorer to see it.";
            await RefreshAsync();
        }
        catch (Exception ex) { StatusMessage = ex.Message; }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private async Task DeleteAsync(BackupRow? row)
    {
        if (row is null) return;
        IsBusy = true;
        try
        {
            await _connection.DeleteBackupAsync(row.Id);
            StatusMessage = $"Deleted backup {row.Id}.";
            await RefreshAsync();
        }
        catch (Exception ex) { StatusMessage = ex.Message; }
        finally { IsBusy = false; }
    }

    private static string FormatBytes(long bytes) => bytes switch
    {
        >= 1L << 30 => $"{bytes / (double)(1L << 30):F2} GB",
        >= 1L << 20 => $"{bytes / (double)(1L << 20):F2} MB",
        >= 1L << 10 => $"{bytes / (double)(1L << 10):F1} KB",
        _ => $"{bytes:N0} B",
    };
}
