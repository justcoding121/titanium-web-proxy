using System.Globalization;
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
        return statusClass is HttpStatusClass.ClientError or HttpStatusClass.ServerError
            ? FontWeight.SemiBold
            : FontWeight.Normal;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
