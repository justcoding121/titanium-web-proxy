using Avalonia.Controls;
using Avalonia.Interactivity;
using Titanium.Inspector.Services;

namespace Titanium.Inspector.Views;

public partial class ExcludeHostDialog : Window
{
    private readonly SettingsService _settings;
    private readonly string _hostname;
    private bool _saved;

    public ExcludeHostDialog() : this(SettingsService.Load(), "")
    {
    }

    public ExcludeHostDialog(SettingsService settings, string hostname)
    {
        _settings = settings;
        _hostname = hostname.Trim();
        InitializeComponent();
        HostLabel.Text = _hostname;
        var parent = ExtractParentDomain(_hostname);
        if (string.IsNullOrEmpty(parent))
        {
            WildcardParentCheck.IsVisible = false;
        }
        else
        {
            WildcardParentCheck.Content = "Also exclude other hosts on " + parent;
        }

        SaveButton.Click += OnSave;
        CancelButton.Click += (_, _) => Close();
    }

    public bool Saved => _saved;

    public static async Task<(bool Saved, bool WildcardParent)> ShowAsync(
        Window owner,
        SettingsService settings,
        string hostname)
    {
        var w = new ExcludeHostDialog(settings, hostname);
        await w.ShowDialog(owner);
        return (w.Saved, w.WildcardParentCheck.IsChecked == true);
    }

    private void OnSave(object? sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_hostname))
        {
            Close();
            return;
        }

        var patterns = new List<string> { _hostname };
        if (WildcardParentCheck.IsVisible && WildcardParentCheck.IsChecked == true)
        {
            var parent = ExtractParentDomain(_hostname);
            if (!string.IsNullOrEmpty(parent))
            {
                patterns.Add("*." + parent);
            }
        }

        var s = _settings.Current;
        foreach (var p in patterns.Where(p => !s.DecryptSkipHosts.Contains(p, StringComparer.OrdinalIgnoreCase)))
        {
            s.DecryptSkipHosts.Add(p);
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
