using Avalonia.Controls;
using LustPad.ViewModels;

namespace LustPad.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        Opened += OnOpened;
        Closed += OnClosed;
    }

    private void OnOpened(object? sender, System.EventArgs e)
    {
        if (DataContext is MainViewModel vm)
            vm.StorageProviderAccessor = () => StorageProvider;
    }

    private void OnClosed(object? sender, System.EventArgs e)
    {
        if (DataContext is MainViewModel vm)
            vm.Dispose();
    }
}
