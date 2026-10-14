using System.Globalization;
using Avalonia;
using Avalonia.Data.Converters;
using Avalonia.Media;
using Titanium.Inspector.Services;

namespace Titanium.Inspector.Converters;

/// <summary>SemiBold for 4xx/5xx status codes; Normal otherwise.</summary>
public sealed class StatusCodeFontWeightConverter : IValueConverter
{
    public static readonly StatusCodeFontWeightConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var status = value as int? ?? (value is int i ? i : null);
        var statusClass = SessionDisplayFormat.GetStatusClass(status);
        if (statusClass is HttpStatusClass.ClientError or HttpStatusClass.ServerError)
        {
            return FontWeight.SemiBold;
        }

        // Light draws dark strokes on white, which look thin at Normal. The theme dictionary
        // raises the base weight; Dark stays Normal for 2xx and 3xx.
        if (Application.Current?.TryGetResource(
                "SessionStatusBaseFontWeight",
                Application.Current.ActualThemeVariant,
                out var resource) == true
            && resource is string name
            && Enum.TryParse(name, ignoreCase: false, out FontWeight weight))
        {
            return weight;
        }

        return FontWeight.Normal;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
