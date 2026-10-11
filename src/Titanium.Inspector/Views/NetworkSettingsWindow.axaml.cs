using Avalonia.Controls;
using Avalonia.Interactivity;
using Titanium.Inspector.Localization;
using Titanium.Inspector.Services;

namespace Titanium.Inspector.Views;

public partial class NetworkSettingsWindow : Window
{
    private readonly SettingsService _settings;
    private readonly string _bindAddress;
    private readonly int _bindPort;
    private bool _saved;

    public NetworkSettingsWindow() : this(SettingsService.Load(), "127.0.0.1", 8866, captureRunning: false)
    {
    }

    public NetworkSettingsWindow(SettingsService settings, string bindAddress, int bindPort, bool captureRunning)
    {
        _settings = settings;
        _bindAddress = bindAddress;
        _bindPort = bindPort;
        InitializeComponent();
        LanguageService.AttachWindow(this);
        LoadFromSettings();
        RestartCheck.IsVisible = captureRunning;
        NoneRadio.IsCheckedChanged += (_, _) => UpdateFieldsEnabled();
        ProxyRadio.IsCheckedChanged += (_, _) => UpdateFieldsEnabled();
        SaveButton.Click += OnSave;
        CancelButton.Click += (_, _) => Close();
        ResetButton.Click += (_, _) =>
        {
            NoneRadio.IsChecked = true;
            HostBox.Text = string.Empty;
            PortBox.Text = InspectorNetworkSettings.DefaultUpstreamPort.ToString(
                System.Globalization.CultureInfo.InvariantCulture);
            StatusText.Text = string.Empty;
            UpdateFieldsEnabled();
        };
        UpdateFieldsEnabled();
    }

    public bool Saved => _saved;

    public bool RestartCapture => RestartCheck.IsChecked == true;

    public static async Task<NetworkSettingsWindow> ShowAsync(
        Window owner,
        SettingsService settings,
        string bindAddress,
        int bindPort,
        bool captureRunning)
    {
        var window = new NetworkSettingsWindow(settings, bindAddress, bindPort, captureRunning);
        await window.ShowDialog(owner);
        return window;
    }

    private void LoadFromSettings()
    {
        var s = _settings.Current;
        var enabled = s.UpstreamProxyEnabled;
        NoneRadio.IsChecked = !enabled;
        ProxyRadio.IsChecked = enabled;
        HostBox.Text = s.UpstreamProxyHost;
        PortBox.Text = s.UpstreamProxyPort.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    private void UpdateFieldsEnabled()
    {
        var on = ProxyRadio.IsChecked == true;
        HostBox.IsEnabled = on;
        PortBox.IsEnabled = on;
    }

    private void OnSave(object? sender, RoutedEventArgs e)
    {
        var enabled = ProxyRadio.IsChecked == true;
        if (!int.TryParse(PortBox.Text?.Trim(), System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out var port))
        {
            port = enabled ? 0 : InspectorNetworkSettings.DefaultUpstreamPort;
        }

        var error = InspectorNetworkSettings.Validate(enabled, HostBox.Text, port, _bindAddress, _bindPort);
        if (error is not null)
        {
            StatusText.Text = LanguageService.Get(error);
            return;
        }

        var s = _settings.Current;
        s.UpstreamProxyEnabled = enabled;
        s.UpstreamProxyHost = HostBox.Text?.Trim() ?? string.Empty;
        s.UpstreamProxyPort = enabled ? port : (port is >= 1 and <= 65535 ? port : InspectorNetworkSettings.DefaultUpstreamPort);
        _settings.Save();
        _saved = true;
        Close();
    }
}
