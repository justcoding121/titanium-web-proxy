using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using Titanium.Inspector.Services;

namespace Titanium.Inspector.ViewModels;

/// <summary>AutoResponder rules — evaluated before breakpoints. Optional Map Local file body.</summary>
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
                LocalFilePath = dto.LocalFilePath ?? string.Empty,
                GraphQlOperationName = dto.GraphQlOperationName ?? string.Empty,
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
            LocalFilePath = string.IsNullOrWhiteSpace(r.LocalFilePath) ? null : r.LocalFilePath,
            GraphQlOperationName = string.IsNullOrWhiteSpace(r.GraphQlOperationName) ? null : r.GraphQlOperationName,
        }).ToList();

    public bool TryMatch(string url, string? requestBody, out AutoResponderRule? matched)
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

            if (!GraphQlOperationMatcher.MatchesOperation(requestBody, rule.GraphQlOperationName))
            {
                continue;
            }

            matched = rule;
            return true;
        }

        return false;
    }

    public bool TryMatch(string url, out AutoResponderRule? matched)
        => TryMatch(url, requestBody: null, out matched);

    public bool TryRespond(SessionSnapshot session, out AutoResponderRule? matched)
        => TryMatch(session.Url, session.RequestBodyText, out matched);

    /// <summary>
    /// Resolves the response body for a matched rule. Map Local (<see cref="AutoResponderRule.LocalFilePath"/>)
    /// wins when the path is non-empty; otherwise uses the inline <see cref="AutoResponderRule.Body"/>.
    /// </summary>
    public static bool TryResolveBody(AutoResponderRule rule, out byte[] body, out string? error)
    {
        error = null;
        if (!string.IsNullOrWhiteSpace(rule.LocalFilePath))
        {
            try
            {
                var path = rule.LocalFilePath.Trim();
                if (!File.Exists(path))
                {
                    error = $"Map Local file not found: {path}";
                    body = Array.Empty<byte>();
                    return false;
                }

                body = File.ReadAllBytes(path);
                return true;
            }
            catch (Exception ex)
            {
                error = $"Map Local read failed: {ex.Message}";
                body = Array.Empty<byte>();
                return false;
            }
        }

        body = Encoding.UTF8.GetBytes(rule.Body ?? string.Empty);
        return true;
    }

    private static bool Matches(string filter, string url)
    {
        if (string.IsNullOrEmpty(filter) || filter == "*")
        {
            return true;
        }

        var pattern = "^" + Regex.Escape(filter).Replace("\\*", ".*") + "$";
        return Regex.IsMatch(url, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    }
}

public sealed class AutoResponderRule : INotifyPropertyChanged
{
    private string _matchUrl = "*";
    private int _statusCode = 200;
    private string _body = string.Empty;
    private string _contentType = "text/plain";
    private string _localFilePath = string.Empty;
    private string _graphQlOperationName = string.Empty;
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

    /// <summary>Optional absolute path; when set, body is loaded from disk (Map Local).</summary>
    public string LocalFilePath
    {
        get => _localFilePath;
        set => SetField(ref _localFilePath, value ?? string.Empty);
    }

    /// <summary>Optional GraphQL operationName filter for same-URL APIs.</summary>
    public string GraphQlOperationName
    {
        get => _graphQlOperationName;
        set => SetField(ref _graphQlOperationName, value ?? string.Empty);
    }

    public bool Enabled
    {
        get => _enabled;
        set => SetField(ref _enabled, value);
    }

    public string Display
    {
        get
        {
            var map = string.IsNullOrWhiteSpace(LocalFilePath) ? string.Empty : " [Map Local]";
            var gql = string.IsNullOrWhiteSpace(GraphQlOperationName) ? string.Empty : $" gql:{GraphQlOperationName}";
            return $"{(Enabled ? "✓" : "✗")} {StatusCode}{map}{gql} {MatchUrl}";
        }
    }

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
