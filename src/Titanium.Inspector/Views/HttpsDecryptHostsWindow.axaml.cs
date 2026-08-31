using Avalonia.Controls;
using Avalonia.Interactivity;
using Titanium.Inspector.Services;

namespace Titanium.Inspector.Views;

public partial class HttpsDecryptHostsWindow : Window
{
    private readonly SettingsService _settings;
    private readonly Action? _onSaved;
    private bool _saved;

    public HttpsDecryptHostsWindow() : this(SettingsService.Load(), null)
    {
    }

    public HttpsDecryptHostsWindow(SettingsService settings, Action? onSaved)
    {
        _settings = settings;
        _onSaved = onSaved;
        InitializeComponent();
        LoadFromSettings();
        SaveButton.Click += OnSave;
        CancelButton.Click += (_, _) => Close();
    }

    public bool Saved => _saved;

    public static async Task<bool> ShowAsync(Window owner, SettingsService settings, Action? onSaved)
    {
        var w = new HttpsDecryptHostsWindow(settings, onSaved);
        await w.ShowDialog(owner);
        return w.Saved;
    }

    private void LoadFromSettings()
    {
        var s = _settings.Current;
        SkipHostsBox.Text = HostListFormat.Join(s.DecryptSkipHosts);
        OnlyHostsBox.Text = HostListFormat.Join(s.DecryptOnlyHosts);
    }

    private void OnSave(object? sender, RoutedEventArgs e)
    {
        var s = _settings.Current;
        s.DecryptSkipHosts = HostListFormat.Parse(SkipHostsBox.Text);
        s.DecryptOnlyHosts = HostListFormat.Parse(OnlyHostsBox.Text);
        _settings.Save();
        _onSaved?.Invoke();
        _saved = true;
        Close();
    }
}

/// <summary>Newline-separated host pattern helpers for settings UI.</summary>
public static class HostListFormat
{
    public static string Join(IEnumerable<string>? hosts) =>
        hosts is null ? "" : string.Join(Environment.NewLine, hosts.Where(h => !string.IsNullOrWhiteSpace(h)));

    public static List<string> Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return [];
        }

        return text
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(h => h.Length > 0 && !h.StartsWith('#'))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
