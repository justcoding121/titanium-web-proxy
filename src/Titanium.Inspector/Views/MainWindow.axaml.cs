using System.Collections.Specialized;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Titanium.Inspector.Services;
using Titanium.Inspector.ViewModels;

namespace Titanium.Inspector.Views;

public partial class MainWindow : Window
{
    private bool _autoStartStarted;
    private bool _followLatest = true;
    private bool _programmaticScroll;
    private bool _scrollQueued;
    private ScrollViewer? _sessionsScroll;
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
        if (_sessionsScroll is not null)
        {
            _sessionsScroll.ScrollChanged -= OnSessionsScrollChanged;
            _sessionsScroll = null;
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
        if (_sessionsScroll is not null)
        {
            return;
        }

        _sessionsScroll = SessionsGrid.FindDescendantOfType<ScrollViewer>();
        if (_sessionsScroll is null)
        {
            return;
        }

        _sessionsScroll.ScrollChanged += OnSessionsScrollChanged;
    }

    private void OnSessionsScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        if (_sessionsScroll is null)
        {
            return;
        }

        var offset = _sessionsScroll.Offset.Y;
        var viewport = _sessionsScroll.Viewport.Height;
        var extent = _sessionsScroll.Extent.Height;
        var userMovedOffset = Math.Abs(e.OffsetDelta.Y) > 0.5;
        var isNearBottom = SessionListFollowLatest.IsNearBottom(
            offset, viewport, extent, SessionListFollowLatest.DefaultThresholdPx);
        var allContentVisible = extent <= viewport;

        _followLatest = SessionListFollowLatest.UpdateFollowAfterScroll(
            _followLatest, _programmaticScroll, userMovedOffset, isNearBottom, allContentVisible);

        if (!_programmaticScroll
            && e.ExtentDelta.Y > 0.5
            && SessionListFollowLatest.ShouldScrollToLatest(_followLatest, unsorted: true, hasItems: true))
        {
            RequestScrollToLatest();
        }
    }

    private void OnSessionsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action is not (NotifyCollectionChangedAction.Add or NotifyCollectionChangedAction.Reset))
        {
            return;
        }

        if (SessionListFollowLatest.ShouldScrollToLatest(
                _followLatest, unsorted: true, _sessionsVm is { Sessions.Count: > 0 }))
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
                _followLatest, unsorted: true, _sessionsVm.Sessions.Count > 0))
        {
            return;
        }

        AttachSessionsScroll();
        // Avalonia DataGrid does not expose column sort state publicly; last collection
        // item is newest (capture order). ScrollIntoView realizes the virtualized row,
        // then ScrollToEnd pins the viewport to the visual bottom.
        _programmaticScroll = true;
        SessionsGrid.ScrollIntoView(_sessionsVm.Sessions[^1], column: null);
        _sessionsScroll?.ScrollToEnd();
        Dispatcher.UIThread.Post(() => _programmaticScroll = false, DispatcherPriority.Background);
    }
}
