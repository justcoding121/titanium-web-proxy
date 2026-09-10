using Avalonia.Controls;
using Avalonia.Input;
using Titanium.Inspector.Services;

namespace Titanium.Inspector.Views;

public partial class AboutWindow : Window
{
    private static readonly string LicenseUrl =
        string.Concat("https://", "polyformproject.org", "/licenses/noncommercial/1.0.0");
    private static readonly string WebsiteUrl =
        string.Concat("https://", "titaniumproxy.com");

    public AboutWindow()
    {
        InitializeComponent();
        VersionText.Text = $"Version {UpdateService.FormatAssemblyDisplayVersion()}";
        OkButton.Click += (_, _) => Close();
        LicenseLink.PointerPressed += (_, e) => OnLinkPressed(e, LicenseUrl);
        WebsiteLink.PointerPressed += (_, e) => OnLinkPressed(e, WebsiteUrl);
    }

    public static async Task ShowAsync(Window owner)
    {
        var w = new AboutWindow();
        await w.ShowDialog(owner);
    }

    private async void OnLinkPressed(PointerPressedEventArgs e, string url)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }

        e.Handled = true;
        var launcher = TopLevel.GetTopLevel(this)?.Launcher;
        if (launcher is not null)
        {
            await launcher.LaunchUriAsync(new Uri(url));
        }
    }
}
