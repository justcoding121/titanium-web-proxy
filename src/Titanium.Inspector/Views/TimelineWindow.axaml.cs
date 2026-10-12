using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using Titanium.Inspector.Localization;
using Titanium.Inspector.Services;
using Titanium.Inspector.ViewModels;

namespace Titanium.Inspector.Views;

public partial class TimelineWindow : Window
{
    private static TimelineWindow? _open;

    private readonly TimelineViewModel _model;
    private readonly Func<IReadOnlyCollection<long>>? _selectedIds;
    private readonly DispatcherTimer _timer;
    private HashSet<long> _lastSelected = new();

    public TimelineWindow() : this(new SessionStore(new SessionStoreOptions { SpillBodiesToDisk = false }), _ => { }, null)
    {
    }

    private TimelineWindow(
        SessionStore store,
        Action<long> select,
        Func<IReadOnlyCollection<long>>? selectedIds)
    {
        InitializeComponent();
        LanguageService.AttachWindow(this);
        _selectedIds = selectedIds;
        _model = new TimelineViewModel(store, select);
        Legend.Text = string.Join(
            " · ",
            Enum.GetValues<SessionStatistics.Phase>().Select(phase => LanguageService.Get(SessionStatistics.PhaseKey(phase))))
            + " · " + LanguageService.Get("timeline.tunnel")
            + " · " + LanguageService.Get("timeline.estimated")
            + " · " + LanguageService.Get("timeline.inFlight");
        _model.Open(DateTimeOffset.UtcNow, ChartWidth());
        ApplyModel();
        SyncSelection(force: true);
        Chart.PointerPressed += OnChartPressed;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
        _timer.Tick += (_, _) =>
        {
            var rebuilt = _model.Tick(DateTimeOffset.UtcNow, ChartWidth());
            if (rebuilt)
            {
                ApplyModel();
            }

            SyncSelection(force: rebuilt);
        };
        _timer.Start();
        PropertyChanged += (_, e) =>
        {
            if (e.Property == IsVisibleProperty)
            {
                _model.SetVisible(IsVisible, DateTimeOffset.UtcNow, ChartWidth());
                ApplyModel();
                SyncSelection(force: true);
            }
        };
        Closed += (_, _) =>
        {
            _timer.Stop();
            Chart.PointerPressed -= OnChartPressed;
            _model.Close();
            ClearOpenIf(this);
        };
    }

    public static void Show(
        Window owner,
        SessionStore store,
        Action<long> select,
        Func<IReadOnlyCollection<long>>? selectedIds = null)
    {
        if (_open is { } existing)
        {
            existing.Activate();
            return;
        }

        var window = new TimelineWindow(store, select, selectedIds);
        RegisterOpen(window);
        window.Show(owner);
    }

    private static void RegisterOpen(TimelineWindow window) => _open = window;

    private static void ClearOpenIf(TimelineWindow window)
    {
        if (ReferenceEquals(_open, window))
        {
            _open = null;
        }
    }

    private void OnChartPressed(object? sender, PointerPressedEventArgs e)
    {
        var point = e.GetPosition(Chart);
        _model.SelectAt(point.X, point.Y);
        Detail.Text = _model.Detail;
        e.Handled = true;
    }

    private void ApplyModel()
    {
        Chart.Model = _model.Chart;
        Chart.Height = Math.Max(_model.Chart.Height, 1);
        Summary.Text = _model.Summary;
        Chart.InvalidateVisual();
    }

    private void SyncSelection(bool force)
    {
        if (_selectedIds is null)
        {
            return;
        }

        var next = new HashSet<long>(_selectedIds());
        if (!force && next.SetEquals(_lastSelected))
        {
            return;
        }

        _lastSelected = next;
        Chart.SelectedIds = next;
    }

    private double ChartWidth()
    {
        var width = Bounds.Width - 24;
        return width > 1 ? width : 800;
    }
}
