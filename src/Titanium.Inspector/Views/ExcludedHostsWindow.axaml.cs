using Avalonia.Controls;
using Avalonia.Interactivity;
using Titanium.Inspector.Services;

namespace Titanium.Inspector.Views;

public partial class ExcludedHostsWindow : Window
{
    private readonly SettingsService _settings;
    private readonly bool _readOnly;
    private readonly Action? _onSaved;
    private bool _saved;

    public ExcludedHostsWindow() : this(SettingsService.Load(), readOnly: false, null)
    {
    }

    public ExcludedHostsWindow(SettingsService settings, bool readOnly, Action? onSaved)
    {
        _settings = settings;
        _readOnly = readOnly;
        _onSaved = onSaved;
        InitializeComponent();
        Title = readOnly ? "Excluded hosts (view)" : "Excluded hosts";
        BuiltInBox.Text = string.Join(
            Environment.NewLine,
            ExclusionPreview.BuiltInEntries().Select(e => $"{e.Pattern,-36} {e.Outcome,-14} {e.Note}"));
        LoadFromSettings();
        if (readOnly)
        {
            BypassHostsBox.IsReadOnly = true;
            SkipHostsBox.IsReadOnly = true;
            OnlyHostsBox.IsReadOnly = true;
            ProxyLoopbackCheck.IsEnabled = false;
            SaveButton.IsVisible = false;
            CancelButton.Content = "Close";
        }
        else
        {
            SaveButton.Click += OnSave;
            BypassHostsBox.TextChanged += (_, _) => RefreshPreview();
            SkipHostsBox.TextChanged += (_, _) => RefreshPreview();
            OnlyHostsBox.TextChanged += (_, _) => RefreshPreview();
            ProxyLoopbackCheck.IsCheckedChanged += (_, _) => RefreshPreview();
        }

        CancelButton.Click += (_, _) => Close();
        RefreshPreview();
    }

    public bool Saved => _saved;

    public static async Task<bool> ShowAsync(
        Window owner,
        SettingsService settings,
        bool readOnly,
        Action? onSaved)
    {
        var w = new ExcludedHostsWindow(settings, readOnly, onSaved);
        await w.ShowDialog(owner);
        return w.Saved;
    }

    private void LoadFromSettings()
    {
        var s = _settings.Current;
        BypassHostsBox.Text = HostListFormat.Join(s.SystemProxyBypassHosts);
        SkipHostsBox.Text = HostListFormat.Join(s.DecryptSkipHosts);
        OnlyHostsBox.Text = HostListFormat.Join(s.DecryptOnlyHosts);
        ProxyLoopbackCheck.IsChecked = s.ProxyLoopback;
        ScopeBanner.Text = _readOnly
            ? "Read-only view of built-in and saved exclusion rules."
            : "Changes to OS bypass apply when System proxy is on (re-applied on save if active).";
    }

    private InspectorSettings DraftSettings()
    {
        return new InspectorSettings
        {
            ProxyLoopback = ProxyLoopbackCheck.IsChecked == true,
            SystemProxyBypassHosts = HostListFormat.Parse(BypassHostsBox.Text),
            DecryptSkipHosts = HostListFormat.Parse(SkipHostsBox.Text),
            DecryptOnlyHosts = HostListFormat.Parse(OnlyHostsBox.Text),
            AllowEditingBuiltInExclusions = _settings.Current.AllowEditingBuiltInExclusions,
            WarnedAboutPacReplace = _settings.Current.WarnedAboutPacReplace,
        };
    }

    private void RefreshPreview()
    {
        var draft = DraftSettings();
        var (label, value) = ExclusionPreview.FormatForCurrentOs(draft);
        PreviewLabel.Text = label;
        PreviewBox.Text = value;
    }

    private void OnSave(object? sender, RoutedEventArgs e)
    {
        var s = _settings.Current;
        s.SystemProxyBypassHosts = HostListFormat.Parse(BypassHostsBox.Text);
        s.DecryptSkipHosts = HostListFormat.Parse(SkipHostsBox.Text);
        s.DecryptOnlyHosts = HostListFormat.Parse(OnlyHostsBox.Text);
        s.ProxyLoopback = ProxyLoopbackCheck.IsChecked == true;
        _settings.Save();
        _onSaved?.Invoke();
        _saved = true;
        Close();
    }
}
