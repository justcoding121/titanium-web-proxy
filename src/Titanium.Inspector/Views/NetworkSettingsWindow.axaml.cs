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
        Http2Check.IsCheckedChanged += (_, _) => UpdateSummary();
        Http3Check.IsCheckedChanged += (_, _) => UpdateSummary();
        TlsCombo.SelectionChanged += (_, _) => UpdateTlsWarning();
        SaveButton.Click += OnSave;
        CancelButton.Click += (_, _) => Close();
        ResetButton.Click += (_, _) =>
        {
            NoneRadio.IsChecked = true;
            HostBox.Text = string.Empty;
            PortBox.Text = InspectorNetworkSettings.DefaultUpstreamPort.ToString(
                System.Globalization.CultureInfo.InvariantCulture);
            Http2Check.IsChecked = true;
            Http3Check.IsChecked = true;
            SelectTls(InspectorNetworkSettings.TlsDefault);
            ReuseCheck.IsChecked = true;
            WinAuthBox.Text = string.Empty;
            StatusText.Text = string.Empty;
            UpdateFieldsEnabled();
            UpdateSummary();
            UpdateTlsWarning();
        };
        WinAuthSection.IsVisible = OperatingSystem.IsWindows();
        var quic = InterceptionService.IsHttp3Supported;
        Http3Check.IsEnabled = quic;
        Http3UnavailableText.IsVisible = !quic;
        UpdateFieldsEnabled();
        UpdateSummary();
        UpdateTlsWarning();
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
        Http2Check.IsChecked = s.EnableHttp2;
        Http3Check.IsChecked = s.EnableHttp3;
        SelectTls(s.OutboundTlsPreset);
        ReuseCheck.IsChecked = s.ReuseServerConnections;
        WinAuthBox.Text = s.WinAuthHosts;
    }

    private void SelectTls(string? preset)
    {
        var wanted = string.IsNullOrWhiteSpace(preset) ? InspectorNetworkSettings.TlsDefault : preset;
        foreach (var item in TlsCombo.Items)
        {
            if (item is ComboBoxItem combo && string.Equals(combo.Tag as string, wanted, StringComparison.OrdinalIgnoreCase))
            {
                TlsCombo.SelectedItem = combo;
                return;
            }
        }

        TlsCombo.SelectedIndex = 0;
    }

    private string SelectedTlsPreset() =>
        (TlsCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? InspectorNetworkSettings.TlsDefault;

    private void UpdateSummary()
    {
        var http3 = Http3Check.IsChecked == true && Http3Check.IsEnabled;
        SummaryText.Text = LanguageService.Get(
            InspectorNetworkSettings.ProtocolSummaryKey(Http2Check.IsChecked == true, http3));
    }

    private void UpdateTlsWarning() =>
        TlsLegacyWarning.IsVisible = string.Equals(
            SelectedTlsPreset(), InspectorNetworkSettings.TlsLegacy, StringComparison.OrdinalIgnoreCase);

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

        var winAuthError = WinAuthHostList.Validate(WinAuthBox.Text);
        if (winAuthError is not null)
        {
            StatusText.Text = LanguageService.Get(winAuthError);
            return;
        }

        var s = _settings.Current;
        s.UpstreamProxyEnabled = enabled;
        s.UpstreamProxyHost = HostBox.Text?.Trim() ?? string.Empty;
        s.UpstreamProxyPort = enabled ? port : (port is >= 1 and <= 65535 ? port : InspectorNetworkSettings.DefaultUpstreamPort);
        s.EnableHttp2 = Http2Check.IsChecked == true;
        s.EnableHttp3 = Http3Check.IsEnabled && Http3Check.IsChecked == true;
        s.OutboundTlsPreset = SelectedTlsPreset();
        s.ReuseServerConnections = ReuseCheck.IsChecked == true;
        s.WinAuthHosts = WinAuthBox.Text?.Replace("\r\n", "\n", StringComparison.Ordinal) ?? string.Empty;
        _settings.Save();
        _saved = true;
        Close();
    }
}
