using DocumentForge.Studio.Core.Connections;
using DocumentForge.Studio.ViewModels;

namespace DocumentForge.Studio.Services;

public sealed record ConnectRequest(ConnectionDescriptor Descriptor, string? ApiKey, bool Save);

/// <summary>Server is null when the new database should be a local file in
/// the default data directory.</summary>
public sealed record NewDatabaseRequest(string Name, ServerNodeViewModel? Server);

/// <summary>Attach an existing .dfdb (with its sidecars) to a server.
/// FilePath is as the SERVER resolves it.</summary>
public sealed record AttachDatabaseRequest(string Name, string FilePath);

public sealed record NewIndexRequest(string Collection, string Name, IReadOnlyList<string> Paths, bool Unique);

public enum DropChoice { Cancel, Detach, Drop }

/// <summary>One relationship (schema ref, #151/#152) as the diagram's dialog
/// edits it. OnDelete uses the wire spelling: restrict | setNull | cascade.</summary>
public sealed record RelationshipRequest(
    string ChildCollection, string ChildField, string ParentCollection, string TargetField, string OnDelete);

/// <summary>Existing is null when adding; non-null pre-fills the dialog and
/// enables its Remove button.</summary>
public sealed record RelationshipDialogArgs(IReadOnlyList<string> Collections, RelationshipRequest? Existing);

public enum RelationshipDialogChoice { Cancel, Save, Remove }

public sealed record RelationshipDialogOutcome(RelationshipDialogChoice Choice, RelationshipRequest? Request);

/// <summary>Keeps WPF dialog plumbing out of the view models so they stay
/// testable and the views stay dumb.</summary>
public interface IDialogService
{
    ConnectRequest? ShowConnectDialog();
    NewDatabaseRequest? ShowNewDatabaseDialog(IReadOnlyList<ServerNodeViewModel> servers, string defaultDataDir);
    /// <summary>Pick an existing .dfdb file + name to attach it to a server as.</summary>
    AttachDatabaseRequest? ShowAttachDatabaseDialog(string serverName, bool serverIsLocal);
    NewIndexRequest? ShowNewIndexDialog(string collection);
    /// <summary>Add/edit a diagram relationship (#152).</summary>
    RelationshipDialogOutcome ShowRelationshipDialog(RelationshipDialogArgs args);
    /// <summary>Returns the validated JSON to insert, or null if cancelled.</summary>
    string? ShowInsertDocumentDialog(string collection, string template);
    DropChoice ConfirmDropDatabase(string databaseName);
    bool Confirm(string title, string message);
    void ShowError(string title, string message);
    void ShowInfo(string title, string message);
    string? PickSaveFile(string filter, string defaultFileName);
    string? PickOpenFile(string filter);
}
