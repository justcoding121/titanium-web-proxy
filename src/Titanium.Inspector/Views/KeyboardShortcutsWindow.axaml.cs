using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Titanium.Inspector.Localization;
using Titanium.Inspector.Services;

namespace Titanium.Inspector.Views;

public partial class KeyboardShortcutsWindow : Window
{
    public KeyboardShortcutsWindow()
    {
        InitializeComponent();
        LanguageService.AttachWindow(this);
        DialogWorkArea.Attach(this);
        var first = true;
        foreach (var section in InspectorShortcuts.Sections(OperatingSystem.IsMacOS()))
        {
            RowsHost.Children.Add(new TextBlock
            {
                Text = LanguageService.Get(section.TitleKey),
                FontWeight = FontWeight.SemiBold,
                Margin = new Avalonia.Thickness(0, first ? 0 : 12, 0, 4),
            });
            first = false;
            foreach (var entry in section.Entries)
            {
                var row = new Grid
                {
                    ColumnDefinitions = new ColumnDefinitions("*,Auto"),
                    Margin = new Avalonia.Thickness(0, 2, 0, 2),
                };
                row.Children.Add(new TextBlock { Text = LanguageService.Get(entry.ActionKey) });
                var gesture = new TextBlock
                {
                    Text = entry.Gesture,
                    Opacity = 0.75,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Margin = new Avalonia.Thickness(16, 0, 0, 0),
                };
                Grid.SetColumn(gesture, 1);
                row.Children.Add(gesture);
                RowsHost.Children.Add(row);
            }
        }
    }

    public static async Task ShowAsync(Window owner)
    {
        var window = new KeyboardShortcutsWindow();
        await window.ShowDialog(owner);
    }
}
