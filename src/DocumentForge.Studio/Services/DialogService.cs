using System.Windows;
using DocumentForge.Studio.Core.Settings;
using DocumentForge.Studio.ViewModels;
using DocumentForge.Studio.Views;
using Microsoft.Win32;

namespace DocumentForge.Studio.Services;

public sealed class DialogService : IDialogService
{
    private readonly Window _owner;
    private readonly StudioWorkspace _workspace;

    public DialogService(Window owner, StudioWorkspace workspace)
    {
        _owner = owner;
        _workspace = workspace;
    }

    public ConnectRequest? ShowConnectDialog()
    {
        var dialog = new ConnectDialog(_workspace) { Owner = _owner };
        return dialog.ShowDialog() == true ? dialog.Result : null;
    }

    public NewDatabaseRequest? ShowNewDatabaseDialog(IReadOnlyList<ServerNodeViewModel> servers, string defaultDataDir)
    {
        var dialog = new NewDatabaseDialog(servers, defaultDataDir) { Owner = _owner };
        return dialog.ShowDialog() == true ? dialog.Result : null;
    }

    public AttachDatabaseRequest? ShowAttachDatabaseDialog(string serverName, bool serverIsLocal)
    {
        var dialog = new AttachDatabaseDialog(serverName, serverIsLocal) { Owner = _owner };
        return dialog.ShowDialog() == true ? dialog.Result : null;
    }

    public NewIndexRequest? ShowNewIndexDialog(string collection)
    {
        var dialog = new NewIndexDialog(collection) { Owner = _owner };
        return dialog.ShowDialog() == true ? dialog.Result : null;
    }

    public RelationshipDialogOutcome ShowRelationshipDialog(RelationshipDialogArgs args)
    {
        var dialog = new RelationshipDialog(args) { Owner = _owner };
        dialog.ShowDialog();
        return dialog.Outcome;
    }

    public string? ShowInsertDocumentDialog(string collection, string template)
    {
        var dialog = new InsertDocumentDialog(collection, template) { Owner = _owner };
        return dialog.ShowDialog() == true ? dialog.Result : null;
    }

    public DropChoice ConfirmDropDatabase(string databaseName)
    {
        var result = MessageBox.Show(
            _owner,
            $"Drop database '{databaseName}'?\n\n" +
            "Yes — DROP: detach it AND delete its files from disk (cannot be undone).\n" +
            "No — DETACH: remove it from the server but keep the files.\n",
            "Drop database",
            MessageBoxButton.YesNoCancel,
            MessageBoxImage.Warning,
            MessageBoxResult.Cancel);
        return result switch
        {
            MessageBoxResult.Yes => DropChoice.Drop,
            MessageBoxResult.No => DropChoice.Detach,
            _ => DropChoice.Cancel,
        };
    }

    public bool Confirm(string title, string message) =>
        MessageBox.Show(_owner, message, title, MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;

    public void ShowError(string title, string message) =>
        MessageBox.Show(_owner, message, title, MessageBoxButton.OK, MessageBoxImage.Error);

    public void ShowInfo(string title, string message) =>
        MessageBox.Show(_owner, message, title, MessageBoxButton.OK, MessageBoxImage.Information);

    public string? PickSaveFile(string filter, string defaultFileName)
    {
        var dialog = new SaveFileDialog { Filter = filter, FileName = defaultFileName };
        return dialog.ShowDialog(_owner) == true ? dialog.FileName : null;
    }

    public string? PickOpenFile(string filter)
    {
        var dialog = new OpenFileDialog { Filter = filter };
        return dialog.ShowDialog(_owner) == true ? dialog.FileName : null;
    }
}
