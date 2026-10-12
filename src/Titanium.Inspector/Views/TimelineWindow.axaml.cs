using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
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
        DialogWorkArea.Attach(this);
        _selectedIds = selectedIds;
        _model = new TimelineViewModel(store, select);
        BuildLegend();
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
        Detail.Text = _model.Detail;
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
        ScrollSelectionIntoView(next);
    }

    private void ScrollSelectionIntoView(HashSet<long> ids)
    {
        if (ids.Count == 0)
        {
            return;
        }

        foreach (var row in _model.Chart.Rows)
        {
            if (!ids.Contains(row.Id))
            {
                continue;
            }

            var viewport = ChartScroll.Viewport.Height;
            var top = Math.Max(0, row.Y - Math.Max(0, (viewport - TimelineLayout.RowHeight) / 2));
            ChartScroll.Offset = new Vector(ChartScroll.Offset.X, top);
            return;
        }
    }

    private void BuildLegend()
    {
        foreach (var phase in Enum.GetValues<SessionStatistics.Phase>())
        {
            AddLegendItem(
                TimelineChart.PhaseBrushes[(int)phase],
                hatched: false,
                LanguageService.Get(SessionStatistics.PhaseKey(phase)));
        }

        AddLegendItem(TimelineChart.PlainBrush, hatched: false, LanguageService.Get("timeline.tunnel"));
        AddLegendItem(TimelineChart.PlainBrush, hatched: true, LanguageService.Get("timeline.estimated"));
        AddLegendItem(TimelineChart.PlainBrush, hatched: false, LanguageService.Get("timeline.inFlight"));
    }

    private void AddLegendItem(IBrush fill, bool hatched, string label)
    {
        Legend.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            Margin = new Thickness(0, 0, 14, 4),
            Children =
            {
                new LegendSwatch
                {
                    Width = 12,
                    Height = 12,
                    VerticalAlignment = VerticalAlignment.Center,
                    Fill = fill,
                    Hatched = hatched,
                },
                new TextBlock
                {
                    Text = label,
                    VerticalAlignment = VerticalAlignment.Center,
                    Opacity = 0.85,
                },
            },
        });
    }

    private double ChartWidth()
    {
        var width = Bounds.Width - 24;
        return width > 1 ? width : 800;
    }

    private sealed class LegendSwatch : Control
    {
        private static readonly Pen HatchPen = new(TimelineChart.HatchBrush, 1);

        public IBrush Fill { get; set; } = TimelineChart.PlainBrush;

        public bool Hatched { get; set; }

        public override void Render(DrawingContext context)
        {
            var rect = new Rect(Bounds.Size);
            if (rect.Width <= 0 || rect.Height <= 0)
            {
                return;
            }

            if (!Hatched)
            {
                context.FillRectangle(Fill, rect);
                return;
            }

            using (context.PushClip(rect))
            {
                context.FillRectangle(Fill, rect);
                for (var x = rect.X - rect.Height; x < rect.Right; x += 3)
                {
                    context.DrawLine(HatchPen, new Point(x, rect.Bottom), new Point(x + rect.Height, rect.Top));
                }
            }
        }
    }
}
