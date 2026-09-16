using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Threading;

namespace Titanium.Inspector.Views;

/// <summary>
/// Avalonia 11.2 ToggleButton/MenuItem click uses <c>SetValue</c> on <c>IsChecked</c>, which
/// severs a OneWay binding. After that, ViewModel <c>PropertyChanged</c> never moves the glyph.
/// <see cref="ToggleButton.SetCurrentValue"/> keeps (or restores) the visual without replacing the binding.
/// Fixed upstream in Avalonia 12 / #17674 — remove when Inspector upgrades past that.
/// </summary>
internal static class OneWayToggleVisualSync
{
    public static void Apply(CheckBox? box, bool isChecked)
    {
        if (box is null)
            return;

        void Set() => box.SetCurrentValue(ToggleButton.IsCheckedProperty, isChecked);
        if (Dispatcher.UIThread.CheckAccess())
            Set();
        else
            Dispatcher.UIThread.Post(Set, DispatcherPriority.Input);
    }

    public static void Apply(MenuItem? item, bool isChecked)
    {
        if (item is null)
            return;

        void Set() => item.SetCurrentValue(MenuItem.IsCheckedProperty, isChecked);
        if (Dispatcher.UIThread.CheckAccess())
            Set();
        else
            Dispatcher.UIThread.Post(Set, DispatcherPriority.Input);
    }
}
