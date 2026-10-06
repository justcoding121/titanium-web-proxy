using Avalonia.Controls;
using Avalonia.Interactivity;
using Titanium.Inspector.Services;

namespace Titanium.Inspector.Views;

public partial class LoopbackExemptWindow : Window
{
    private readonly LoopbackExemptSession _session;
    private List<AppContainerInfo> _items = [];
    private HashSet<string> _committedSids = new(StringComparer.OrdinalIgnoreCase);
    private string? _pendingStatus;
    private string _closeSummary = LoopbackExemptCopy.ClosedUnchanged;
    private bool _changed;
    private bool _suppressExemptCheckChanged;
    private int _commitGate;
    private List<AppContainerInfo> _visibleItems = [];

    public LoopbackExemptWindow()
        : this(new LoopbackExemptSession())
    {
    }

    public LoopbackExemptWindow(LoopbackExemptSession session)
    {
        _session = session;
        InitializeComponent();
        IntroText.Text = LoopbackExemptCopy.Intro;
        ExemptButton.Click += OnExempt;
        ClearButton.Click += OnClear;
        CheckAllButton.Click += OnCheckAll;
        UncheckAllButton.Click += OnUncheckAll;
        CloseButton.Click += (_, _) => Close();
        FilterBox.TextChanged += (_, _) => ApplyFilter();
        Opened += (_, _) =>
        {
            RefreshProxyWarning();
            Reload();
        };
    }

    public static async Task<LoopbackExemptResult> ShowAsync(Window owner, LoopbackExemptSession session)
    {
        var w = new LoopbackExemptWindow(session);
        await w.ShowDialog(owner);
        return new LoopbackExemptResult
        {
            Changed = w._changed,
            StatusText = w._closeSummary,
        };
    }

    private void Reload()
    {
        if (!AppContainerLoopback.IsSupported)
        {
            _items = [];
            _committedSids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            SetGridItems([]);
            StatusText.Text = "Allowing Store apps requires Windows 8 or later.";
            return;
        }

        try
        {
            _items = AppContainerLoopback.ListContainers().ToList();
            _committedSids = _items
                .Where(i => i.IsExempt)
                .Select(i => i.AppContainerSid)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            ApplyFilter();
        }
        catch (Exception ex)
        {
            _items = [];
            _committedSids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
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

        // Do not call ApplyFilter() here. Rebinding ItemsSource while template CheckBoxes
        // raise IsCheckedChanged (Check all / Uncheck all / row toggle) re-enters and hangs.
        UpdateStatusCounts();
    }

    private void OnCheckAll(object? sender, RoutedEventArgs e)
    {
        _suppressExemptCheckChanged = true;
        try
        {
            foreach (var item in _visibleItems)
                item.IsExempt = true;
        }
        finally
        {
            _suppressExemptCheckChanged = false;
        }

        ApplyFilter();
    }

    private void OnUncheckAll(object? sender, RoutedEventArgs e)
    {
        _suppressExemptCheckChanged = true;
        try
        {
            foreach (var item in _visibleItems)
                item.IsExempt = false;
        }
        finally
        {
            _suppressExemptCheckChanged = false;
        }

        ApplyFilter();
    }

    private void UpdateStatusCounts()
    {
        var query = FilterBox.Text?.Trim() ?? "";
        var exemptCount = _items.Count(i => i.IsExempt);
        if (string.IsNullOrEmpty(query))
            StatusText.Text = $"{_items.Count} apps; {exemptCount} currently allowed.";
        else
            StatusText.Text = $"Showing {_visibleItems.Count} of {_items.Count}; {exemptCount} currently allowed.";
    }

    private async void OnExempt(object? sender, RoutedEventArgs e)
    {
        if (Interlocked.Exchange(ref _commitGate, 1) != 0)
            return;

        SetCommitBusy(true);
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

            if (LoopbackExemptCopy.SameSet(_committedSids, checkedSids))
            {
                StatusText.Text = LoopbackExemptCopy.UnchangedStatus(checkedSids.Count);
                RefreshProxyWarning();
                return;
            }

            var ok = await Task.Run(() => AppContainerLoopback.SetExemptions(checkedSids));
            if (!ok)
            {
                _pendingStatus = "Failed to set exemptions (try running Inspector elevated).";
                Reload();
                return;
            }

            var refreshed = await RefreshRunningAppsIfReadyAsync(setChanged: true);
            var readiness = _session.CurrentReadiness();
            RememberCommit(LoopbackExemptCopy.AppliedStatus(checkedSids.Count, readiness, refreshed));
            Reload();
        }
        catch (Exception ex)
        {
            StatusText.Text = "Failed to set exemptions: " + ex.Message;
        }
        finally
        {
            SetCommitBusy(false);
            Interlocked.Exchange(ref _commitGate, 0);
        }
    }

    private async void OnClear(object? sender, RoutedEventArgs e)
    {
        if (Interlocked.Exchange(ref _commitGate, 1) != 0)
            return;

        SetCommitBusy(true);
        try
        {
            var hadExemptions = _committedSids.Count > 0;
            var ok = await Task.Run(AppContainerLoopback.ClearExemptions);
            if (!ok)
            {
                _pendingStatus = "Failed to clear exemptions (try running Inspector elevated).";
                Reload();
                return;
            }

            var refreshed = await RefreshRunningAppsIfReadyAsync(setChanged: hadExemptions);

            RememberCommit(LoopbackExemptCopy.ClearedStatus(_session.CurrentReadiness(), refreshed));
            Reload();
        }
        catch (Exception ex)
        {
            StatusText.Text = "Failed to clear exemptions: " + ex.Message;
        }
        finally
        {
            SetCommitBusy(false);
            Interlocked.Exchange(ref _commitGate, 0);
        }
    }

    private async Task<bool> RefreshRunningAppsIfReadyAsync(bool setChanged)
    {
        if (!LoopbackExemptCopy.ShouldRefreshRunningApps(setChanged, _session.CurrentReadiness()) ||
            _session.RefreshRunningAppsAsync is null)
        {
            return false;
        }

        StatusText.Text = LoopbackExemptCopy.RefreshingStatus;
        return await _session.RefreshRunningAppsAsync();
    }

    private void RememberCommit(string summary)
    {
        _changed = true;
        _closeSummary = summary;
        _pendingStatus = summary;
        RefreshProxyWarning();
    }

    private void RefreshProxyWarning()
    {
        var warning = LoopbackExemptCopy.ProxyWarning(_session.CurrentReadiness());
        ProxyWarningText.Text = warning ?? "";
        ProxyWarningText.IsVisible = warning is not null;
    }

    private void SetCommitBusy(bool busy)
    {
        ExemptButton.IsEnabled = !busy;
        ClearButton.IsEnabled = !busy;
        CheckAllButton.IsEnabled = !busy;
        UncheckAllButton.IsEnabled = !busy;
    }
}
