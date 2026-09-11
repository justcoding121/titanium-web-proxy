using Avalonia.Controls;
using Avalonia.Interactivity;
using Titanium.Inspector.Services;
using Titanium.Web.Proxy.Models;

namespace Titanium.Inspector.Views;

public partial class ExcludedHostsWindow : Window
{
    private readonly SettingsService _settings;
    private readonly bool _readOnly;
    private readonly Action? _onSaved;
    private readonly InterceptionService? _interception;
    private bool _saved;

    public ExcludedHostsWindow() : this(SettingsService.Load(), readOnly: false, null, null)
    {
    }

    public ExcludedHostsWindow(
        SettingsService settings,
        bool readOnly,
        Action? onSaved,
        InterceptionService? interception = null)
    {
        _settings = settings;
        _readOnly = readOnly;
        _onSaved = onSaved;
        _interception = interception;
        InitializeComponent();
        IntroText.Text = OsTrustUxCopy.ExcludedHostsIntro();
        Title = readOnly ? "Excluded hosts (view)" : "Excluded hosts";
        _settings.EnsureExclusionsSeeded();
        LoadFromSettings();
        RefreshLearnedList();
        if (readOnly)
        {
            BypassHostsBox.IsReadOnly = true;
            SkipHostsBox.IsReadOnly = true;
            LearningEnabledCheck.IsEnabled = false;
            PromoteLearnedButton.IsEnabled = false;
            ForgetLearnedButton.IsEnabled = false;
            ClearLearnedButton.IsEnabled = false;
            ResetDefaultsButton.IsVisible = false;
            SaveButton.IsVisible = false;
            CancelButton.Content = "Close";
        }
        else
        {
            SaveButton.Click += OnSave;
            ResetDefaultsButton.Click += OnResetDefaults;
            LearningEnabledCheck.IsCheckedChanged += (_, _) => UpdateLearningPausedUi();
            PromoteLearnedButton.Click += OnPromoteLearned;
            ForgetLearnedButton.Click += OnForgetLearned;
            ClearLearnedButton.Click += OnClearLearned;
        }

        CancelButton.Click += (_, _) => Close();
        UpdateLearningPausedUi();
    }

    public bool Saved => _saved;

    public static async Task<bool> ShowAsync(
        Window owner,
        SettingsService settings,
        bool readOnly,
        Action? onSaved,
        InterceptionService? interception = null)
    {
        var w = new ExcludedHostsWindow(settings, readOnly, onSaved, interception);
        await w.ShowDialog(owner);
        return w.Saved;
    }

    private void LoadFromSettings()
    {
        var s = _settings.Current;
        BypassHostsBox.Text = HostListFormat.Join(s.SystemProxyBypassHosts);
        SkipHostsBox.Text = HostListFormat.Join(s.DecryptSkipHosts);
        LearningEnabledCheck.IsChecked = s.EnableDecryptFailureBypass;
        ScopeBanner.Text = _readOnly
            ? "Read-only view of saved exclusion rules (including factory seeds)."
            : "Changes to OS bypass apply when System proxy is on (re-applied on save if active).";
    }

    private void UpdateLearningPausedUi()
    {
        var on = LearningEnabledCheck.IsChecked == true;
        LearningPausedBanner.IsVisible = !on;
        LearnedList.Opacity = on ? 1 : 0.55;
    }

    private void RefreshLearnedList()
    {
        var entries = _interception?.GetDecryptFailureBypassEntries()
                      ?? Array.Empty<DecryptFailureBypassEntry>();
        var active = entries.Where(e => e.BypassActive).ToList();
        LearnedList.ItemsSource = active
            .Select(e => $"{e.Host}  ·  Decrypt failure  ·  {e.LearnedAtUtc:u}")
            .ToList();
        LearnedList.Tag = active;
        LearnedEmptyText.IsVisible = active.Count == 0;
        LearnedList.IsVisible = active.Count > 0;
    }

    private DecryptFailureBypassEntry? SelectedLearned()
    {
        if (LearnedList.Tag is not List<DecryptFailureBypassEntry> list)
            return null;
        var idx = LearnedList.SelectedIndex;
        if (idx < 0 || idx >= list.Count)
            return null;
        return list[idx];
    }

    private void OnPromoteLearned(object? sender, RoutedEventArgs e)
    {
        var entry = SelectedLearned();
        if (entry is null)
            return;

        var hosts = HostListFormat.Parse(SkipHostsBox.Text);
        if (!hosts.Contains(entry.Host, StringComparer.OrdinalIgnoreCase))
            hosts.Add(entry.Host);
        SkipHostsBox.Text = HostListFormat.Join(hosts);
        _interception?.RemoveDecryptFailureBypass(entry.Host);
        RefreshLearnedList();
    }

    private void OnForgetLearned(object? sender, RoutedEventArgs e)
    {
        var entry = SelectedLearned();
        if (entry is null)
            return;
        _interception?.RemoveDecryptFailureBypass(entry.Host);
        RefreshLearnedList();
    }

    private void OnClearLearned(object? sender, RoutedEventArgs e)
    {
        _interception?.ClearDecryptFailureBypass();
        RefreshLearnedList();
    }

    private void OnResetDefaults(object? sender, RoutedEventArgs e)
    {
        SettingsService.ApplyFactoryExclusionDefaults(_settings.Current);
        BypassHostsBox.Text = HostListFormat.Join(_settings.Current.SystemProxyBypassHosts);
        SkipHostsBox.Text = HostListFormat.Join(_settings.Current.DecryptSkipHosts);
        LearningEnabledCheck.IsChecked = true;
        UpdateLearningPausedUi();
    }

    private void OnSave(object? sender, RoutedEventArgs e)
    {
        var s = _settings.Current;
        s.SystemProxyBypassHosts = HostListFormat.Parse(BypassHostsBox.Text);
        s.DecryptSkipHosts = HostListFormat.Parse(SkipHostsBox.Text);
        s.EnableDecryptFailureBypass = LearningEnabledCheck.IsChecked == true;
        s.ExclusionsInitialized = true;
        _settings.Save();
        if (_interception is not null)
        {
            _interception.EnableDecryptFailureBypass = s.EnableDecryptFailureBypass;
            _interception.ApplyDecryptFailureBypassSetting();
        }

        _onSaved?.Invoke();
        _saved = true;
        Close();
    }
}
