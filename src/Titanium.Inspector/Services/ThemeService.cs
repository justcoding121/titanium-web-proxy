using Avalonia;
using Avalonia.Styling;

namespace Titanium.Inspector.Services;

public static class ThemeService
{
    public static void ApplyThemeMode(ThemeMode mode)
    {
        if (Application.Current is null)
        {
            return;
        }

        Application.Current.RequestedThemeVariant = mode switch
        {
            ThemeMode.Light => ThemeVariant.Light,
            ThemeMode.Dark => ThemeVariant.Dark,
            _ => ThemeVariant.Default,
        };
    }
}
