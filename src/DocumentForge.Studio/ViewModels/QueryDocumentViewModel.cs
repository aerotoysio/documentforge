using System.Collections.ObjectModel;
using System.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DocumentForge.Studio.Core.Connections;
using DocumentForge.Studio.Core.Query;
using DocumentForge.Studio.Core.Settings;

namespace DocumentForge.Studio.ViewModels;

/// <summary>One SQL editor tab bound to a specific connection + database.</summary>
public sealed partial class QueryDocumentViewModel : DocumentViewModel
{
    private readonly IDfConnection _connection;
    private readonly StudioWorkspace _workspace;
    private CancellationTokenSource? _cts;

    public QueryDocumentViewModel(IDfConnection connection, string database, StudioWorkspace workspace, string? initialSql)
    {
        _connection = connection;
        _workspace = workspace;
        Database = database;
        InitialSql = initialSql ?? "";
        Title = $"Query — {database}";
        LimitRows = true;
        RowLimit = workspace.Settings.DefaultQueryLimit;
        ReloadHistory();
    }

    public string Database { get; }
    public string ConnectionName => _connection.Descriptor.Name;

    /// <summary>Seed text the view drops into the editor on load.</summary>
    public string InitialSql { get; }

    public ObservableCollection<string> History { get; } = new();

    [ObservableProperty]
    private bool _limitRows;

    [ObservableProperty]
    private int _rowLimit;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private DataView? _resultView;

    [ObservableProperty]
    private string _jsonText = "";

    [ObservableProperty]
    private string _messages = "Ready. Press F5 or click Run to execute.";

    [ObservableProperty]
    private string _statusSummary = "";

    /// <summary>0 = Results grid, 1 = JSON, 2 = Messages. The view binds the
    /// results TabControl's SelectedIndex here so errors surface the Messages
    /// tab automatically.</summary>
    [ObservableProperty]
    private int _selectedResultTab;

    public bool CanCancel => IsBusy;

    partial void OnIsBusyChanged(bool value)
    {
        OnPropertyChanged(nameof(CanCancel));
        CancelCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Invoked by the view (Run button / F5) with the editor's current text.</summary>
    public async Task ExecuteAsync(string sql)
    {
        sql = sql.Trim();
        if (sql.Length == 0 || IsBusy) return;

        var effective = LimitRows ? SqlText.EnsureLimit(sql, RowLimit) : sql;
        var limitApplied = !ReferenceEquals(effective, sql) && effective != sql;

        IsBusy = true;
        Messages = "Executing…";
        StatusSummary = "Executing…";
        _cts = new CancellationTokenSource();
        try
        {
            var result = await _connection.ExecuteAsync(Database, effective, _cts.Token);

            if (!result.Success)
            {
                ResultView = null;
                JsonText = "";
                Messages = result.Message ?? "Query failed.";
                StatusSummary = "Failed";
                SelectedResultTab = 2;
                return;
            }

            _workspace.AddQueryHistory(_connection.Descriptor.Id, sql);
            ReloadHistory();

            var verb = SqlText.LeadingKeyword(sql);
            if (result.Documents.Count > 0 || verb == "SELECT")
            {
                ResultView = ResultTable.Build(result.Documents).DefaultView;
                JsonText = ResultTable.PrettyJson(result.Documents);
                SelectedResultTab = 0;
            }
            else
            {
                ResultView = null;
                JsonText = "";
                SelectedResultTab = 2;
            }

            var rows = result.Documents.Count;
            var affected = result.AffectedCount;
            var parts = new List<string>();
            if (verb == "SELECT") parts.Add($"{rows:N0} row(s)");
            else parts.Add($"{affected:N0} affected");
            if (result.Plan is { Length: > 0 }) parts.Add(result.Plan);
            parts.Add($"{result.ExecutionMs:F2} ms");
            if (limitApplied) parts.Add($"auto-LIMIT {RowLimit}");
            StatusSummary = string.Join("  •  ", parts);
            Messages = BuildMessages(verb, rows, affected, result.Plan, result.ExecutionMs, limitApplied, effective);
        }
        catch (OperationCanceledException)
        {
            Messages = "Query canceled.";
            StatusSummary = "Canceled";
            SelectedResultTab = 2;
        }
        catch (Exception ex)
        {
            ResultView = null;
            Messages = ex.Message;
            StatusSummary = "Error";
            SelectedResultTab = 2;
        }
        finally
        {
            _cts?.Dispose();
            _cts = null;
            IsBusy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanCancel))]
    private void Cancel() => _cts?.Cancel();

    private void ReloadHistory()
    {
        History.Clear();
        foreach (var sql in _workspace.GetQueryHistory(_connection.Descriptor.Id))
            History.Add(sql);
    }

    private string BuildMessages(string verb, int rows, long affected, string? plan, double ms, bool limitApplied, string effectiveSql)
    {
        var lines = new List<string>();
        if (verb == "SELECT") lines.Add($"{rows:N0} row(s) returned.");
        else lines.Add($"{affected:N0} row(s) affected.");
        if (plan is { Length: > 0 }) lines.Add($"Plan: {plan}");
        lines.Add($"Server execution time: {ms:F2} ms");
        if (limitApplied)
        {
            lines.Add("");
            lines.Add($"A LIMIT {RowLimit} was added automatically (uncheck \"Limit rows\" to run unbounded).");
            lines.Add($"Executed: {effectiveSql}");
        }
        return string.Join(Environment.NewLine, lines);
    }
}
