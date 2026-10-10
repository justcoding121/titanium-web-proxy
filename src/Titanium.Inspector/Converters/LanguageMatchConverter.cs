using System.Globalization;
using Avalonia.Data;
using Avalonia.Data.Converters;

namespace Titanium.Inspector.Converters;

/// <summary>Radio check for a language setting value (<c>auto</c> or a culture name).</summary>
public sealed class LanguageMatchConverter : IValueConverter
{
    public static LanguageMatchConverter Instance { get; } = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        string.Equals(value?.ToString(), parameter?.ToString(), StringComparison.OrdinalIgnoreCase);

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        BindingOperations.DoNothing;
}
