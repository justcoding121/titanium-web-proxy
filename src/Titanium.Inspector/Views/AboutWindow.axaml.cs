using Avalonia.Controls;
using Avalonia.Input;
using Titanium.Inspector.Services;

namespace Titanium.Inspector.Views;

public partial class AboutWindow : Window
{
    private const string LicenseUrl = "https://polyformproject.org/licenses/noncommercial/1.0.0";
    private const string WebsiteUrl = "https://titaniumproxy.com";

    public AboutWindow()
    {
        InitializeComponent();
        var ver = UpdateService.AssemblyVersion();
        VersionText.Text = $"Version {ver.Major}.{ver.Minor}.{ver.Build}";
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
