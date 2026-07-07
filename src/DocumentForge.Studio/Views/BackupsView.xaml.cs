using System.Windows;
using System.Windows.Controls;
using DocumentForge.Studio.ViewModels;

namespace DocumentForge.Studio.Views;

public partial class BackupsView : UserControl
{
    private bool _loaded;

    public BackupsView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_loaded || DataContext is not BackupsDocumentViewModel vm) return;
        _loaded = true;
        await vm.RefreshAsync();
        await vm.LoadConfigCommand.ExecuteAsync(null);
    }
}
