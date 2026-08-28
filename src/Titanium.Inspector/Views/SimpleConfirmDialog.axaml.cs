using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Titanium.Inspector.Views;

public partial class SimpleConfirmDialog : Window
{
    private bool _accepted;

    public SimpleConfirmDialog()
    {
        InitializeComponent();
        AcceptButton.Click += OnAccept;
        CancelButton.Click += OnCancel;
    }

    public static async Task<bool> ShowAsync(
        Window? owner,
        string title,
        string message,
        string accept,
        string cancel)
    {
        var dialog = new SimpleConfirmDialog
        {
            Title = title,
        };
        dialog.MessageText.Text = message;
        dialog.AcceptButton.Content = accept;
        dialog.CancelButton.Content = cancel;

        if (owner is null)
        {
            // Headless / no owner — treat as cancel (production always has a main window).
            return false;
        }

        await dialog.ShowDialog(owner);
        return dialog._accepted;
    }

    private void OnAccept(object? sender, RoutedEventArgs e)
    {
        _accepted = true;
        Close();
    }

    private void OnCancel(object? sender, RoutedEventArgs e)
    {
        _accepted = false;
        Close();
    }
}
