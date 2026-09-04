using System.Collections.Specialized;
using System.ComponentModel;
using System.Reflection;
using Avalonia;
using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Controls.Notifications;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Titanium.Inspector.Services;
using Titanium.Inspector.ViewModels;

namespace Titanium.Inspector.Views;

public partial class MainWindow : Window
{
    // Avalonia DataGridColumn.GetSortDescription() is internal (no public SortDirection).
    private static readonly MethodInfo? GetSortDescriptionMethod =
        typeof(DataGridColumn).GetMethod(
            "GetSortDescription",
            BindingFlags.Instance | BindingFlags.NonPublic); // NOSONAR S3011 -- Avalonia has no public SortDirection API

    private bool _autoStartStarted;
    private bool _followLatest = true;
    private bool _programmaticScroll;
    private bool _scrollQueued;
    private bool _sessionGridLayoutApplied;
    private ScrollBar? _sessionsVScroll;
    private MainWindowViewModel? _sessionsVm;
    private MainWindowViewModel? _statusVm;
    private WindowNotificationManager? _notificationManager;
    private CancellationTokenSource? _attentionCts;
    private EventHandler? _themeVariantChangedHandler;

    public MainWindow()
    {
        InitializeComponent();
        MacOsNativeMenu.AttachIfMac(this, MainMenu);
        Closing += OnClosing;
        Opened += OnOpened;
        DataContextChanged += OnDataContextChanged;
        SessionsGrid.Loaded += OnSessionsGridLoaded;
        SessionsGrid.SelectionChanged += OnSessionsGridSelectionChanged;
        SessionsGrid.KeyDown += OnSessionsGridKeyDown;
        SessionsGrid.AddHandler(
            InputElement.PointerPressedEvent,
            OnSessionsGridPointerPressed,
            RoutingStrategies.Tunnel);
        HookSessionsCollection(DataContext as MainWindowViewModel);
        HookStatusAttention(DataContext as MainWindowViewModel);
        HookThemeVariantChanged();
    }

    protected override void OnApplyTemplate(TemplateAppliedEventArgs e)
    {
        base.OnApplyTemplate(e);
        EnsureNotificationManager();
    }

    private void EnsureNotificationManager()
    {
        if (_notificationManager is not null)
        {
            return;
        }

        _notificationManager = new WindowNotificationManager(this)
        {
            Position = NotificationPosition.BottomRight,
            MaxItems = 3,
        };

        if (DataContext is MainWindowViewModel vm)
        {
            vm.AttachStatusNotifier(new AvaloniaStatusNotifier(() => _notificationManager));
        }
    }

    private void OnSessionsGridLoaded(object? sender, RoutedEventArgs e)
    {
        AttachSessionsScroll();
        ApplyProcessColumnVisibility();
        ApplySessionGridLayoutIfNeeded();
        ApplySessionColumnHeaderTips();
    }

    private void OnSessionsGridKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Delete || DataContext is not MainWindowViewModel vm)
        {
            return;
        }

        if (vm.RemoveSelectedSessionsCommand.CanExecute(null))
        {
            vm.RemoveSelectedSessionsCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void OnSessionsGridPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(SessionsGrid).Properties.IsRightButtonPressed)
        {
            return;
        }

        var source = e.Source as Control;
        var row = source?.FindAncestorOfType<DataGridRow>()
            ?? (source as DataGridRow);
        if (row?.DataContext is not SessionSnapshot snap)
        {
            return;
        }

        if (SessionsGrid.SelectedItems.Contains(snap))
        {
            return;
        }

        SessionsGrid.SelectedItems.Clear();
        SessionsGrid.SelectedItem = snap;
        if (DataContext is MainWindowViewModel vm)
        {
            vm.SelectedSession = snap;
            vm.SetSelectedSessions([snap]);
        }
    }

    private void OnSessionsGridSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm)
        {
            return;
        }

        var selected = new List<SessionSnapshot>();
        foreach (var item in SessionsGrid.SelectedItems)
        {
            if (item is SessionSnapshot snap)
            {
                selected.Add(snap);
            }
        }

        vm.SetSelectedSessions(selected);
    }

    private async void OnOpened(object? sender, EventArgs e)
    {
        EnsureNotificationManager();
        AttachSessionsScroll();
        ApplySessionGridLayoutIfNeeded();

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

        try
        {
            if (vm.CheckForUpdatesOnStartup
                && Environment.GetEnvironmentVariable("TITANIUM_UPDATE_FEED") != string.Empty)
            {
                // Do not block window open on network; failures stay in StatusText.
                _ = vm.CheckUpdatesAsync(promptIfAvailable: true);
            }
        }
        catch
        {
            // never crash UI on update check failure
        }
    }

    private void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        CaptureAndPersistSessionGridLayout();
        HookSessionsCollection(null);
        HookStatusAttention(null);
        HookThemeVariantChanged(unhook: true);
        _attentionCts?.Cancel();
        _attentionCts?.Dispose();
        _attentionCts = null;
        if (_sessionsVScroll is not null)
        {
            _sessionsVScroll.PropertyChanged -= OnSessionsScrollBarPropertyChanged;
            _sessionsVScroll = null;
        }

        if (DataContext is MainWindowViewModel vm)
        {
            // Off UI thread — WinINET restore from EnsureShutdown deadlocks the closing window.
            vm.BeginBackgroundShutdown();
        }
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        HookSessionsCollection(DataContext as MainWindowViewModel);
        HookStatusAttention(DataContext as MainWindowViewModel);
        if (_notificationManager is not null && DataContext is MainWindowViewModel vm)
        {
            vm.AttachStatusNotifier(new AvaloniaStatusNotifier(() => _notificationManager));
        }

        ApplyProcessColumnVisibility();
        ApplySessionGridLayoutIfNeeded();
        HookThemeVariantChanged();
    }

    private void HookThemeVariantChanged(bool unhook = false)
    {
        if (Application.Current is not { } app)
        {
            return;
        }

        if (_themeVariantChangedHandler is not null)
        {
            app.ActualThemeVariantChanged -= _themeVariantChangedHandler;
            _themeVariantChangedHandler = null;
        }

        if (unhook)
        {
            return;
        }

        _themeVariantChangedHandler = OnActualThemeVariantChanged;
        app.ActualThemeVariantChanged += _themeVariantChangedHandler;
    }

    private void OnActualThemeVariantChanged(object? sender, EventArgs e)
    {
        if (DataContext is MainWindowViewModel vm)
        {
            vm.NotifyThemeVariantChanged();
        }
    }

    private void HookStatusAttention(MainWindowViewModel? vm)
    {
        if (ReferenceEquals(_statusVm, vm))
        {
            return;
        }

        if (_statusVm is not null)
        {
            _statusVm.PropertyChanged -= OnStatusVmPropertyChanged;
        }

        _statusVm = vm;
        if (_statusVm is not null)
        {
            _statusVm.PropertyChanged += OnStatusVmPropertyChanged;
        }
    }

    private void OnStatusVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainWindowViewModel.StatusAttentionTick))
        {
            PulseStatusAttention();
        }
    }

    private async void PulseStatusAttention()
    {
        _attentionCts?.Cancel();
        _attentionCts?.Dispose();
        _attentionCts = new CancellationTokenSource();
        var token = _attentionCts.Token;

        try
        {
            // Brief highlight behind the status text so results are harder to miss.
            StatusTextHost.Background = ResolveStatusAttentionBackground();
            StatusTextBlock.Opacity = 1;
            await Task.Delay(180, token);
            StatusTextBlock.Opacity = 0.55;
            await Task.Delay(160, token);
            StatusTextBlock.Opacity = 1;
            await Task.Delay(900, token);
            StatusTextHost.Background = Brushes.Transparent;
        }
        catch (OperationCanceledException)
        {
            // superseded by a newer status result
        }
    }

    private static IBrush ResolveStatusAttentionBackground()
    {
        if (Application.Current?.TryGetResource(
                "StatusFeedbackBusyBrush",
                Application.Current.ActualThemeVariant,
                out var resource) == true
            && resource is SolidColorBrush busy)
        {
            var c = busy.Color;
            return new SolidColorBrush(Color.FromArgb(56, c.R, c.G, c.B));
        }

        return new SolidColorBrush(Color.FromArgb(56, 0, 120, 212));
    }

    private void HookSessionsCollection(MainWindowViewModel? vm)
    {
        if (ReferenceEquals(_sessionsVm, vm))
        {
            return;
        }

        if (_sessionsVm is not null)
        {
            _sessionsVm.Sessions.CollectionChanged -= OnSessionsCollectionChanged;
        }

        _sessionsVm = vm;
        if (_sessionsVm is not null)
        {
            _sessionsVm.Sessions.CollectionChanged += OnSessionsCollectionChanged;
        }
    }

    private void AttachSessionsScroll()
    {
        if (_sessionsVScroll is not null)
        {
            return;
        }

        // DataGrid does not host a ScrollViewer; vertical scrolling is PART_VerticalScrollbar.
        _sessionsVScroll = SessionsGrid.FindControl<ScrollBar>("PART_VerticalScrollbar")
            ?? SessionsGrid.GetVisualDescendants()
                .OfType<ScrollBar>()
                .FirstOrDefault(bar => bar.Orientation == Orientation.Vertical);

        if (_sessionsVScroll is null)
        {
            return;
        }

        _sessionsVScroll.PropertyChanged += OnSessionsScrollBarPropertyChanged;
    }

    private void OnSessionsScrollBarPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (_sessionsVScroll is null
            || (e.Property != RangeBase.ValueProperty
                && e.Property != RangeBase.MaximumProperty
                && e.Property != ScrollBar.ViewportSizeProperty))
        {
            return;
        }

        var value = _sessionsVScroll.Value;
        var maximum = _sessionsVScroll.Maximum;
        var userMovedOffset = e.Property == RangeBase.ValueProperty;
        var edge = ResolveFollowEdge();
        var isNearFollowEdge = SessionListFollowLatest.IsNearFollowEdgeByScrollBar(
            edge, value, maximum, SessionListFollowLatest.DefaultThresholdPx);
        var allContentVisible = maximum <= 0;

        _followLatest = SessionListFollowLatest.UpdateFollowAfterScroll(
            _followLatest, _programmaticScroll, userMovedOffset, isNearFollowEdge, allContentVisible);
    }

    private void OnSessionsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Reset
            || (e.Action == NotifyCollectionChangedAction.Remove && _sessionsVm is { Sessions.Count: 0 }))
        {
            // Clear vs filter: filter Reset is immediately followed by Adds on this turn.
            Dispatcher.UIThread.Post(() =>
            {
                if (SessionListFollowLatest.ShouldResumeFollowAfterReset(_sessionsVm?.Sessions.Count ?? 0))
                {
                    _followLatest = true;
                }
            }, DispatcherPriority.Background);
        }

        if (e.Action is not (NotifyCollectionChangedAction.Add or NotifyCollectionChangedAction.Reset))
        {
            return;
        }

        if (SessionListFollowLatest.ShouldScrollToLatest(
                _followLatest, ResolveFollowEdge(), _sessionsVm is { Sessions.Count: > 0 }))
        {
            RequestScrollToLatest();
        }
    }

    private void RequestScrollToLatest()
    {
        if (_scrollQueued)
        {
            return;
        }

        _scrollQueued = true;
        Dispatcher.UIThread.Post(() =>
        {
            _scrollQueued = false;
            ScrollToLatest();
        }, DispatcherPriority.Loaded);
    }

    private void ScrollToLatest()
    {
        var edge = ResolveFollowEdge();
        if (_sessionsVm is null
            || !SessionListFollowLatest.ShouldScrollToLatest(
                _followLatest, edge, _sessionsVm.Sessions.Count > 0))
        {
            return;
        }

        AttachSessionsScroll();
        // Newest live row is always the last append; sort only changes visual position.
        _programmaticScroll = true;
        SessionsGrid.ScrollIntoView(_sessionsVm.Sessions[^1], column: null);
        if (_sessionsVScroll is not null)
        {
            _sessionsVScroll.Value = edge == SessionListFollowEdge.Top
                ? 0
                : _sessionsVScroll.Maximum;
        }

        // Layout/virtualization may raise ValueChanged after this method returns.
        Dispatcher.UIThread.Post(() =>
        {
            Dispatcher.UIThread.Post(() => _programmaticScroll = false, DispatcherPriority.Background);
        }, DispatcherPriority.Background);
    }

    private SessionListFollowEdge ResolveFollowEdge()
    {
        if (GetSortDescriptionMethod is null)
        {
            return SessionListFollowEdge.Bottom;
        }

        try
        {
            DataGridColumn? sortedColumn = null;
            DataGridSortDescription? sortedDescription = null;
            foreach (var column in SessionsGrid.Columns)
            {
                if (GetSortDescriptionMethod.Invoke(column, null) is not DataGridSortDescription description)
                {
                    continue;
                }

                if (sortedColumn is not null)
                {
                    // Multi-column sort: do not auto-follow.
                    return SessionListFollowEdge.None;
                }

                sortedColumn = column;
                sortedDescription = description;
            }

            var anySorted = sortedColumn is not null;
            var idIsSoleSort = sortedColumn is not null && IsIdColumn(sortedColumn);
            ListSortDirection? idDirection = idIsSoleSort ? sortedDescription!.Direction : null;
            return SessionListFollowLatest.ResolveFollowEdge(anySorted, idIsSoleSort, idDirection);
        }
        catch
        {
            return SessionListFollowEdge.Bottom;
        }
    }

    private void ApplyProcessColumnVisibility()
    {
        if (DataContext is not MainWindowViewModel vm || SessionsGrid.Columns.Count == 0)
        {
            return;
        }

        foreach (var column in SessionsGrid.Columns)
        {
            if (string.Equals(SessionGridLayout.GetColumnKey(column.Header), "Process", StringComparison.Ordinal))
            {
                column.IsVisible = vm.ShowProcessColumn;
            }
        }
    }

    private void ApplySessionGridLayoutIfNeeded()
    {
        if (_sessionGridLayoutApplied
            || SessionsGrid.Columns.Count == 0
            || DataContext is not MainWindowViewModel vm)
        {
            return;
        }

        _sessionGridLayoutApplied = true;
        ApplyProcessColumnVisibility();
        var layout = vm.GetSessionGridLayout();
        var byKey = SessionGridLayout.IndexByKey(layout?.Columns);

        foreach (var column in SessionsGrid.Columns)
        {
            var key = SessionGridLayout.GetColumnKey(column.Header);
            if (key is null || !byKey.TryGetValue(key, out var state) || state.Width <= 0)
            {
                continue;
            }

            // Don't restore a width narrower than MinWidth — that clips headers like "Duration (ms)".
            var width = state.Width;
            if (column.MinWidth > 0 && width < column.MinWidth)
            {
                width = column.MinWidth;
            }

            column.Width = new DataGridLength(width);
        }

        foreach (var (column, state) in SessionsGrid.Columns
                     .Select(c => (Column: c, Key: SessionGridLayout.GetColumnKey(c.Header)))
                     .Where(x => x.Key is not null && byKey.ContainsKey(x.Key))
                     .Select(x => (x.Column, State: byKey[x.Key!]))
                     .OrderBy(x => x.State.DisplayIndex))
        {
            if (state.DisplayIndex < 0 || state.DisplayIndex >= SessionsGrid.Columns.Count)
            {
                continue;
            }

            try
            {
                if (column.DisplayIndex != state.DisplayIndex)
                {
                    column.DisplayIndex = state.DisplayIndex;
                }
            }
            catch
            {
                // DisplayIndex can throw while the grid is still wiring columns.
            }
        }

        SessionGridLayout.ResolveSort(layout, out var sortKey, out var sortDirection);
        var sortColumn = SessionsGrid.Columns.FirstOrDefault(c =>
            string.Equals(SessionGridLayout.GetColumnKey(c.Header), sortKey, StringComparison.Ordinal));
        sortColumn?.Sort(sortDirection);
        ApplySessionColumnHeaderTips();
    }

    private void ApplySessionColumnHeaderTips()
    {
        foreach (var header in SessionsGrid.GetVisualDescendants().OfType<DataGridColumnHeader>())
        {
            var tip = SessionGridLayout.GetColumnKey(header.Content) switch
            {
                "Duration" => "Total request time from session start to complete (milliseconds).",
                "TTFB" => "Time until first response byte (TTFB), in milliseconds.",
                "Protocol" => "HTTP/1.1, HTTP/2, … between client and proxy.",
                "Size" => "Response body size (B below 1 KB, otherwise KB / MB).",
                _ => null,
            };

            if (tip is not null)
            {
                ToolTip.SetTip(header, tip);
            }
        }
    }

    private void CaptureAndPersistSessionGridLayout()
    {
        if (DataContext is not MainWindowViewModel vm || SessionsGrid.Columns.Count == 0)
        {
            return;
        }

        var layout = new SessionGridLayoutDto();
        foreach (var column in SessionsGrid.Columns)
        {
            if (!column.IsVisible)
            {
                continue;
            }

            var key = SessionGridLayout.GetColumnKey(column.Header);
            if (key is null)
            {
                continue;
            }

            var width = SessionGridLayout.ResolvePersistableWidth(
                column.ActualWidth,
                column.Width.IsAbsolute,
                column.Width.Value);
            if (width <= 0)
            {
                continue;
            }

            layout.Columns.Add(new SessionGridColumnStateDto
            {
                Key = key,
                Width = width,
                DisplayIndex = column.DisplayIndex,
            });
        }

        CaptureActiveSort(layout);
        vm.PersistSessionGridLayout(layout);
    }

    private void CaptureActiveSort(SessionGridLayoutDto layout)
    {
        if (GetSortDescriptionMethod is null)
        {
            SessionGridLayout.ResolveSort(null, out var defaultKey, out var defaultDirection);
            layout.SortColumnKey = defaultKey;
            layout.SortDirection = defaultDirection;
            return;
        }

        try
        {
            DataGridColumn? sortedColumn = null;
            DataGridSortDescription? sortedDescription = null;
            foreach (var column in SessionsGrid.Columns)
            {
                if (GetSortDescriptionMethod.Invoke(column, null) is not DataGridSortDescription description)
                {
                    continue;
                }

                if (sortedColumn is not null)
                {
                    // Multi-column sort: persist nothing special; factory sort on next launch.
                    SessionGridLayout.ResolveSort(null, out var defaultKey, out var defaultDirection);
                    layout.SortColumnKey = defaultKey;
                    layout.SortDirection = defaultDirection;
                    return;
                }

                sortedColumn = column;
                sortedDescription = description;
            }

            if (sortedColumn is not null
                && SessionGridLayout.GetColumnKey(sortedColumn.Header) is { } key)
            {
                layout.SortColumnKey = key;
                layout.SortDirection = sortedDescription!.Direction;
                return;
            }
        }
        catch
        {
            // fall through to factory default
        }

        SessionGridLayout.ResolveSort(null, out var fallbackKey, out var fallbackDirection);
        layout.SortColumnKey = fallbackKey;
        layout.SortDirection = fallbackDirection;
    }

    private static bool IsIdColumn(DataGridColumn column)
    {
        if (ReferenceEquals(column.CustomSortComparer, SessionIdComparer.Instance))
        {
            return true;
        }

        return column.Header is string header
            && header.Equals("Id", StringComparison.Ordinal);
    }
}
