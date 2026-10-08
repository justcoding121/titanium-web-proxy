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
    private bool _sessionsResetInFlight;
    private bool _scrollQueued;
    private bool _sessionGridLayoutApplied;
    private ScrollBar? _sessionsVScroll;
    private MainWindowViewModel? _sessionsVm;
    private MainWindowViewModel? _statusVm;
    private MainWindowViewModel? _toggleSyncVm;
    private MainWindowViewModel? _gridColumnsVm;
    private MainWindowViewModel? _inspectLayoutVm;
    private readonly Dictionary<string, DataGridLength> _defaultColumnWidths = new(StringComparer.Ordinal);
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
        // Under heavy proxy load the machine is CPU-saturated by worker threads (and browsers); the UI thread
        // must win the scheduler or clicks stall for seconds even when its own work is small.
        try
        {
            System.Threading.Thread.CurrentThread.Priority = System.Threading.ThreadPriority.AboveNormal;
        }
        catch
        {
            // Best effort: restricted environments may refuse priority changes.
        }

        // Capture-driven grid refreshes step aside while the user is interacting (see CaptureUiGovernor).
        AddHandler(InputElement.PointerPressedEvent, OnUserInputForCaptureUi, RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(InputElement.PointerMovedEvent, OnUserInputForCaptureUi, RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(InputElement.PointerWheelChangedEvent, OnUserInputForCaptureUi, RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(InputElement.KeyDownEvent, OnUserInputForCaptureUi, RoutingStrategies.Tunnel, handledEventsToo: true);
        SessionsGrid.AddHandler(
            InputElement.PointerPressedEvent,
            OnSessionsGridPointerPressed,
            RoutingStrategies.Tunnel);
        SessionsGrid.AddHandler(
            ContextRequestedEvent,
            OnSessionsGridContextRequested,
            RoutingStrategies.Tunnel);
        HookSessionsCollection(DataContext as MainWindowViewModel);
        HookStatusAttention(DataContext as MainWindowViewModel);
        HookOneWayToggleVisualSync(DataContext as MainWindowViewModel);
        HookGridColumnsChanged(DataContext as MainWindowViewModel);
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
        ApplyColumnVisibility();
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

        if (DataContext is not MainWindowViewModel vm)
        {
            return;
        }

        // Right-click only prepares selection for the context menu — do not open Inspect.
        using (vm.SuppressOpenSessionDetails())
        {
            SessionsGrid.SelectedItems.Clear();
            SessionsGrid.SelectedItem = snap;
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
        HookOneWayToggleVisualSync(null);
        HookGridColumnsChanged(null);
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
        HookOneWayToggleVisualSync(DataContext as MainWindowViewModel);
        HookGridColumnsChanged(DataContext as MainWindowViewModel);
        HookInspectHeadersLayout(DataContext as MainWindowViewModel);
        if (_notificationManager is not null && DataContext is MainWindowViewModel vm)
        {
            vm.AttachStatusNotifier(new AvaloniaStatusNotifier(() => _notificationManager));
        }

        ApplyColumnVisibility();
        ApplySessionGridLayoutIfNeeded();
        HookThemeVariantChanged();
    }

    private const int InspectHeadersRowIndex = 2;
    private const int InspectBodyRowIndex = 4;

    private void HookInspectHeadersLayout(MainWindowViewModel? vm)
    {
        if (_inspectLayoutVm is not null)
        {
            _inspectLayoutVm.PropertyChanged -= OnInspectHeadersLayoutChanged;
        }

        _inspectLayoutVm = vm;
        if (vm is null)
        {
            return;
        }

        vm.PropertyChanged += OnInspectHeadersLayoutChanged;
        ApplyInspectHeadersLayout();
    }

    private void OnInspectHeadersLayoutChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainWindowViewModel.InspectHeadersCollapsed)
            or nameof(MainWindowViewModel.InspectHeadersStar)
            or nameof(MainWindowViewModel.InspectBodyStar))
        {
            ApplyInspectHeadersLayout();
        }
    }

    private void OnInspectHostSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        if (DataContext is MainWindowViewModel vm)
        {
            vm.ApplyInspectPaneHeight(e.NewSize.Height);
        }
    }

    private void OnInspectHeadersSplitterDragCompleted(object? sender, VectorEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm || sender is not GridSplitter splitter)
        {
            return;
        }

        if (splitter.Parent is not Grid grid)
        {
            return;
        }

        var headers = grid.RowDefinitions[InspectHeadersRowIndex];
        var body = grid.RowDefinitions[InspectBodyRowIndex];
        vm.CommitInspectHeadersRatio(DefinitionPixels(headers), DefinitionPixels(body));
        ApplyInspectHeadersLayout();
    }

    private void OnInspectHeadersSplitterDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm)
        {
            return;
        }

        vm.ResetInspectHeadersRatio();
        e.Handled = true;
    }

    private void ApplyInspectHeadersLayout()
    {
        if (_inspectLayoutVm is null)
        {
            return;
        }

        ApplyInspectSplit(RequestInspectSplit, _inspectLayoutVm);
        ApplyInspectSplit(ResponseInspectSplit, _inspectLayoutVm);
    }

    private static void ApplyInspectSplit(Grid grid, MainWindowViewModel vm)
    {
        var headers = grid.RowDefinitions[InspectHeadersRowIndex];
        var body = grid.RowDefinitions[InspectBodyRowIndex];
        if (vm.InspectHeadersCollapsed)
        {
            headers.MinHeight = 0;
            headers.Height = new GridLength(0);
            body.MinHeight = 120;
            body.Height = new GridLength(1, GridUnitType.Star);
            return;
        }

        headers.MinHeight = 72;
        headers.Height = new GridLength(vm.InspectHeadersStar, GridUnitType.Star);
        body.MinHeight = 120;
        body.Height = new GridLength(vm.InspectBodyStar, GridUnitType.Star);
    }

    private static double DefinitionPixels(RowDefinition definition)
    {
        if (definition.Height.GridUnitType == GridUnitType.Pixel)
        {
            return definition.Height.Value;
        }

        return definition.ActualHeight;
    }

    /// <summary>
    /// Avalonia 11.2: CheckBox/MenuItem toggle severs OneWay IsChecked bindings (SetValue).
    /// Push visuals with SetCurrentValue whenever Decrypt/SystemProxy/ProxyLoopback change.
    /// </summary>
    private void HookOneWayToggleVisualSync(MainWindowViewModel? vm)
    {
        if (_toggleSyncVm is not null)
            _toggleSyncVm.SyncToggleVisual = null;

        _toggleSyncVm = vm;
        if (vm is null)
            return;

        vm.SyncToggleVisual = (propertyName, isChecked) =>
        {
            if (propertyName == nameof(MainWindowViewModel.DecryptHttps))
            {
                OneWayToggleVisualSync.Apply(DecryptHttpsCheck, isChecked);
                OneWayToggleVisualSync.Apply(MenuDecryptHttps, isChecked);
            }
            else if (propertyName == nameof(MainWindowViewModel.SystemProxy))
            {
                OneWayToggleVisualSync.Apply(SystemProxyCheck, isChecked);
                OneWayToggleVisualSync.Apply(MenuToggleSystemProxy, isChecked);
            }
            else if (propertyName == nameof(MainWindowViewModel.ProxyLoopback))
            {
                OneWayToggleVisualSync.Apply(MenuProxyLocalhost, isChecked);
            }
        };
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
            _ = PulseStatusAttentionAsync();
        }
    }

    private async Task PulseStatusAttentionAsync()
    {
        if (_attentionCts is not null)
        {
            await _attentionCts.CancelAsync();
            _attentionCts.Dispose();
        }
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

    private static SolidColorBrush ResolveStatusAttentionBackground()
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
            if (_sessionsVm.Sessions is SessionListCollection oldList)
            {
                oldList.Replacing -= OnSessionsReplacing;
            }
        }

        _sessionsVm = vm;
        if (_sessionsVm is not null)
        {
            _sessionsVm.Sessions.CollectionChanged += OnSessionsCollectionChanged;
            if (_sessionsVm.Sessions is SessionListCollection newList)
            {
                newList.Replacing += OnSessionsReplacing;
            }
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

        // A list swap (filter added/cleared) makes the DataGrid reset its offset; that is not the user scrolling
        // away, so it must not pause following.
        _followLatest = SessionListFollowLatest.UpdateFollowAfterScroll(
            _followLatest,
            _programmaticScroll || _sessionsResetInFlight,
            userMovedOffset,
            isNearFollowEdge,
            allContentVisible);
    }

    private static void OnUserInputForCaptureUi(object? sender, RoutedEventArgs e) => CaptureUiGovernor.NoteUserInput();

    private void OnSessionsReplacing() => _sessionsResetInFlight = true;

    private void OnSessionsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Reset)
        {
            // Also covers collections that raise Reset without the Replacing hook (Clear).
            _sessionsResetInFlight = true;
        }

        if (e.Action == NotifyCollectionChangedAction.Reset
            || (e.Action == NotifyCollectionChangedAction.Remove && _sessionsVm is { Sessions.Count: 0 }))
        {
            // Runs after layout and after the scroll-to-latest below (Loaded outranks Background), once the
            // DataGrid has settled on its new extent.
            Dispatcher.UIThread.Post(() =>
            {
                _sessionsResetInFlight = false;
                if (SessionListFollowLatest.ShouldResumeFollowAfterReset(_sessionsVm?.Sessions.Count ?? 0)
                    || _sessionsVScroll is { Maximum: <= 0 })
                {
                    // Cleared, or the result fits on screen: nothing is hidden, so follow the live edge again.
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

    /// <summary>Column visibility from the catalog defaults, saved choices, and the Process platform gate.</summary>
    private void ApplyColumnVisibility()
    {
        if (DataContext is not MainWindowViewModel vm || SessionsGrid.Columns.Count == 0)
        {
            return;
        }

        foreach (var column in SessionsGrid.Columns)
        {
            var key = SessionGridLayout.GetColumnKey(column.Header);
            if (SessionGridColumnCatalog.Find(key) is not null)
            {
                column.IsVisible = vm.IsGridColumnVisible(key!);
            }
        }
    }

    /// <summary>XAML widths, remembered once so Reset columns can restore them.</summary>
    private void SnapshotDefaultColumnWidths()
    {
        if (_defaultColumnWidths.Count > 0)
        {
            return;
        }

        foreach (var column in SessionsGrid.Columns)
        {
            if (SessionGridLayout.GetColumnKey(column.Header) is { } key)
            {
                _defaultColumnWidths[key] = column.Width;
            }
        }
    }

    private DataGridColumn? FindGridColumn(string key) =>
        SessionsGrid.Columns.FirstOrDefault(c =>
            string.Equals(SessionGridLayout.GetColumnKey(c.Header), key, StringComparison.Ordinal));

    private void HookGridColumnsChanged(MainWindowViewModel? vm)
    {
        if (_gridColumnsVm is not null)
        {
            _gridColumnsVm.GridColumnsChanged -= OnGridColumnsChanged;
        }

        _gridColumnsVm = vm;
        if (vm is not null)
        {
            vm.GridColumnsChanged += OnGridColumnsChanged;
        }
    }

    private void OnGridColumnsChanged(string? key)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            ApplyGridColumnsChange(key);
        }
        else
        {
            Dispatcher.UIThread.Post(() => ApplyGridColumnsChange(key), DispatcherPriority.Input);
        }
    }

    private void ApplyGridColumnsChange(string? key)
    {
        if (DataContext is not MainWindowViewModel || SessionsGrid.Columns.Count == 0)
        {
            return;
        }

        if (key is null)
        {
            ResetGridColumnsToDefaults();
        }
        else if (FindGridColumn(key) is { } column)
        {
            ApplyColumnVisibility();
            if (!column.IsVisible)
            {
                FallBackToDefaultSortIfHiddenColumnSorted();
            }
        }

        SyncColumnMenuChecks();
        // Headers of newly shown columns are created lazily; give them their tooltips.
        Dispatcher.UIThread.Post(ApplySessionColumnHeaderTips, DispatcherPriority.Background);
    }

    private void ResetGridColumnsToDefaults()
    {
        SnapshotDefaultColumnWidths();
        ApplyColumnVisibility();
        for (var index = 0; index < SessionsGrid.Columns.Count; index++)
        {
            var column = SessionsGrid.Columns[index];
            if (SessionGridLayout.GetColumnKey(column.Header) is { } key
                && _defaultColumnWidths.TryGetValue(key, out var width))
            {
                column.Width = width;
            }

            try
            {
                if (column.DisplayIndex != index)
                {
                    column.DisplayIndex = index;
                }
            }
            catch
            {
                // DisplayIndex can throw while the grid is still wiring columns.
            }
        }

        ApplyDefaultSort();
    }

    /// <summary>Id ascending (factory default). Clears first because <c>Sort</c> may add to an active sort.</summary>
    private void ApplyDefaultSort()
    {
        SessionsGrid.CollectionView?.SortDescriptions.Clear();
        SessionsGrid.Columns.FirstOrDefault(IsIdColumn)?.Sort(ListSortDirection.Ascending);
    }

    /// <summary>
    /// A hidden column keeps sorting the rows with no header to show it. Id is exempt: hiding it must not
    /// change the default order.
    /// </summary>
    private void FallBackToDefaultSortIfHiddenColumnSorted()
    {
        if (GetSortDescriptionMethod is null)
        {
            return;
        }

        try
        {
            foreach (var column in SessionsGrid.Columns)
            {
                if (!column.IsVisible
                    && !IsIdColumn(column)
                    && GetSortDescriptionMethod.Invoke(column, null) is DataGridSortDescription)
                {
                    ApplyDefaultSort();
                    return;
                }
            }
        }
        catch
        {
            // keep the current sort
        }
    }

    /// <summary>Options &gt; Columns check marks follow the effective visibility (OneWay binding would be severed).</summary>
    private void SyncColumnMenuChecks()
    {
        if (DataContext is not MainWindowViewModel vm)
        {
            return;
        }

        foreach (var item in MenuColumns.Items.OfType<MenuItem>())
        {
            if (item.CommandParameter is string key)
            {
                OneWayToggleVisualSync.Apply(item, vm.IsGridColumnVisible(key));
            }
        }
    }

    /// <summary>Right-click on a column header: pick columns (rows keep their own menu).</summary>
    private void OnSessionsGridContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm || e.Source is not Visual source)
        {
            return;
        }

        var header = source as DataGridColumnHeader ?? source.FindAncestorOfType<DataGridColumnHeader>();
        var onHeaderStrip = header is not null
            || source is DataGridColumnHeadersPresenter
            || source.FindAncestorOfType<DataGridColumnHeadersPresenter>() is not null;
        if (!onHeaderStrip)
        {
            return;
        }

        e.Handled = true;
        var menu = BuildColumnChooserMenu(vm);
        menu.Open(header ?? (Control)SessionsGrid);
    }

    /// <summary>Header context menu: one check item per catalog column plus Reset columns.</summary>
    public static ContextMenu BuildColumnChooserMenu(MainWindowViewModel vm)
    {
        var menu = new ContextMenu();
        var optionalSectionStarted = false;
        foreach (var info in SessionGridColumnCatalog.All)
        {
            if (info.PlatformGated && !vm.ShowProcessColumn)
            {
                continue;
            }

            if (!info.DefaultVisible && !optionalSectionStarted)
            {
                // Separate the original columns from the optional ones, like Options > Columns.
                optionalSectionStarted = true;
                menu.Items.Add(new Separator());
            }

            menu.Items.Add(new MenuItem
            {
                Header = info.MenuLabel,
                ToggleType = MenuItemToggleType.CheckBox,
                IsChecked = vm.IsGridColumnVisible(info.Key),
                IsEnabled = info.CanHide,
                Command = vm.ToggleGridColumnCommand,
                CommandParameter = info.Key,
            });
        }

        menu.Items.Add(new Separator());
        menu.Items.Add(new MenuItem
        {
            Header = "Reset columns",
            Command = vm.ResetGridColumnsCommand,
        });
        return menu;
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
        SnapshotDefaultColumnWidths();
        ApplyColumnVisibility();
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

        RestoreSavedColumnOrder(byKey);

        SessionGridLayout.ResolveSort(layout, out var sortKey, out var sortDirection);
        var sortColumn = SessionsGrid.Columns.FirstOrDefault(c =>
            string.Equals(SessionGridLayout.GetColumnKey(c.Header), sortKey, StringComparison.Ordinal));
        if (sortColumn is { IsVisible: false } && !IsIdColumn(sortColumn))
        {
            // The saved sort column is hidden: nothing shows why rows are ordered that way.
            ApplyDefaultSort();
        }
        else
        {
            sortColumn?.Sort(sortDirection);
        }

        SyncColumnMenuChecks();
        ApplySessionColumnHeaderTips();
    }

    private void RestoreSavedColumnOrder(Dictionary<string, SessionGridColumnStateDto> byKey)
    {
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
            catch (Exception)
            {
                // DisplayIndex throws while the grid is still wiring columns.
            }
        }
    }

    private void ApplySessionColumnHeaderTips()
    {
        foreach (var header in SessionsGrid.GetVisualDescendants().OfType<DataGridColumnHeader>())
        {
            var tip = SessionGridLayout.GetColumnKey(header.Content) switch
            {
                "URL" => "Path and query (empty for CONNECT tunnels). The host is in the Host column; hover a row for the full URL.",
                "Duration" => "Total request time from session start to complete (milliseconds).",
                "TTFB" => "Time until first response byte (TTFB), in milliseconds.",
                "Protocol" => "HTTP/1.1, HTTP/2, … between client and proxy.",
                "Size" => "Response body size (B below 1 KB, otherwise KB / MB).",
                "Started" => "When the request started (local time). Hover a row for the full date and time.",
                "Scheme" => "URL scheme (http, https, ws, wss). Empty for CONNECT tunnels.",
                "Content-Type" => "Response media type, without charset and other parameters.",
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
            // Hidden columns are persisted too, so width and position survive hide/show and restarts.
            // Whether a column is shown is stored separately (ColumnVisibility).
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
