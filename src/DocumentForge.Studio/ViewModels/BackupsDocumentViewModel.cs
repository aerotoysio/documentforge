using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DocumentForge.Studio.Core.Connections;

namespace DocumentForge.Studio.ViewModels;

public sealed record BackupRow(string Id, string Database, string SizeText, string? Created, string? Kind);

public sealed record SegmentRow(long Sequence, string? Archived, string SizeText, long Records);

/// <summary>Backups admin (a document tab): list/take/restore-as-new/delete,
/// plus the deeper wizards from issue #117 — point-in-time restore (preview →
/// execute), per-DB WAL archiving, and the backup settings editor.
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

    // ------------------------------------------------------------------
    // Point-in-time restore (issue #117): base backup + target time →
    // preview (feasibility, segments, bytes, gaps) → restore as a NEW DB.
    // ------------------------------------------------------------------

    [ObservableProperty] private string _pitrTargetTimeText = "";
    [ObservableProperty] private string _pitrRestoreAsName = "";
    [ObservableProperty] private string _pitrPreviewText = "Select a base backup, enter a target time (UTC), then Preview.";
    [ObservableProperty] private bool _pitrCanRestore;

    private (string BackupId, DateTime Target)? _previewedPlan;

    partial void OnSelectedBackupChanged(BackupRow? value)
    {
        // A new base backup invalidates any previewed plan.
        PitrCanRestore = false;
        _previewedPlan = null;
    }

    private bool TryParsePitrInputs(out DateTime targetUtc, out string error)
    {
        error = "";
        if (!DateTime.TryParse(PitrTargetTimeText.Trim(), CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out targetUtc))
        {
            error = "Enter the target time as UTC, e.g. 2026-07-07 14:30:00.";
            return false;
        }
        return true;
    }

    [RelayCommand]
    private async Task PitrPreviewAsync()
    {
        if (SelectedBackup is null) { PitrPreviewText = "Select the base backup to restore from."; return; }
        if (!TryParsePitrInputs(out var target, out var error)) { PitrPreviewText = error; return; }

        IsBusy = true;
        PitrCanRestore = false;
        _previewedPlan = null;
        try
        {
            var plan = await _connection.PreviewPitrRestoreAsync(SelectedBackup.Id, target);
            var lines = new List<string>
            {
                $"Base backup: {plan.BaseBackupId} ({plan.BaseDatabase}, taken {plan.BaseCreatedAtUtc ?? "?"})",
                $"Target time: {target:yyyy-MM-dd HH:mm:ss}Z",
                $"Segments to replay: {plan.SegmentCount} ({FormatBytes(plan.BytesToReplay)})",
            };
            if (plan.SequenceGaps.Count > 0)
            {
                lines.Add($"⚠ {plan.SequenceGaps.Count} sequence gap(s):");
                lines.AddRange(plan.SequenceGaps.Select(g =>
                    $"   after seq {g.AfterSequence:N0} → before {g.BeforeSequence:N0} ({g.MissingCount:N0} missing)"));
            }
            if (plan.FeasibleToRestore)
            {
                lines.Add("");
                lines.Add("✔ Feasible. Enter a new database name and click Restore.");
                _previewedPlan = (SelectedBackup.Id, target);
                PitrCanRestore = true;
            }
            else
            {
                lines.Add("");
                lines.Add($"✘ Not feasible: {plan.RefusalReason ?? "unknown reason"}");
            }
            PitrPreviewText = string.Join(Environment.NewLine, lines);
        }
        catch (Exception ex) { PitrPreviewText = ex.Message; }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private async Task PitrRestoreAsync()
    {
        if (_previewedPlan is not { } plan) { PitrPreviewText = "Preview the plan first."; return; }
        var name = PitrRestoreAsName.Trim();
        if (name.Length == 0) { PitrPreviewText = "Enter a new database name to restore into (never overwrites)."; return; }

        IsBusy = true;
        try
        {
            var result = await _connection.RestorePitrAsync(plan.BackupId, plan.Target, name);
            PitrPreviewText = $"Restored '{result.SourceDatabase}' → '{result.Database}' ({result.FilePath})." +
                              $"{Environment.NewLine}{result.SegmentsApplied} segment(s), {FormatBytes(result.BytesReplayed)} replayed." +
                              $"{Environment.NewLine}Refresh Object Explorer to see the new database.";
            PitrRestoreAsName = "";
            PitrCanRestore = false;
            _previewedPlan = null;
            StatusMessage = $"PITR restore into '{result.Database}' complete.";
        }
        catch (Exception ex) { PitrPreviewText = ex.Message; }
        finally { IsBusy = false; }
    }

    // ------------------------------------------------------------------
    // WAL archiving (issue #117): per-DB enable/disable + status + segments.
    // ------------------------------------------------------------------

    public ObservableCollection<SegmentRow> Segments { get; } = new();

    [ObservableProperty] private string _archiveStatusText = "Pick a database and click Refresh status.";
    [ObservableProperty] private bool _archiveEnabled;

    [RelayCommand]
    private async Task RefreshArchiveAsync()
    {
        if (SelectedDatabase is null) { ArchiveStatusText = "Pick a database first."; return; }
        IsBusy = true;
        try
        {
            var status = await _connection.GetArchiveStatusAsync(SelectedDatabase);
            ArchiveEnabled = status.Enabled;
            ArchiveStatusText =
                $"{status.Database}: archiving {(status.Enabled ? "ON" : "off")} · next seq {status.NextSequence:N0}" +
                $" · {status.SegmentsThisSession:N0} segment(s) this session" +
                (status.LastShippedAtUtc is { } t ? $" · last shipped {t}" : "");

            Segments.Clear();
            foreach (var s in await _connection.GetArchiveSegmentsAsync(SelectedDatabase))
                Segments.Add(new SegmentRow(s.SequenceNumber, s.ArchivedAtUtc, FormatBytes(s.ByteCount), s.RecordCount));
        }
        catch (DfHttpException ex) when (ex.Message.Contains("not configured", StringComparison.OrdinalIgnoreCase))
        {
            ArchiveStatusText = "WAL archiving isn't configured on this server (no archive directory in node.json).";
            Segments.Clear();
        }
        catch (Exception ex) { ArchiveStatusText = ex.Message; }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private async Task ToggleArchiveAsync()
    {
        if (SelectedDatabase is null) { ArchiveStatusText = "Pick a database first."; return; }
        IsBusy = true;
        try
        {
            await _connection.SetArchiveEnabledAsync(SelectedDatabase, !ArchiveEnabled);
            await RefreshArchiveAsync();
        }
        catch (Exception ex) { ArchiveStatusText = ex.Message; IsBusy = false; return; }
        finally { IsBusy = false; }
    }

    // ------------------------------------------------------------------
    // Backup settings (issue #117): directory, retention, schedule cron.
    // ------------------------------------------------------------------

    [ObservableProperty] private string _configBackupDir = "";
    [ObservableProperty] private string _configRetentionText = "";
    [ObservableProperty] private string _configScheduleCron = "";
    [ObservableProperty] private string _configStatusText = "";

    [RelayCommand]
    private async Task LoadConfigAsync()
    {
        IsBusy = true;
        try
        {
            var cfg = await _connection.GetBackupConfigAsync();
            ConfigBackupDir = cfg.BackupDirConfigured ?? "";
            ConfigRetentionText = cfg.RetentionCount.ToString(CultureInfo.InvariantCulture);
            ConfigScheduleCron = cfg.ScheduleCron ?? "";
            ConfigStatusText = $"Effective backup directory: {cfg.BackupDir ?? "(server default)"}" +
                               (cfg.BackupDirConfigured is null ? " — using the server default; set a directory to override." : "");
        }
        catch (DfHttpException ex) when (ex.Message.Contains("not configured", StringComparison.OrdinalIgnoreCase))
        {
            ConfigStatusText = "Backups aren't configured on this server. Set a backup directory and save.";
        }
        catch (Exception ex) { ConfigStatusText = ex.Message; }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private async Task SaveConfigAsync()
    {
        if (!int.TryParse(ConfigRetentionText.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var retention) || retention < 1)
        { ConfigStatusText = "Retention must be a positive integer (backups kept per database)."; return; }

        IsBusy = true;
        try
        {
            var dir = ConfigBackupDir.Trim();
            var cron = ConfigScheduleCron.Trim();
            await _connection.SetBackupConfigAsync(dir.Length == 0 ? null : dir, retention, cron.Length == 0 ? null : cron);
            ConfigStatusText = "Saved.";
            await LoadConfigAsync();
        }
        catch (Exception ex) { ConfigStatusText = ex.Message; IsBusy = false; }
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
