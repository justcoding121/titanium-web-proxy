using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Titanium.Inspector.Services;

namespace Titanium.Inspector.Views;

/// <summary>Paints one cached <see cref="TimelineLayout.Chart"/>. Hit testing stays in the layout helper.</summary>
public sealed class TimelineChart : Control
{
    private static readonly IBrush SelectedBand = new SolidColorBrush(Color.Parse("#333B82F6"));
    private static readonly IBrush HoverBand = new SolidColorBrush(Color.Parse("#1A3B82F6"));

    private readonly IBrush _label = new SolidColorBrush(Color.Parse("#FF6B7280"));
    private readonly IBrush _plain = new SolidColorBrush(Color.Parse("#FF64748B"));
    private readonly IBrush _hatch = new SolidColorBrush(Color.Parse("#FF94A3B8"));
    private readonly Pen _hatchPen;
    private readonly IBrush[] _phases;
    private IReadOnlySet<long> _selectedIds = new HashSet<long>();
    private long? _hoveredId;

    public TimelineChart()
    {
        _hatchPen = new Pen(_hatch, 1);
        _phases =
        [
            Brush("#FF94A3B8"),
            Brush("#FF3B82F6"),
            Brush("#FFF59E0B"),
            Brush("#FF8B5CF6"),
            Brush("#FF22C55E"),
            Brush("#FFEF4444"),
            Brush("#FF14B8A6"),
        ];
        PointerMoved += OnPointerMoved;
        PointerExited += OnPointerExited;
    }

    protected override void OnAttachedToVisualTree(Avalonia.VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Cursor = new Cursor(StandardCursorType.Hand);
    }

    public TimelineLayout.Chart? Model { get; set; }

    public IReadOnlySet<long> SelectedIds
    {
        get => _selectedIds;
        set
        {
            _selectedIds = value ?? new HashSet<long>();
            InvalidateVisual();
        }
    }

    public long? HoveredId
    {
        get => _hoveredId;
        set
        {
            if (_hoveredId == value)
            {
                return;
            }

            _hoveredId = value;
            InvalidateVisual();
        }
    }

    public override void Render(DrawingContext context)
    {
        var model = Model;
        if (model is null)
        {
            return;
        }

        foreach (var row in model.Rows)
        {
            var selected = _selectedIds.Contains(row.Id);
            var hovered = _hoveredId == row.Id;
            if (selected)
            {
                context.FillRectangle(SelectedBand, new Rect(0, row.Y, Bounds.Width, TimelineLayout.RowHeight));
            }
            else if (hovered)
            {
                context.FillRectangle(HoverBand, new Rect(0, row.Y, Bounds.Width, TimelineLayout.RowHeight));
            }

            var typeface = selected
                ? new Typeface(Typeface.Default.FontFamily, FontStyle.Normal, FontWeight.Bold)
                : Typeface.Default;
            var label = new FormattedText(
                row.Label,
                CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight,
                typeface,
                11,
                _label);
            using (context.PushClip(new Rect(0, row.Y, TimelineLayout.LabelWidth - 4, TimelineLayout.RowHeight)))
            {
                context.DrawText(label, new Point(0, row.Y + 1));
            }

            foreach (var segment in row.Segments)
            {
                if (segment.Width <= 0)
                {
                    continue;
                }

                var rect = new Rect(segment.X, row.Y, segment.Width, TimelineLayout.RowHeight);
                if (segment.Style == TimelineLayout.SegmentStyle.Hatched)
                {
                    DrawHatched(context, rect);
                    continue;
                }

                var brush = segment.Phase is SessionStatistics.Phase phase
                    ? _phases[(int)phase % _phases.Length]
                    : _plain;
                context.FillRectangle(brush, rect);
            }
        }
    }

    private void OnPointerMoved(object? sender, PointerEventArgs e)
    {
        var model = Model;
        if (model is null)
        {
            HoveredId = null;
            return;
        }

        var point = e.GetPosition(this);
        HoveredId = TimelineLayout.HitTest(model, point.X, point.Y);
    }

    private void OnPointerExited(object? sender, PointerEventArgs e) => HoveredId = null;

    private void DrawHatched(DrawingContext context, Rect rect)
    {
        using (context.PushClip(rect))
        {
            context.FillRectangle(_plain, rect);
            for (var x = rect.X - rect.Height; x < rect.Right; x += 4)
            {
                context.DrawLine(
                    _hatchPen,
                    new Point(x, rect.Bottom),
                    new Point(x + rect.Height, rect.Top));
            }
        }
    }

    private static SolidColorBrush Brush(string hex) => new(Color.Parse(hex));
}
