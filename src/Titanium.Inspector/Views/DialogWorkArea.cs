using Avalonia.Controls;

namespace Titanium.Inspector.Views;

/// <summary>
/// Keeps a dialog inside the work area. A fixed or size-to-content height that is taller
/// than the screen clips the form; capping the height lets the body scrollbar appear.
/// </summary>
internal static class DialogWorkArea
{
    private const double ScreenMargin = 48;
    private static bool _applying;
    private static readonly HashSet<Window> HeightLocked = new();

    public static void Attach(Window window)
    {
        window.Closed += (_, _) => HeightLocked.Remove(window);
        window.Opened += (_, _) =>
        {
            Apply(window);
            if (window.SizeToContent != SizeToContent.Manual && !HeightLocked.Contains(window))
            {
                void OnLayoutUpdated(object? sender, EventArgs e)
                {
                    if (HeightLocked.Contains(window))
                    {
                        window.LayoutUpdated -= OnLayoutUpdated;
                        return;
                    }

                    var height = window.Bounds.Height;
                    if (height <= 0 || double.IsNaN(height))
                    {
                        return;
                    }

                    LockHeightAfterSizeToContent(window, height);
                    window.LayoutUpdated -= OnLayoutUpdated;
                    Apply(window);
                }

                window.LayoutUpdated += OnLayoutUpdated;
            }
        };
        window.PositionChanged += (_, _) => Apply(window);
    }

    internal static void Apply(Window window)
    {
        if (_applying)
        {
            return;
        }

        var screen = window.Screens?.ScreenFromWindow(window) ?? window.Screens?.Primary;
        if (screen is null || screen.Scaling <= 0)
        {
            return;
        }

        _applying = true;
        try
        {
            var maxHeight = Math.Floor(screen.WorkingArea.Height / screen.Scaling) - ScreenMargin;
            if (maxHeight >= 160)
            {
                window.MaxHeight = maxHeight;
                if (window.SizeToContent == SizeToContent.Manual
                    && !double.IsNaN(window.Height)
                    && window.Height > maxHeight)
                {
                    window.Height = maxHeight;
                }
            }

            var maxWidth = Math.Floor(screen.WorkingArea.Width / screen.Scaling) - ScreenMargin;
            if (maxWidth >= 280)
            {
                window.MaxWidth = maxWidth;
                if (!double.IsNaN(window.Width) && window.Width > maxWidth)
                {
                    window.Width = maxWidth;
                }
            }
        }
        finally
        {
            _applying = false;
        }
    }

    private static void LockHeightAfterSizeToContent(Window window, double height)
    {
        _applying = true;
        try
        {
            if (window.MaxHeight is > 0 and var max && height > max)
            {
                height = max;
            }

            height = Math.Max(height, window.MinHeight);
            var sizeToContent = window.SizeToContent;
            switch (sizeToContent)
            {
                case SizeToContent.Height:
                    window.SizeToContent = SizeToContent.Manual;
                    window.Height = height;
                    HeightLocked.Add(window);
                    break;
                case SizeToContent.WidthAndHeight:
                    window.SizeToContent = SizeToContent.Width;
                    window.Height = height;
                    HeightLocked.Add(window);
                    break;
            }
        }
        finally
        {
            _applying = false;
        }
    }
}
