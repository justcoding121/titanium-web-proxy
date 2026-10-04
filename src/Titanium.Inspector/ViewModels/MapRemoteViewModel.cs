using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Titanium.Inspector.Services;

namespace Titanium.Inspector.ViewModels;

/// <summary>Map Remote rules — rewrite request URL before origin (after AutoResponder).</summary>
public sealed class MapRemoteViewModel : INotifyPropertyChanged
{
    private readonly object _rulesGate = new();
    private bool _enabled;
    private MapRemoteRule? _selectedRule;

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

    /// <summary>UI-bound rule list. Mutate only via <see cref="AddRule"/> / <see cref="RemoveRule"/> / <see cref="LoadFromDtos"/>.</summary>
    public ObservableCollection<MapRemoteRule> Rules { get; } = new();

    public MapRemoteRule? SelectedRule
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

    public void AddRule(MapRemoteRule rule)
    {
        lock (_rulesGate)
        {
            Rules.Add(rule);
        }
    }

    public bool RemoveRule(MapRemoteRule rule)
    {
        lock (_rulesGate)
        {
            return Rules.Remove(rule);
        }
    }

    public void ClearRules()
    {
        lock (_rulesGate)
        {
            Rules.Clear();
        }
    }

    public void LoadFromDtos(IEnumerable<MapRemoteRuleDto> dtos)
    {
        lock (_rulesGate)
        {
            Rules.Clear();
            foreach (var dto in dtos)
            {
                Rules.Add(new MapRemoteRule
                {
                    MatchUrl = dto.MatchUrl,
                    TargetUrl = dto.TargetUrl,
                    Enabled = dto.Enabled,
                    GraphQlOperationName = dto.GraphQlOperationName ?? string.Empty,
                });
            }
        }
    }

    public List<MapRemoteRuleDto> ToDtos() =>
        SnapshotRules().Select(r => new MapRemoteRuleDto
        {
            MatchUrl = r.MatchUrl,
            TargetUrl = r.TargetUrl,
            Enabled = r.Enabled,
            GraphQlOperationName = string.IsNullOrWhiteSpace(r.GraphQlOperationName) ? null : r.GraphQlOperationName,
        }).ToList();

    /// <summary>True when an enabled rule filters by GraphQL operation (needs request body).</summary>
    public bool HasEnabledGraphQlRule()
    {
        foreach (var r in SnapshotRules())
        {
            if (r.Enabled && !string.IsNullOrWhiteSpace(r.GraphQlOperationName))
            {
                return true;
            }
        }

        return false;
    }

    public bool TryRewrite(string url, string? requestBody, out string? rewritten, out MapRemoteRule? matched)
    {
        rewritten = null;
        matched = null;
        if (!Enabled)
        {
            return false;
        }

        // Snapshot: proxy OnBeforeRequest enumerates while the UI may Add/Remove.
        foreach (var rule in SnapshotRules())
        {
            if (!rule.Enabled || string.IsNullOrWhiteSpace(rule.TargetUrl))
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

            if (!TryApplyRewrite(url, rule.MatchUrl, rule.TargetUrl, out rewritten))
            {
                continue;
            }

            matched = rule;
            return true;
        }

        return false;
    }

    public bool TryRewrite(string url, out string? rewritten, out MapRemoteRule? matched)
        => TryRewrite(url, requestBody: null, out rewritten, out matched);

    private MapRemoteRule[] SnapshotRules()
    {
        lock (_rulesGate)
        {
            var arr = new MapRemoteRule[Rules.Count];
            Rules.CopyTo(arr, 0);
            return arr;
        }
    }

    /// <summary>
    /// Rewrites <paramref name="url"/> using wildcard capture from <paramref name="matchPattern"/>
    /// into <paramref name="targetTemplate"/>. A single trailing <c>*</c> in both match and target
    /// preserves the matched suffix (path/query). Otherwise the target absolute URL replaces the request.
    /// </summary>
    public static bool TryApplyRewrite(string url, string matchPattern, string targetTemplate, out string? rewritten) // NOSONAR S3776 -- Wildcard rewrite keeps match/target suffix handling together.
    {
        rewritten = null;
        if (string.IsNullOrWhiteSpace(targetTemplate))
        {
            return false;
        }

        var target = targetTemplate.Trim();
        if (!Uri.TryCreate(target.Contains('*') ? target.Replace("*", "x", StringComparison.Ordinal) : target,
                UriKind.Absolute, out _) && !target.Contains('*'))
        {
            // Allow templates with *; validate non-wildcard targets are absolute.
            return false;
        }

        if (string.IsNullOrEmpty(matchPattern) || matchPattern == "*")
        {
            if (target.Contains('*'))
            {
                rewritten = target.Replace("*", url, StringComparison.Ordinal);
            }
            else
            {
                rewritten = target;
            }

            return Uri.TryCreate(rewritten, UriKind.Absolute, out _);
        }

        var star = matchPattern.IndexOf('*');
        if (star >= 0 && matchPattern.IndexOf('*', star + 1) < 0)
        {
            var prefix = matchPattern[..star];
            var suffix = matchPattern[(star + 1)..];
            if (url.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
                url.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) &&
                url.Length >= prefix.Length + suffix.Length)
            {
                var captured = url.Substring(prefix.Length, url.Length - prefix.Length - suffix.Length);
                if (target.Contains('*'))
                {
                    rewritten = target.Replace("*", captured, StringComparison.Ordinal);
                }
                else
                {
                    rewritten = target;
                }

                return Uri.TryCreate(rewritten, UriKind.Absolute, out _);
            }
        }

        // Full-string wildcard regex match without capture → replace with literal target (no *).
        if (!target.Contains('*') && Matches(matchPattern, url))
        {
            rewritten = target;
            return Uri.TryCreate(rewritten, UriKind.Absolute, out _);
        }

        return false;
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

public sealed class MapRemoteRule : INotifyPropertyChanged
{
    private string _matchUrl = "*";
    private string _targetUrl = "http://127.0.0.1/"; // NOSONAR S1075 -- Default Map Remote target is loopback.
    private string _graphQlOperationName = string.Empty;
    private bool _enabled = true;

    public string MatchUrl
    {
        get => _matchUrl;
        set => SetField(ref _matchUrl, value);
    }

    public string TargetUrl
    {
        get => _targetUrl;
        set => SetField(ref _targetUrl, value);
    }

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
            var gql = string.IsNullOrWhiteSpace(GraphQlOperationName) ? string.Empty : $" GraphQL:{GraphQlOperationName}";
            return $"{(Enabled ? "✓" : "✗")} {MatchUrl}{gql} → {TargetUrl}";
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
