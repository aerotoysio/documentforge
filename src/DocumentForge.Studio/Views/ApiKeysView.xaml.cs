using System.Windows;
using System.Windows.Controls;
using DocumentForge.Studio.ViewModels;

namespace DocumentForge.Studio.Views;

public partial class ApiKeysView : UserControl
{
    private bool _loaded;

    public ApiKeysView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_loaded || DataContext is not ApiKeysDocumentViewModel vm) return;
        _loaded = true;
        await vm.RefreshAsync();
    }
}
