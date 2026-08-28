using Avalonia.Controls;
using Avalonia.Interactivity;
using Titanium.Inspector.Services;

namespace Titanium.Inspector.Views;

public partial class LoopbackExemptWindow : Window
{
    private List<AppContainerInfo> _items = [];

    public LoopbackExemptWindow()
    {
        InitializeComponent();
        ExemptButton.Click += OnExempt;
        ClearButton.Click += OnClear;
        CloseButton.Click += (_, _) => Close();
        Opened += (_, _) => Reload();
    }

    public static async Task ShowAsync(Window owner)
    {
        var w = new LoopbackExemptWindow();
        await w.ShowDialog(owner);
    }

    private void Reload()
    {
        if (!AppContainerLoopback.IsSupported)
        {
            StatusText.Text = "Loopback exemptions require Windows 8 or later.";
            PackageList.ItemsSource = Array.Empty<AppContainerInfo>();
            return;
        }

        try
        {
            _items = AppContainerLoopback.ListContainers().ToList();
            PackageList.ItemsSource = _items;
            StatusText.Text = $"{_items.Count} AppContainers; {_items.Count(i => i.IsExempt)} currently exempt.";
        }
        catch (Exception ex)
        {
            StatusText.Text = "Failed to enumerate AppContainers: " + ex.Message;
        }
    }

    private void OnExempt(object? sender, RoutedEventArgs e)
    {
        var selected = PackageList.SelectedItems?
            .OfType<AppContainerInfo>()
            .Select(i => i.AppContainerSid)
            .ToList() ?? [];
        if (selected.Count == 0)
        {
            StatusText.Text = "Select one or more packages to exempt.";
            return;
        }

        // Merge with existing exemptions.
        var merged = _items.Where(i => i.IsExempt).Select(i => i.AppContainerSid)
            .Concat(selected)
            .Distinct(StringComparer.OrdinalIgnoreCase);
        var ok = AppContainerLoopback.SetExemptions(merged);
        StatusText.Text = ok ? "Exemptions updated." : "Failed to set exemptions (try elevated).";
        Reload();
    }

    private void OnClear(object? sender, RoutedEventArgs e)
    {
        var ok = AppContainerLoopback.ClearExemptions();
        StatusText.Text = ok ? "All loopback exemptions cleared." : "Failed to clear exemptions.";
        Reload();
    }
}
