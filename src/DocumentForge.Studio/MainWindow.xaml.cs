using System.ComponentModel;
using System.Windows;
using AvalonDock;
using DocumentForge.Studio.Core.Settings;
using DocumentForge.Studio.Services;
using DocumentForge.Studio.ViewModels;

namespace DocumentForge.Studio;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;

    public MainWindow(StudioWorkspace workspace)
    {
        InitializeComponent();
        _viewModel = new MainViewModel(workspace, new DialogService(this, workspace));
        DataContext = _viewModel;
        Loaded += async (_, _) => await _viewModel.ReconnectSavedAsync();
    }

    // AvalonDock owns the tab lifecycle; mirror a closed document back into the
    // bound collection so the VM and the dock stay in sync.
    private void OnDocumentClosed(object? sender, DocumentClosedEventArgs e)
        => _viewModel.CloseDocument(e.Document.Content);

    protected override async void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        // Direct-file connections hold the OS-level lock on their .dfdb —
        // release them so a service or another Studio can take over.
        await _viewModel.ShutdownAsync();
    }
}
