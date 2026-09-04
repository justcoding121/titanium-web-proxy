using System.Globalization;
using Avalonia;
using Avalonia.Data.Converters;
using Avalonia.Media;
using Titanium.Inspector.Services;

namespace Titanium.Inspector.Converters;

/// <summary>Maps <see cref="StatusSeverity"/> to theme-aware status bar foreground brushes.</summary>
public sealed class StatusSeverityBrushConverter : IValueConverter
{
    public static readonly StatusSeverityBrushConverter Instance = new();

    private static readonly IBrush FallbackNeutral = new SolidColorBrush(Color.Parse("#6B6B6B"));
    private static readonly IBrush FallbackBusy = new SolidColorBrush(Color.Parse("#0078D4"));
    private static readonly IBrush FallbackSuccess = new SolidColorBrush(Color.Parse("#0A5F0A"));
    private static readonly IBrush FallbackWarning = new SolidColorBrush(Color.Parse("#9A6700"));
    private static readonly IBrush FallbackError = new SolidColorBrush(Color.Parse("#C42B1C"));

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var severity = value is StatusSeverity s ? s : StatusSeverity.Neutral;
        return severity switch
        {
            StatusSeverity.Busy => ResolveBrush("StatusFeedbackBusyBrush", FallbackBusy),
            StatusSeverity.Success => ResolveBrush("StatusFeedbackSuccessBrush", FallbackSuccess),
            StatusSeverity.Warning => ResolveBrush("StatusFeedbackWarningBrush", FallbackWarning),
            StatusSeverity.Error => ResolveBrush("StatusFeedbackErrorBrush", FallbackError),
            _ => ResolveBrush("StatusFeedbackNeutralBrush", FallbackNeutral),
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
