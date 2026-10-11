using Titanium.Inspector.Localization;
using Avalonia.Controls;
using Avalonia.Input;
using Titanium.Inspector.Services;

namespace Titanium.Inspector.Views;

public partial class AboutWindow : Window
{
    public AboutWindow()
    {
        InitializeComponent();
        LanguageService.AttachWindow(this);
        VersionText.Text = $"Version {UpdateService.FormatAssemblyDisplayVersion()}";
        OkButton.Click += (_, _) => Close();
        LicenseLink.PointerPressed += (_, e) => OnLinkPressed(e, InspectorLinks.License);
        WebsiteLink.PointerPressed += (_, e) => OnLinkPressed(e, InspectorLinks.Website);
    }

    public static async Task ShowAsync(Window owner)
    {
        var w = new AboutWindow();
        await w.ShowDialog(owner);
    }

    private async void OnLinkPressed(PointerPressedEventArgs e, Uri url)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }

        e.Handled = true;
        var launcher = TopLevel.GetTopLevel(this)?.Launcher;
        if (launcher is not null)
        {
            await launcher.LaunchUriAsync(url);
        }
    }
}
