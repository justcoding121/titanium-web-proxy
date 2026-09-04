using Avalonia;
using Avalonia.Controls;

namespace Titanium.Inspector.Views;

/// <summary>
/// Mirrors the in-window <see cref="Menu"/> into a macOS <see cref="NativeMenu"/> and hides the
/// Avalonia menu strip. In-window Menu/ContextMenu popups are unreliable on macOS when the app is
/// started from a fullscreen host (Cursor/Rider) — see AvaloniaUI/Avalonia#15178. Native menus and
/// <c>OverlayPopups</c> avoid that failure mode.
/// </summary>
internal static class MacOsNativeMenu
{
    public static void AttachIfMac(Window window, Menu menu)
    {
        if (!OperatingSystem.IsMacOS())
        {
            return;
        }

        try
        {
            var native = ConvertItems(menu.Items);
            NativeMenu.SetMenu(window, native);
            menu.IsVisible = false;
        }
        catch
        {
            // Headless / non-native hosts: keep the in-window Menu for AutomationId tests.
            menu.IsVisible = true;
        }
    }

    private static NativeMenu ConvertItems(System.Collections.IEnumerable items)
    {
        var menu = new NativeMenu();
        foreach (var item in items)
        {
            switch (item)
            {
                case Separator:
                    menu.Items.Add(new NativeMenuItemSeparator());
                    break;
                case MenuItem menuItem:
                    menu.Items.Add(ConvertMenuItem(menuItem));
                    break;
            }
        }

        return menu;
    }

    private static NativeMenuItem ConvertMenuItem(MenuItem source)
    {
        var native = new NativeMenuItem
        {
            Header = StripMnemonic(source.Header?.ToString()),
            Command = source.Command,
            CommandParameter = source.CommandParameter,
            IsEnabled = source.IsEnabled,
            IsVisible = source.IsVisible,
            ToggleType = MapToggleType(source.ToggleType),
            IsChecked = source.IsChecked,
        };

        // Keep checkmarks in sync when OneWay bindings / commands flip IsChecked.
        source.PropertyChanged += (_, e) =>
        {
            if (e.Property == MenuItem.IsCheckedProperty)
            {
                native.IsChecked = source.IsChecked;
            }
            else if (e.Property == MenuItem.IsEnabledProperty)
            {
                native.IsEnabled = source.IsEnabled;
            }
            else if (e.Property == MenuItem.IsVisibleProperty)
            {
                native.IsVisible = source.IsVisible;
            }
            else if (e.Property == MenuItem.HeaderProperty)
            {
                native.Header = StripMnemonic(source.Header?.ToString());
            }
            else if (e.Property == MenuItem.CommandProperty)
            {
                native.Command = source.Command;
            }
        };

        if (source.Items.Count > 0)
        {
            native.Menu = ConvertItems(source.Items);
        }

        return native;
    }

    /// <summary>Avalonia uses '_' for access keys; AppKit menus should not show the underscore.</summary>
    private static string? StripMnemonic(string? header)
    {
        if (string.IsNullOrEmpty(header))
        {
            return header;
        }

        return header.Replace("_", "", StringComparison.Ordinal);
    }

    private static NativeMenuItemToggleType MapToggleType(MenuItemToggleType toggle) => toggle switch
    {
        MenuItemToggleType.CheckBox => NativeMenuItemToggleType.CheckBox,
        MenuItemToggleType.Radio => NativeMenuItemToggleType.Radio,
        _ => NativeMenuItemToggleType.None,
    };
}
