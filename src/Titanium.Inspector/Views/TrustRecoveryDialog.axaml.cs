using Avalonia.Controls;
using Avalonia.Interactivity;
using Titanium.Inspector.Services;

namespace Titanium.Inspector.Views;

public partial class TrustRecoveryDialog : Window
{
    private TrustRecoveryChoice _choice = TrustRecoveryChoice.Cancel;

    public TrustRecoveryDialog()
    {
        InitializeComponent();
        PrimaryButton.Click += (_, _) =>
        {
            _choice = TrustRecoveryChoice.Primary;
            Close();
        };
        SecondaryButton.Click += (_, _) =>
        {
            _choice = TrustRecoveryChoice.Secondary;
            Close();
        };
        CancelButton.Click += (_, _) =>
        {
            _choice = TrustRecoveryChoice.Cancel;
            Close();
        };
    }

    public static async Task<TrustRecoveryChoice> ShowAsync(
        Window? owner,
        string title,
        string message,
        string primary,
        string? secondary,
        double height = 260)
    {
        if (owner is null)
            return TrustRecoveryChoice.Cancel;

        var dialog = new TrustRecoveryDialog
        {
            Title = title,
            Height = height,
        };
        dialog.MessageText.Text = message;
        dialog.PrimaryButton.Content = primary;
        if (string.IsNullOrWhiteSpace(secondary))
        {
            dialog.SecondaryButton.IsVisible = false;
        }
        else
        {
            dialog.SecondaryButton.Content = secondary;
            dialog.SecondaryButton.IsVisible = true;
        }

        await dialog.ShowDialog(owner);
        return dialog._choice;
    }
}
