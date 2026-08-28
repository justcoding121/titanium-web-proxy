using System.Collections.ObjectModel;
using System.Text.RegularExpressions;
using Titanium.Inspector.Services;

namespace Titanium.Inspector.ViewModels;

/// <summary>AutoResponder rules — evaluated before breakpoints.</summary>
public sealed class AutoResponderViewModel : System.ComponentModel.INotifyPropertyChanged
{
    private bool _enabled;

    public bool Enabled
    {
        get => _enabled;
        set
        {
            _enabled = value;
            PropertyChanged?.Invoke(this, new(nameof(Enabled)));
        }
    }

    public ObservableCollection<AutoResponderRule> Rules { get; } = new();

    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

    public bool TryMatch(string url, out AutoResponderRule? matched)
    {
        matched = null;
        if (!Enabled)
        {
            return false;
        }

        foreach (var rule in Rules)
        {
            if (!rule.Enabled)
            {
                continue;
            }

            if (!Matches(rule.MatchUrl, url))
            {
                continue;
            }

            matched = rule;
            return true;
        }

        return false;
    }

    public bool TryRespond(SessionSnapshot session, out AutoResponderRule? matched)
        => TryMatch(session.Url, out matched);

    private static bool Matches(string filter, string url)
    {
        if (string.IsNullOrEmpty(filter) || filter == "*")
        {
            return true;
        }

        var pattern = "^" + Regex.Escape(filter).Replace("\\*", ".*") + "$";
        return Regex.IsMatch(url, pattern, RegexOptions.IgnoreCase);
    }
}

public sealed class AutoResponderRule
{
    public string MatchUrl { get; set; } = "*";
    public int StatusCode { get; set; } = 200;
    public string Body { get; set; } = string.Empty;
    public string ContentType { get; set; } = "text/plain";
    public bool Enabled { get; set; } = true;
}
