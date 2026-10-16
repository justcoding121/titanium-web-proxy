using Avalonia.Controls;
using Avalonia.LogicalTree;

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
        AttachExpanderScroll(window);
    }

    /// <summary>
    /// Opening an expander grows the form below the fold. Scroll that section into the dialog body.
    /// </summary>
    private static void AttachExpanderScroll(Window window)
    {
        foreach (var expander in window.GetLogicalDescendants().OfType<Expander>())
        {
            expander.PropertyChanged += (_, e) =>
            {
                if (e.Property != Expander.IsExpandedProperty || !expander.IsExpanded)
                {
                    return;
                }

                ScrollExpandedIntoView(expander);
            };
        }
    }

    private static void ScrollExpandedIntoView(Expander expander)
    {
        var attempts = 0;
        void OnLayout(object? sender, EventArgs e)
        {
            attempts++;
            if (expander.Content is not Control content || content.Bounds.Height <= 0)
            {
                if (attempts > 8)
                {
                    expander.LayoutUpdated -= OnLayout;
                }

                return;
            }

            expander.LayoutUpdated -= OnLayout;
            content.BringIntoView();
        }

        expander.LayoutUpdated += OnLayout;
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
