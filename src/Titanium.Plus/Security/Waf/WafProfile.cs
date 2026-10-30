using System.Collections.Concurrent;
using System.Globalization;
using System.Runtime.CompilerServices;
using Titanium.Web.Proxy.Abstractions.Routing;

namespace Titanium.Plus.Security.Waf;

/// <summary>
/// Settings of the WAF, read from <c>plus.options</c> (<c>waf.*</c>). Everything has a default, so
/// <c>waf.enabled: "true"</c> alone gives detect mode, paranoia level 1, and the built-in pack.
/// </summary>
public sealed class WafOptions
{
    public const int DefaultBlockThreshold = 5;
    public const int DefaultInspectBodyBytes = 8 * 1024;
    public const int MaxInspectBodyBytes = 1024 * 1024;
    public const int DefaultMaxArgLength = 4096;

    /// <summary>Default mode of routes that carry no <c>waf</c> policy. Default: detect.</summary>
    public WafMode Mode { get; init; } = WafMode.Detect;

    /// <summary>1-3. A rule runs when its paranoia level is at or below this. Default: 1.</summary>
    public int ParanoiaLevel { get; init; } = 1;

    /// <summary>Anomaly score that <see cref="WafMode.Block"/> refuses. Default: 5, which one critical rule reaches.</summary>
    public int BlockThreshold { get; init; } = DefaultBlockThreshold;

    /// <summary>Largest body that is buffered and screened. 0 turns body screening off. Default: 8 KiB.</summary>
    public int InspectBodyBytes { get; init; } = DefaultInspectBodyBytes;

    /// <summary>Longest query or body value before TW-PROTO-004 fires. Default: 4096.</summary>
    public int MaxArgLength { get; init; } = DefaultMaxArgLength;

    /// <summary>Rule ids or categories switched off everywhere.</summary>
    public IReadOnlyList<string> DisabledRules { get; init; } = [];

    /// <summary>Exclusions (<c>rule[@target]</c>) that apply everywhere.</summary>
    public IReadOnlyList<string> Exclusions { get; init; } = [];

    /// <summary>YAML file of custom rules. Null for none.</summary>
    public string? RulesFile { get; init; }

    public int CriticalWeight { get; init; } = (int)WafSeverity.Critical;
    public int ErrorWeight { get; init; } = (int)WafSeverity.Error;
    public int WarningWeight { get; init; } = (int)WafSeverity.Warning;
    public int NoticeWeight { get; init; } = (int)WafSeverity.Notice;

    internal int WeightOf(WafSeverity severity) => severity switch
    {
        WafSeverity.Critical => CriticalWeight,
        WafSeverity.Error => ErrorWeight,
        WafSeverity.Warning => WarningWeight,
        _ => NoticeWeight,
    };

    /// <summary>Reads <c>waf.*</c> keys. Throws <see cref="InvalidOperationException"/> for a value that cannot be used.</summary>
    public static WafOptions FromOptions(IReadOnlyDictionary<string, string> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return new WafOptions
        {
            Mode = ParseMode(options.GetValueOrDefault("waf.mode"), WafMode.Detect, "waf.mode"),
            ParanoiaLevel = ReadInt(options, "waf.paranoia", 1, 1, 3),
            BlockThreshold = ReadInt(options, "waf.blockThreshold", DefaultBlockThreshold, 1, 1000),
            InspectBodyBytes = ReadInt(options, "waf.inspectBodyBytes", DefaultInspectBodyBytes, 0, MaxInspectBodyBytes),
            MaxArgLength = ReadInt(options, "waf.maxArgLength", DefaultMaxArgLength, 1, MaxInspectBodyBytes),
            DisabledRules = ReadList(options.GetValueOrDefault("waf.disableRules")),
            Exclusions = ReadList(options.GetValueOrDefault("waf.exclusions")),
            RulesFile = string.IsNullOrWhiteSpace(options.GetValueOrDefault("waf.rulesFile"))
                ? null
                : options["waf.rulesFile"],
            CriticalWeight = ReadInt(options, "waf.score.critical", (int)WafSeverity.Critical, 0, 1000),
            ErrorWeight = ReadInt(options, "waf.score.error", (int)WafSeverity.Error, 0, 1000),
            WarningWeight = ReadInt(options, "waf.score.warning", (int)WafSeverity.Warning, 0, 1000),
            NoticeWeight = ReadInt(options, "waf.score.notice", (int)WafSeverity.Notice, 0, 1000),
        };
    }

    internal static WafMode ParseMode(string? text, WafMode fallback, string key)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return fallback;
        }

        return text.Trim().ToLowerInvariant() switch
        {
            "off" => WafMode.Off,
            "detect" => WafMode.Detect,
            "block" => WafMode.Block,
            _ => throw new InvalidOperationException($"{key} must be off, detect, or block (got '{text}')."),
        };
    }

    private static int ReadInt(IReadOnlyDictionary<string, string> options, string key, int fallback, int min, int max)
    {
        if (!options.TryGetValue(key, out var text) || string.IsNullOrWhiteSpace(text))
        {
            return fallback;
        }

        if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) || value < min || value > max)
        {
            throw new InvalidOperationException($"{key} must be an integer from {min} to {max} (got '{text}').");
        }

        return value;
    }

    private static string[] ReadList(string? csv) =>
        string.IsNullOrWhiteSpace(csv)
            ? []
            : csv.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
}

/// <summary>
/// A rule exclusion: a rule id, a category, or <c>*</c>, optionally limited to one request part and one name
/// (<c>942-SQLI@query:q</c>, <c>sqli@cookie</c>, <c>*@header:Referer</c>).
/// </summary>
internal sealed class WafExclusion
{
    private WafExclusion(string rule, WafTargets target, string? name)
    {
        Rule = rule;
        Target = target;
        Name = name;
    }

    public string Rule { get; }

    public WafTargets Target { get; }

    public string? Name { get; }

    public static WafExclusion Parse(string text)
    {
        var at = text.IndexOf('@', StringComparison.Ordinal);
        if (at < 0)
        {
            return new WafExclusion(text.Trim(), WafTargets.All, null);
        }

        var rule = text[..at].Trim();
        var target = text[(at + 1)..].Trim();
        var colon = target.IndexOf(':', StringComparison.Ordinal);
        var kind = colon < 0 ? target : target[..colon];
        var name = colon < 0 ? null : target[(colon + 1)..].Trim();
        var flags = kind.ToLowerInvariant() switch
        {
            "path" => WafTargets.Path,
            "query" => WafTargets.Query,
            "header" => WafTargets.Header,
            "cookie" => WafTargets.Cookie,
            "body" => WafTargets.Body,
            _ => throw new InvalidOperationException(
                $"WAF exclusion '{text}' has target '{kind}'. Use path, query, header, cookie, or body, optionally with :name."),
        };
        if (rule.Length == 0 || (colon >= 0 && string.IsNullOrEmpty(name)))
        {
            throw new InvalidOperationException($"WAF exclusion '{text}' must be rule[@target[:name]].");
        }

        return new WafExclusion(rule, flags, name);
    }

    public bool Applies(WafRule rule, WafTargets target, ReadOnlySpan<char> name)
    {
        if ((Target & target) == 0)
        {
            return false;
        }

        if (Rule != "*" &&
            !string.Equals(Rule, rule.Id, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(Rule, rule.Category, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return Name is null || name.Equals(Name, StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>Rules chosen for one paranoia level and disable list, grouped by target for the scan loop.</summary>
internal sealed class WafRuleSet
{
    internal readonly record struct Entry(WafRule Rule, int Index);

    private WafRuleSet(WafRule[] all, Entry[] path, Entry[] query, Entry[] header, Entry[] cookie, Entry[] body, Entry?[] checks)
    {
        All = all;
        Path = path;
        Query = query;
        Header = header;
        Cookie = cookie;
        Body = body;
        Checks = checks;
    }

    public WafRule[] All { get; }

    public Entry[] Path { get; }

    public Entry[] Query { get; }

    public Entry[] Header { get; }

    public Entry[] Cookie { get; }

    public Entry[] Body { get; }

    /// <summary>The enabled rule of each <see cref="WafCheck"/>, indexed by the enum value. Null when off.</summary>
    public Entry?[] Checks { get; }

    public static WafRuleSet Build(IEnumerable<WafRule> rules, int paranoia, ISet<string> disabled)
    {
        var all = new List<WafRule>();
        var path = new List<Entry>();
        var query = new List<Entry>();
        var header = new List<Entry>();
        var cookie = new List<Entry>();
        var body = new List<Entry>();
        var checks = new Entry?[Enum.GetValues<WafCheck>().Length];
        foreach (var rule in rules)
        {
            if (rule.Paranoia > paranoia || disabled.Contains(rule.Id) || disabled.Contains(rule.Category))
            {
                continue;
            }

            var entry = new Entry(rule, all.Count);
            all.Add(rule);
            if (rule.Check != WafCheck.None)
            {
                checks[(int)rule.Check] = entry;
                continue;
            }

            if ((rule.Targets & WafTargets.Path) != 0) path.Add(entry);
            if ((rule.Targets & WafTargets.Query) != 0) query.Add(entry);
            if ((rule.Targets & WafTargets.Header) != 0) header.Add(entry);
            if ((rule.Targets & WafTargets.Cookie) != 0) cookie.Add(entry);
            if ((rule.Targets & WafTargets.Body) != 0) body.Add(entry);
        }

        return new WafRuleSet(all.ToArray(), path.ToArray(), query.ToArray(), header.ToArray(), cookie.ToArray(), body.ToArray(), checks);
    }

    public Entry[] For(WafTargets target) => target switch
    {
        WafTargets.Path => Path,
        WafTargets.Query => Query,
        WafTargets.Header => Header,
        WafTargets.Cookie => Cookie,
        _ => Body,
    };
}

/// <summary>What one request is screened with: a mode, a threshold, a rule set, and exclusions. Immutable and cached.</summary>
internal sealed class WafProfile
{
    public static readonly WafProfile Off = new(WafMode.Off, 0, 1, WafRuleSet.Build([], 1, new HashSet<string>()), []);

    public WafProfile(WafMode mode, int blockThreshold, int paranoia, WafRuleSet ruleSet, WafExclusion[] exclusions)
    {
        Mode = mode;
        BlockThreshold = blockThreshold;
        ParanoiaLevel = paranoia;
        RuleSet = ruleSet;
        Exclusions = exclusions;
    }

    public WafMode Mode { get; }

    public int BlockThreshold { get; }

    public int ParanoiaLevel { get; }

    public WafRuleSet RuleSet { get; }

    public WafExclusion[] Exclusions { get; }
}

/// <summary>
/// Builds and caches profiles. The default profile and every <c>waf</c> route policy resolve to a cached object, so
/// a request pays one dictionary read or one weak-table read, never a rule-set build.
/// </summary>
internal sealed class WafProfiles
{
    private readonly WafOptions _options;
    private readonly IReadOnlyList<WafRule> _rules;
    private readonly ConcurrentDictionary<string, WafRuleSet> _ruleSets = new(StringComparer.Ordinal);
    private readonly ConditionalWeakTable<RoutePolicyConfig, WafProfile> _routeProfiles = new();

    public WafProfiles(WafOptions options, IReadOnlyList<WafRule> rules)
    {
        _options = options;
        _rules = rules;
        Default = Create(options.Mode, options.ParanoiaLevel, options.BlockThreshold, options.DisabledRules, options.Exclusions);
    }

    public WafProfile Default { get; }

    public int RuleSetsBuilt => _ruleSets.Count;

    /// <summary>The profile of a <c>waf</c> policy: its own settings over the process-wide ones.</summary>
    public WafProfile ForPolicy(RoutePolicyConfig policy) =>
        _routeProfiles.TryGetValue(policy, out var cached) ? cached : _routeProfiles.GetValue(policy, BuildForPolicy);

    private WafProfile BuildForPolicy(RoutePolicyConfig policy)
    {
        var mode = WafOptions.ParseMode(policy.Mode, _options.Mode, $"policy '{policy.Name}' mode");
        var paranoia = policy.ParanoiaLevel ?? _options.ParanoiaLevel;
        if (paranoia is < 1 or > 3)
        {
            throw new InvalidOperationException($"waf policy '{policy.Name}' paranoiaLevel must be 1-3.");
        }

        var threshold = policy.BlockThreshold ?? _options.BlockThreshold;
        var disabled = policy.DisabledRules is { Count: > 0 } extraDisabled ? _options.DisabledRules.Concat(extraDisabled).ToArray() : _options.DisabledRules;
        var exclusions = policy.Exclusions is { Count: > 0 } extraExclusions ? _options.Exclusions.Concat(extraExclusions).ToArray() : _options.Exclusions;
        return Create(mode, paranoia, threshold, disabled, exclusions);
    }

    private WafProfile Create(WafMode mode, int paranoia, int threshold, IReadOnlyList<string> disabled, IReadOnlyList<string> exclusions)
    {
        var disabledSet = new HashSet<string>(disabled, StringComparer.OrdinalIgnoreCase);
        var key = paranoia.ToString(CultureInfo.InvariantCulture) + "|" + string.Join(',', disabledSet.OrderBy(s => s, StringComparer.OrdinalIgnoreCase));
        var ruleSet = _ruleSets.GetOrAdd(key, _ => WafRuleSet.Build(_rules, paranoia, disabledSet));
        return new WafProfile(mode, threshold, paranoia, ruleSet, [.. exclusions.Select(WafExclusion.Parse)]);
    }
}
