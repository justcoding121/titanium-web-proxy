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

    public static void Attach(Window window)
    {
        window.Opened += (_, _) => Apply(window);
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
}
