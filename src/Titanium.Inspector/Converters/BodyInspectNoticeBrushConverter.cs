using System.Globalization;
using Avalonia;
using Avalonia.Data.Converters;
using Avalonia.Media;
using Titanium.Inspector.Services;

namespace Titanium.Inspector.Converters;

/// <summary>Maps <see cref="BodyInspectNoticeSeverity"/> to theme-aware Inspect notice foreground brushes.</summary>
public sealed class BodyInspectNoticeBrushConverter : IValueConverter
{
    public static readonly BodyInspectNoticeBrushConverter Instance = new();

    private static readonly IBrush FallbackWarning = new SolidColorBrush(Color.Parse("#7A5200"));
    private static readonly IBrush FallbackError = new SolidColorBrush(Color.Parse("#A4262C"));

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var severity = value is BodyInspectNoticeSeverity s ? s : BodyInspectNoticeSeverity.None;
        return severity switch
        {
            BodyInspectNoticeSeverity.Warning =>
                ResolveBrush("StatusFeedbackWarningBrush", FallbackWarning),
            BodyInspectNoticeSeverity.Error =>
                ResolveBrush("InspectorErrorTextBrush", FallbackError),
            _ => null,
        };
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();

    private static IBrush ResolveBrush(string resourceKey, IBrush fallback)
    {
        if (Application.Current?.TryGetResource(resourceKey, Application.Current.ActualThemeVariant, out var resource) == true
            && resource is IBrush brush)
        {
            return brush;
        }

        return fallback;
    }
}
