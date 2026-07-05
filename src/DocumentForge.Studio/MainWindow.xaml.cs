using System.ComponentModel;
using System.Windows;
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
    }

    protected override async void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        // Direct-file connections hold the OS-level lock on their .dfdb —
        // release them so a service or another Studio can take over.
        await _viewModel.ShutdownAsync();
    }
}
