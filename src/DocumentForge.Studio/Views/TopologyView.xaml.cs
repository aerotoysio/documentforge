using System.Windows;
using System.Windows.Controls;
using DocumentForge.Studio.ViewModels;

namespace DocumentForge.Studio.Views;

public partial class TopologyView : UserControl
{
    private bool _loaded;

    public TopologyView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_loaded || DataContext is not TopologyDocumentViewModel vm) return;
        _loaded = true;
        await vm.RefreshAsync();
    }
}
