using System.Windows;
using System.Windows.Controls;
using DocumentForge.Studio.ViewModels;

namespace DocumentForge.Studio.Views;

public partial class DashboardView : UserControl
{
    private bool _loaded;

    public DashboardView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_loaded || DataContext is not DashboardDocumentViewModel vm) return;
        _loaded = true;
        await vm.RefreshAsync();
    }
}
