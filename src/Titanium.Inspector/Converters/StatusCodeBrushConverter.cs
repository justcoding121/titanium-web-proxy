using System.Globalization;
using Avalonia;
using Avalonia.Data.Converters;
using Avalonia.Media;
using Titanium.Inspector.Services;

namespace Titanium.Inspector.Converters;

/// <summary>Maps HTTP status codes to theme-aware session status brushes.</summary>
public sealed class StatusCodeBrushConverter : IValueConverter
{
    public static readonly StatusCodeBrushConverter Instance = new();

    private static readonly IBrush FallbackMuted = new SolidColorBrush(Color.Parse("#888888"));
    private static readonly IBrush FallbackSuccess = new SolidColorBrush(Color.Parse("#0F7B0F"));
    private static readonly IBrush FallbackRedirect = new SolidColorBrush(Color.Parse("#0078D4"));
    private static readonly IBrush FallbackClientError = new SolidColorBrush(Color.Parse("#C19C00"));
    private static readonly IBrush FallbackServerError = new SolidColorBrush(Color.Parse("#C42B1C"));

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var status = value as int? ?? (value is int i ? i : null);
        var statusClass = SessionDisplayFormat.GetStatusClass(status);

        return statusClass switch
        {
            HttpStatusClass.Success => ResolveBrush("SessionStatusSuccessBrush", FallbackSuccess),
            HttpStatusClass.Redirection => ResolveBrush("SessionStatusRedirectBrush", FallbackRedirect),
            HttpStatusClass.ClientError => ResolveBrush("SessionStatusClientErrorBrush", FallbackClientError),
            HttpStatusClass.ServerError => ResolveBrush("SessionStatusServerErrorBrush", FallbackServerError),
            HttpStatusClass.Pending or HttpStatusClass.Informational or HttpStatusClass.Other
                => ResolveBrush("SessionStatusMutedBrush", FallbackMuted),
            _ => ResolveBrush("SessionStatusMutedBrush", FallbackMuted),
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
