using Avalonia.Controls;
using Avalonia.Interactivity;
using Titanium.Inspector.Services;

namespace Titanium.Inspector.Views;

public partial class LoopbackExemptWindow : Window
{
    private List<AppContainerInfo> _items = [];
    private string? _pendingStatus;
    private bool _suppressExemptCheckChanged;
    private List<AppContainerInfo> _visibleItems = [];

    public LoopbackExemptWindow()
    {
        InitializeComponent();
        ExemptButton.Click += OnExempt;
        ClearButton.Click += OnClear;
        CheckAllButton.Click += OnCheckAll;
        UncheckAllButton.Click += OnUncheckAll;
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
            SetGridItems([]);
            StatusText.Text = "Allowing Store apps requires Windows 8 or later.";
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
            SetGridItems([]);
            StatusText.Text = "Failed to list Store apps: " + ex.Message;
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

        var filtered = view
            .OrderByDescending(i => i.IsExempt)
            .ThenBy(i => i.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();
        SetGridItems(filtered);

        if (_pendingStatus is not null)
        {
            StatusText.Text = _pendingStatus;
            _pendingStatus = null;
            return;
        }

        var exemptCount = _items.Count(i => i.IsExempt);
        if (string.IsNullOrEmpty(query))
            StatusText.Text = $"{_items.Count} apps; {exemptCount} currently allowed.";
        else
            StatusText.Text = $"Showing {filtered.Count} of {_items.Count}; {exemptCount} currently allowed.";
    }

    private void SetGridItems(IReadOnlyList<AppContainerInfo> items)
    {
        // Clearing selection before replacing ItemsSource avoids Avalonia DataGrid crashes.
        _suppressExemptCheckChanged = true;
        try
        {
            PackageGrid.SelectedItem = null;
            _visibleItems = items.ToList();
            PackageGrid.ItemsSource = _visibleItems;
        }
        finally
        {
            _suppressExemptCheckChanged = false;
        }
    }

    private void OnExemptCheckChanged(object? sender, RoutedEventArgs e)
    {
        if (_suppressExemptCheckChanged)
            return;

        ApplyFilter();
    }

    private void OnCheckAll(object? sender, RoutedEventArgs e)
    {
        foreach (var item in _visibleItems)
            item.IsExempt = true;
        ApplyFilter();
    }

    private void OnUncheckAll(object? sender, RoutedEventArgs e)
    {
        foreach (var item in _visibleItems)
            item.IsExempt = false;
        ApplyFilter();
    }

    private void OnExempt(object? sender, RoutedEventArgs e)
    {
        try
        {
            var checkedSids = _items
                .Where(i => i.IsExempt)
                .Select(i => i.AppContainerSid)
                .ToList();
            if (checkedSids.Count == 0)
            {
                StatusText.Text = "Check one or more apps to exempt, then apply.";
                return;
            }

            var ok = AppContainerLoopback.SetExemptions(checkedSids);
            _pendingStatus = ok
                ? $"Exemptions updated ({checkedSids.Count} app(s))."
                : "Failed to set exemptions (try running Inspector elevated).";
            Reload();
        }
        catch (Exception ex)
        {
            StatusText.Text = "Failed to set exemptions: " + ex.Message;
        }
    }

    private void OnClear(object? sender, RoutedEventArgs e)
    {
        try
        {
            var ok = AppContainerLoopback.ClearExemptions();
            _pendingStatus = ok
                ? "All loopback exemptions cleared."
                : "Failed to clear exemptions (try running Inspector elevated).";
            Reload();
        }
        catch (Exception ex)
        {
            StatusText.Text = "Failed to clear exemptions: " + ex.Message;
        }
    }
}
