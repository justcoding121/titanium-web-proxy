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
    private readonly DispatcherTimer _timer;

    public TimelineWindow() : this(new SessionStore(new SessionStoreOptions { SpillBodiesToDisk = false }), _ => { })
    {
    }

    private TimelineWindow(SessionStore store, Action<long> select)
    {
        InitializeComponent();
        LanguageService.AttachWindow(this);
        _model = new TimelineViewModel(store, select);
        Legend.Text = string.Join(
            " · ",
            Enum.GetValues<SessionStatistics.Phase>().Select(phase => LanguageService.Get(SessionStatistics.PhaseKey(phase))))
            + " · " + LanguageService.Get("timeline.tunnel")
            + " · " + LanguageService.Get("timeline.estimated")
            + " · " + LanguageService.Get("timeline.inFlight");
        _model.Open(DateTimeOffset.UtcNow, ChartWidth());
        ApplyModel();
        Chart.PointerPressed += OnChartPressed;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
        _timer.Tick += (_, _) =>
        {
            if (_model.Tick(DateTimeOffset.UtcNow, ChartWidth()))
            {
                ApplyModel();
            }
        };
        _timer.Start();
        PropertyChanged += (_, e) =>
        {
            if (e.Property == IsVisibleProperty)
            {
                _model.SetVisible(IsVisible, DateTimeOffset.UtcNow, ChartWidth());
                ApplyModel();
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

    public static void Show(Window owner, SessionStore store, Action<long> select)
    {
        if (_open is { } existing)
        {
            existing.Activate();
            return;
        }

        var window = new TimelineWindow(store, select);
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

    private double ChartWidth()
    {
        var width = Bounds.Width - 24;
        return width > 1 ? width : 800;
    }
}
