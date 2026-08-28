using Avalonia.Controls;
using Titanium.Inspector.ViewModels;

namespace Titanium.Inspector.Views;

public partial class MainWindow : Window
{
    private bool _autoStartStarted;

    public MainWindow()
    {
        InitializeComponent();
        Closing += OnClosing;
        Opened += OnOpened;
    }

    private async void OnOpened(object? sender, EventArgs e)
    {
        if (_autoStartStarted || DataContext is not MainWindowViewModel vm)
        {
            return;
        }

        _autoStartStarted = true;
        try
        {
            await vm.TryAutoStartAsync();
        }
        catch
        {
            // never crash UI on auto-start failure
        }
    }

    private void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (DataContext is MainWindowViewModel vm)
        {
            vm.EnsureShutdown();
        }
    }
}
