using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Titanium.Inspector.Services;

namespace Titanium.Inspector.ViewModels;

/// <summary>AutoResponder rules — evaluated before breakpoints.</summary>
public sealed class AutoResponderViewModel : INotifyPropertyChanged
{
    private bool _enabled;
    private AutoResponderRule? _selectedRule;

    public bool Enabled
    {
        get => _enabled;
        set
        {
            if (_enabled == value)
            {
                return;
            }

            _enabled = value;
            PropertyChanged?.Invoke(this, new(nameof(Enabled)));
            EnabledChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public ObservableCollection<AutoResponderRule> Rules { get; } = new();

    public AutoResponderRule? SelectedRule
    {
        get => _selectedRule;
        set
        {
            if (ReferenceEquals(_selectedRule, value))
            {
                return;
            }

            _selectedRule = value;
            PropertyChanged?.Invoke(this, new(nameof(SelectedRule)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public event EventHandler? EnabledChanged;
    public event EventHandler? RulesChanged;

    public void NotifyRulesChanged() => RulesChanged?.Invoke(this, EventArgs.Empty);

    public void LoadFromDtos(IEnumerable<AutoResponderRuleDto> dtos)
    {
        Rules.Clear();
        foreach (var dto in dtos)
        {
            Rules.Add(new AutoResponderRule
            {
                MatchUrl = dto.MatchUrl,
                StatusCode = dto.StatusCode,
                Body = dto.Body,
                ContentType = string.IsNullOrEmpty(dto.ContentType) ? "text/plain" : dto.ContentType,
                Enabled = dto.Enabled,
            });
        }
    }

    public List<AutoResponderRuleDto> ToDtos() =>
        Rules.Select(r => new AutoResponderRuleDto
        {
            MatchUrl = r.MatchUrl,
            StatusCode = r.StatusCode,
            Body = r.Body,
            ContentType = r.ContentType,
            Enabled = r.Enabled,
        }).ToList();

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

public sealed class AutoResponderRule : INotifyPropertyChanged
{
    private string _matchUrl = "*";
    private int _statusCode = 200;
    private string _body = string.Empty;
    private string _contentType = "text/plain";
    private bool _enabled = true;

    public string MatchUrl
    {
        get => _matchUrl;
        set => SetField(ref _matchUrl, value);
    }

    public int StatusCode
    {
        get => _statusCode;
        set => SetField(ref _statusCode, value);
    }

    public string Body
    {
        get => _body;
        set => SetField(ref _body, value);
    }

    public string ContentType
    {
        get => _contentType;
        set => SetField(ref _contentType, value);
    }

    public bool Enabled
    {
        get => _enabled;
        set => SetField(ref _enabled, value);
    }

    public string Display => $"{(Enabled ? "✓" : "✗")} {StatusCode} {MatchUrl}";

    public event PropertyChangedEventHandler? PropertyChanged;

    private void SetField<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (Equals(field, value))
        {
            return;
        }

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        if (name is not nameof(Display))
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Display)));
        }
    }
}
