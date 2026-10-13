using System.Globalization;
using Avalonia;
using Avalonia.Data.Converters;
using Avalonia.Media;
using Microsoft.Extensions.Logging;

namespace Titanium.Inspector.Converters;

/// <summary>Maps a log level to the themed Recent-logs level brush.</summary>
public sealed class RunLogLevelBrushConverter : IValueConverter
{
    public static readonly RunLogLevelBrushConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not LogLevel level)
        {
            return AvaloniaProperty.UnsetValue;
        }

        var key = level switch
        {
            LogLevel.Error or LogLevel.Critical => "InspectorErrorTextBrush",
            LogLevel.Warning => "InspectorLogWarnBrush",
            LogLevel.Debug or LogLevel.Trace => "InspectorLogMutedBrush",
            _ => null,
        };

        if (key is null)
        {
            return AvaloniaProperty.UnsetValue;
        }

        if (Application.Current?.TryGetResource(key, Application.Current.ActualThemeVariant, out var brush) == true
            && brush is IBrush themed)
        {
            return themed;
        }

        return AvaloniaProperty.UnsetValue;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>Bold for Critical; normal otherwise.</summary>
public sealed class RunLogLevelFontWeightConverter : IValueConverter
{
    public static readonly RunLogLevelFontWeightConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is LogLevel.Critical ? FontWeight.Bold : FontWeight.Normal;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
