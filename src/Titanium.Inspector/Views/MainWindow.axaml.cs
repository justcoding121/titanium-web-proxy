using System.Collections.Specialized;
using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
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
    private ScrollBar? _sessionsVScroll;
    private MainWindowViewModel? _sessionsVm;

    public MainWindow()
    {
        InitializeComponent();
        Closing += OnClosing;
        Opened += OnOpened;
        DataContextChanged += OnDataContextChanged;
        SessionsGrid.Loaded += (_, _) => AttachSessionsScroll();
        HookSessionsCollection(DataContext as MainWindowViewModel);
    }

    private async void OnOpened(object? sender, EventArgs e)
    {
        AttachSessionsScroll();

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
        HookSessionsCollection(null);
        if (_sessionsVScroll is not null)
        {
            _sessionsVScroll.PropertyChanged -= OnSessionsScrollBarPropertyChanged;
            _sessionsVScroll = null;
        }

        if (DataContext is MainWindowViewModel vm)
        {
            vm.EnsureShutdown();
        }
    }

    private void OnDataContextChanged(object? sender, EventArgs e) =>
        HookSessionsCollection(DataContext as MainWindowViewModel);

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
        var isNearBottom = SessionListFollowLatest.IsNearBottomByScrollBar(
            value, maximum, SessionListFollowLatest.DefaultThresholdPx);
        var allContentVisible = maximum <= 0;

        _followLatest = SessionListFollowLatest.UpdateFollowAfterScroll(
            _followLatest, _programmaticScroll, userMovedOffset, isNearBottom, allContentVisible);
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
                _followLatest, IsUnsorted(), _sessionsVm is { Sessions.Count: > 0 }))
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
        if (_sessionsVm is null
            || !SessionListFollowLatest.ShouldScrollToLatest(
                _followLatest, IsUnsorted(), _sessionsVm.Sessions.Count > 0))
        {
            return;
        }

        AttachSessionsScroll();
        // ScrollIntoView realizes the virtualized last row; pin the scrollbar to Maximum.
        _programmaticScroll = true;
        SessionsGrid.ScrollIntoView(_sessionsVm.Sessions[^1], column: null);
        if (_sessionsVScroll is not null)
        {
            _sessionsVScroll.Value = _sessionsVScroll.Maximum;
        }

        // Layout/virtualization may raise ValueChanged after this method returns.
        Dispatcher.UIThread.Post(() =>
        {
            Dispatcher.UIThread.Post(() => _programmaticScroll = false, DispatcherPriority.Background);
        }, DispatcherPriority.Background);
    }

    private bool IsUnsorted()
    {
        if (GetSortDescriptionMethod is null)
        {
            return true;
        }

        try
        {
            return !SessionsGrid.Columns.Any(column =>
                GetSortDescriptionMethod.Invoke(column, null) is not null);
        }
        catch
        {
            return true;
        }
    }
}
