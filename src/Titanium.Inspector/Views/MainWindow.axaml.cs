using Avalonia.Controls;
using Titanium.Inspector.ViewModels;

namespace Titanium.Inspector.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        Closing += OnClosing;
    }

    private void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (DataContext is MainWindowViewModel vm)
        {
            vm.EnsureShutdown();
        }
    }
}
