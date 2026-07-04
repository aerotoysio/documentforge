using DocumentForge.Studio.Core.Connections;
using DocumentForge.Studio.ViewModels;

namespace DocumentForge.Studio.Services;

public sealed record ConnectRequest(ConnectionDescriptor Descriptor, string? ApiKey, bool Save);

/// <summary>Server is null when the new database should be a local file in
/// the default data directory.</summary>
public sealed record NewDatabaseRequest(string Name, ServerNodeViewModel? Server);

public sealed record NewIndexRequest(string Collection, string Name, IReadOnlyList<string> Paths, bool Unique);

public enum DropChoice { Cancel, Detach, Drop }

/// <summary>Keeps WPF dialog plumbing out of the view models so they stay
/// testable and the views stay dumb.</summary>
public interface IDialogService
{
    ConnectRequest? ShowConnectDialog();
    NewDatabaseRequest? ShowNewDatabaseDialog(IReadOnlyList<ServerNodeViewModel> servers, string defaultDataDir);
    NewIndexRequest? ShowNewIndexDialog(string collection);
    DropChoice ConfirmDropDatabase(string databaseName);
    bool Confirm(string title, string message);
    void ShowError(string title, string message);
    void ShowInfo(string title, string message);
    string? PickSaveFile(string filter, string defaultFileName);
    string? PickOpenFile(string filter);
}
