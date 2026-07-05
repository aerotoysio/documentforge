using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DocumentForge.Studio.Core.Query;

namespace DocumentForge.Studio.ViewModels;

/// <summary>A searchable DocumentForge SQL cheat-sheet (a document tab). Each
/// entry can be copied or inserted into the active query editor.</summary>
public sealed partial class SqlReferenceDocumentViewModel : DocumentViewModel
{
    private readonly Action<string> _insertIntoQuery;

    public SqlReferenceDocumentViewModel(Action<string> insertIntoQuery)
    {
        _insertIntoQuery = insertIntoQuery;
        Title = "SQL Reference";
        ContentId = "sqlref";
        foreach (var e in SqlReference.Entries) Entries.Add(e);
    }

    public ObservableCollection<SqlReferenceEntry> Entries { get; } = new();

    [ObservableProperty]
    private string _searchText = "";

    partial void OnSearchTextChanged(string value)
    {
        Entries.Clear();
        foreach (var e in SqlReference.Search(value)) Entries.Add(e);
    }

    [RelayCommand]
    private void Copy(SqlReferenceEntry? entry)
    {
        if (entry is null) return;
        try { Clipboard.SetText(entry.Example); }
        catch { /* clipboard can be transiently locked; ignore */ }
    }

    [RelayCommand]
    private void Insert(SqlReferenceEntry? entry)
    {
        if (entry is not null) _insertIntoQuery(entry.Example);
    }
}
