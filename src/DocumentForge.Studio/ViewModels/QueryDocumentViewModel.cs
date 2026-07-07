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
        TimeoutSeconds = workspace.Settings.DefaultQueryTimeoutSeconds;
        ReloadHistory();
    }

    public string Database { get; }
    public string ConnectionName => _connection.Descriptor.Name;

    /// <summary>Raised when something (e.g. the SQL Reference panel) wants to
    /// drop text into this tab's editor. The view inserts it at the caret.</summary>
    public event Action<string>? InsertTextRequested;

    public void RequestInsert(string text) => InsertTextRequested?.Invoke(text);

    /// <summary>Seed text the view drops into the editor on load.</summary>
    public string InitialSql { get; }

    public ObservableCollection<string> History { get; } = new();

    [ObservableProperty]
    private bool _limitRows;

    [ObservableProperty]
    private int _rowLimit;

    /// <summary>Auto-cancel the query after this many seconds (0 = no timeout).</summary>
    [ObservableProperty]
    private int _timeoutSeconds;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private DataView? _resultView;

    // Backing table + the original JSON per row, kept in lockstep so an edited
    // cell can be written back to the right document by _id/_etag.
    private DataTable? _resultTable;
    private List<string> _documents = new();

    /// <summary>Non-null when the result is a single-collection SELECT whose rows
    /// carry _id + _etag — i.e. the grid can write edits back.</summary>
    [ObservableProperty]
    private string? _editableCollection;

    /// <summary>Bound to DataGrid.IsReadOnly (true = read-only).</summary>
    public bool IsGridReadOnly => EditableCollection is null;

    /// <summary>True when the grid is editable — drives the "editing on" banner.</summary>
    public bool IsGridEditable => EditableCollection is not null;

    partial void OnEditableCollectionChanged(string? value)
    {
        OnPropertyChanged(nameof(IsGridReadOnly));
        OnPropertyChanged(nameof(IsGridEditable));
    }

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
        if (TimeoutSeconds > 0) _cts.CancelAfter(TimeSpan.FromSeconds(TimeoutSeconds));
        var started = Environment.TickCount64;
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
                _documents = result.Documents.ToList();
                _resultTable = ResultTable.Build(result.Documents);
                ResultView = _resultTable.DefaultView;
                JsonText = ResultTable.PrettyJson(result.Documents);
                SelectedResultTab = 0;

                // Editable only when the rows are addressable documents: a
                // single-collection SELECT whose rows carry _id and _etag.
                EditableCollection =
                    SqlText.TrySelectSingleCollection(sql, out var coll)
                    && _resultTable.Columns.Contains("_id")
                    && _resultTable.Columns.Contains("_etag")
                        ? coll
                        : null;
            }
            else
            {
                _documents = new();
                _resultTable = null;
                ResultView = null;
                EditableCollection = null;
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
            var elapsedSec = (Environment.TickCount64 - started) / 1000.0;
            var timedOut = TimeoutSeconds > 0 && elapsedSec >= TimeoutSeconds - 0.5;
            Messages = timedOut
                ? $"Query canceled after the {TimeoutSeconds}s timeout. Increase \"Timeout (s)\" or add a tighter WHERE/LIMIT."
                : "Query canceled.";
            StatusSummary = timedOut ? $"Timed out ({TimeoutSeconds}s)" : "Canceled";
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

    /// <summary>Writes a single edited cell back to its document via an
    /// ETag-guarded update. Called by the view after a grid cell commits.
    /// Reverts the cell and reports the reason if anything goes wrong.</summary>
    public async Task CommitCellEditAsync(DataRow row, string field)
    {
        if (_resultTable is null || EditableCollection is not { } collection) return;

        var rowIndex = _resultTable.Rows.IndexOf(row);
        if (rowIndex < 0 || rowIndex >= _documents.Count) return;

        var originalJson = _documents[rowIndex];
        var id = DocumentEdit.ReadField(originalJson, "_id");
        var etag = DocumentEdit.ReadField(originalJson, "_etag");

        var newCell = row[field] == DBNull.Value ? null : row[field]?.ToString();
        var oldCell = ResultTable.RenderField(originalJson, field);
        if (string.Equals(newCell, oldCell, StringComparison.Ordinal)) return; // no-op

        if (id is null || etag is null)
        {
            RevertCell(row, field, originalJson);
            SetEditMessage("This row can't be edited (no _id/_etag).");
            return;
        }

        string updatedJson;
        try { updatedJson = DocumentEdit.ApplyCellEdit(originalJson, field, newCell); }
        catch (Exception ex) { RevertCell(row, field, originalJson); SetEditMessage(ex.Message); return; }

        try
        {
            var newEtag = await _connection.UpdateDocumentAsync(Database, collection, id, updatedJson, etag);
            _documents[rowIndex] = DocumentEdit.ApplyCellEdit(updatedJson, "_etag", newEtag);
            row["_etag"] = newEtag; // reflect the fresh token so the next edit succeeds
            SetEditMessage($"Updated {collection} document {id} ({field}).");
        }
        catch (EtagConflictException)
        {
            RevertCell(row, field, originalJson);
            SetEditMessage($"Conflict: {collection} document {id} changed since it was read. Re-run the query and try again.");
        }
        catch (Exception ex)
        {
            RevertCell(row, field, originalJson);
            SetEditMessage($"Update failed: {ex.Message}");
        }
    }

    /// <summary>Deletes the document behind a grid row. The view confirms first.</summary>
    public async Task DeleteRowAsync(DataRow row)
    {
        if (_resultTable is null || EditableCollection is not { } collection) return;
        var rowIndex = _resultTable.Rows.IndexOf(row);
        if (rowIndex < 0 || rowIndex >= _documents.Count) return;

        var id = DocumentEdit.ReadField(_documents[rowIndex], "_id");
        if (id is null) { SetEditMessage("This row can't be deleted (no _id)."); return; }

        try
        {
            await _connection.DeleteDocumentAsync(Database, collection, id);
            _resultTable.Rows.Remove(row);
            _documents.RemoveAt(rowIndex);
            SetEditMessage($"Deleted {collection} document {id}.");
        }
        catch (Exception ex)
        {
            SetEditMessage($"Delete failed: {ex.Message}");
        }
    }

    private void RevertCell(DataRow row, string field, string originalJson)
    {
        var original = ResultTable.RenderField(originalJson, field);
        row[field] = (object?)original ?? DBNull.Value;
    }

    private void SetEditMessage(string message)
    {
        Messages = message;
        StatusSummary = message;
    }

    // --- Autocomplete data (issue #114) ---
    // Cached per tab: collection names once, field paths per collection (from
    // a one-document sample). Failures degrade to empty — completion is a
    // convenience, never an error surface.

    private IReadOnlyList<string>? _completionCollections;
    private readonly Dictionary<string, IReadOnlyList<string>> _completionFields = new(StringComparer.OrdinalIgnoreCase);

    public async Task<IReadOnlyList<string>> GetCompletionCollectionsAsync()
    {
        if (_completionCollections is not null) return _completionCollections;
        try { _completionCollections = await _connection.GetCollectionNamesAsync(Database); }
        catch { _completionCollections = Array.Empty<string>(); }
        return _completionCollections;
    }

    public async Task<IReadOnlyList<string>> GetCompletionFieldsAsync(string collection)
    {
        if (_completionFields.TryGetValue(collection, out var cached)) return cached;
        IReadOnlyList<string> fields;
        try
        {
            var sample = await _connection.ExecuteAsync(Database, $"SELECT * FROM {collection} LIMIT 1");
            fields = SqlAutocomplete.FlattenFields(sample.Documents.FirstOrDefault());
        }
        catch { fields = Array.Empty<string>(); }
        _completionFields[collection] = fields;
        return fields;
    }

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
