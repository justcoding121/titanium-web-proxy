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
        FilterBox.TextChanged += (_, _) => ApplyFilter();
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
            _items = [];
            PackageGrid.ItemsSource = Array.Empty<AppContainerInfo>();
            StatusText.Text = "Loopback exemptions require Windows 8 or later.";
            return;
        }

        try
        {
            _items = AppContainerLoopback.ListContainers().ToList();
            ApplyFilter();
        }
        catch (Exception ex)
        {
            _items = [];
            PackageGrid.ItemsSource = Array.Empty<AppContainerInfo>();
            StatusText.Text = "Failed to enumerate AppContainers: " + ex.Message;
        }
    }

    private void ApplyFilter()
    {
        var query = FilterBox.Text?.Trim() ?? "";
        IEnumerable<AppContainerInfo> view = _items;
        if (!string.IsNullOrEmpty(query))
        {
            view = _items.Where(i =>
                i.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                i.PackageFamilyName.Contains(query, StringComparison.OrdinalIgnoreCase));
        }

        var filtered = view.ToList();
        PackageGrid.ItemsSource = filtered;

        var exemptCount = _items.Count(i => i.IsExempt);
        if (string.IsNullOrEmpty(query))
            StatusText.Text = $"{_items.Count} AppContainers; {exemptCount} currently exempt.";
        else
            StatusText.Text = $"Showing {filtered.Count} of {_items.Count}; {exemptCount} currently exempt.";
    }

    private void OnExempt(object? sender, RoutedEventArgs e)
    {
        var selected = PackageGrid.SelectedItems
            .OfType<AppContainerInfo>()
            .Select(i => i.AppContainerSid)
            .ToList();
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
