using CommunityToolkit.Mvvm.ComponentModel;

namespace DocumentForge.Studio.ViewModels;

/// <summary>Base for anything that lives as a tab in the main document area.</summary>
public abstract partial class DocumentViewModel : ObservableObject
{
    [ObservableProperty]
    private string _title = "";

    [ObservableProperty]
    private bool _isSelected;

    [ObservableProperty]
    private bool _isActive;

    public string ContentId { get; protected init; } = Guid.NewGuid().ToString("N");

    public bool CanClose { get; protected init; } = true;

    // Safety net: AvalonDock falls back to ToString() for a document tab's title
    // if the container-style Title binding doesn't resolve, so the tab always
    // reads correctly either way.
    public override string ToString() => Title;
}

/// <summary>The always-present home tab.</summary>
public sealed class WelcomeDocumentViewModel : DocumentViewModel
{
    public WelcomeDocumentViewModel()
    {
        Title = "Welcome";
        CanClose = false;
        ContentId = "welcome";
    }
}
