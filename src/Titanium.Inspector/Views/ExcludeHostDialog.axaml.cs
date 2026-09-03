using Avalonia.Controls;
using Avalonia.Interactivity;
using Titanium.Inspector.Services;

namespace Titanium.Inspector.Views;

public enum ExcludeHostKind
{
    TunnelOnly,
    BypassProxy,
}

public partial class ExcludeHostDialog : Window
{
    private readonly SettingsService _settings;
    private bool _saved;

    public ExcludeHostDialog() : this(SettingsService.Load(), "")
    {
    }

    public ExcludeHostDialog(SettingsService settings, string hostname)
    {
        _settings = settings;
        InitializeComponent();
        HostLabel.Text = $"Exclude: {hostname}";
        SaveButton.Click += OnSave;
        CancelButton.Click += (_, _) => Close();
        BypassProxyRadio.IsCheckedChanged += (_, _) => UpdateWarning();
        TunnelOnlyRadio.IsCheckedChanged += (_, _) => UpdateWarning();
    }

    public bool Saved => _saved;
    public ExcludeHostKind SelectedKind =>
        BypassProxyRadio.IsChecked == true ? ExcludeHostKind.BypassProxy : ExcludeHostKind.TunnelOnly;

    public static async Task<(bool Saved, ExcludeHostKind Kind, bool WildcardParent)> ShowAsync(
        Window owner,
        SettingsService settings,
        string hostname)
    {
        var w = new ExcludeHostDialog(settings, hostname);
        await w.ShowDialog(owner);
        return (w.Saved, w.SelectedKind, w.WildcardParentCheck.IsChecked == true);
    }

    private void UpdateWarning() =>
        BypassWarning.IsVisible = BypassProxyRadio.IsChecked == true;

    private void OnSave(object? sender, RoutedEventArgs e)
    {
        var host = HostLabel.Text?["Exclude: ".Length..].Trim();
        if (string.IsNullOrWhiteSpace(host))
        {
            Close();
            return;
        }

        var patterns = new List<string> { host };
        if (WildcardParentCheck.IsChecked == true)
        {
            var parent = ExtractParentDomain(host);
            if (!string.IsNullOrEmpty(parent))
            {
                patterns.Add("*." + parent);
            }
        }

        var s = _settings.Current;
        if (SelectedKind == ExcludeHostKind.BypassProxy)
        {
            foreach (var p in patterns)
            {
                if (!s.SystemProxyBypassHosts.Contains(p, StringComparer.OrdinalIgnoreCase))
                {
                    s.SystemProxyBypassHosts.Add(p);
                }
            }
        }
        else
        {
            foreach (var p in patterns)
            {
                if (!s.DecryptSkipHosts.Contains(p, StringComparer.OrdinalIgnoreCase))
                {
                    s.DecryptSkipHosts.Add(p);
                }
            }
        }

        _settings.Save();
        _saved = true;
        Close();
    }

    private static string? ExtractParentDomain(string hostname)
    {
        var parts = hostname.Split('.');
        if (parts.Length < 2)
        {
            return null;
        }

        return string.Join('.', parts[^2..]);
    }
}
