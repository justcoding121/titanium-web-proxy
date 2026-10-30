using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using YamlDotNet.Serialization;

namespace Titanium.Plus.Security.Waf;

/// <summary>The built-in rule pack, embedded in the assembly and versioned by name.</summary>
public static class WafRulePack
{
    /// <summary>Version of the embedded pack. A rule change that can alter a verdict ships as a new version.</summary>
    public const string Version = "waf-pack-1";

    private const string ResourceName = "Titanium.Plus.Security.Waf.waf-pack-1.json";
    private static readonly JsonSerializerOptions PackJsonOptions = new() { PropertyNameCaseInsensitive = true };
    private static readonly Lazy<IReadOnlyList<WafRule>> Loaded = new(LoadEmbedded);

    /// <summary>Every rule of the pack, compiled once per process.</summary>
    public static IReadOnlyList<WafRule> Rules => Loaded.Value;

    private static IReadOnlyList<WafRule> LoadEmbedded()
    {
        using var stream = typeof(WafRulePack).Assembly.GetManifestResourceStream(ResourceName)
                           ?? throw new WafRuleException($"The embedded rule pack '{Version}' is missing.");
        using var reader = new StreamReader(stream);
        return Parse(reader.ReadToEnd());
    }

    /// <summary>Parses pack JSON. Throws <see cref="WafRuleException"/> for the first invalid rule.</summary>
    internal static IReadOnlyList<WafRule> Parse(string json)
    {
        PackFile? file;
        try
        {
            file = JsonSerializer.Deserialize<PackFile>(json, PackJsonOptions);
        }
        catch (JsonException ex)
        {
            throw new WafRuleException("The rule pack is not valid JSON: " + ex.Message);
        }

        if (file?.RuleEntries is not { Count: > 0 } entries)
        {
            throw new WafRuleException("The rule pack has no rules.");
        }

        if (!string.Equals(file.Pack, Version, StringComparison.Ordinal))
        {
            throw new WafRuleException($"The rule pack names '{file.Pack}', expected '{Version}'.");
        }

        var rules = new List<WafRule>(entries.Count);
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries)
        {
            var rule = WafRule.Build(new WafRule.Spec
            {
                Id = entry.Id,
                Category = entry.Category,
                Severity = WafSpecParser.ParseSeverity(entry.Id, entry.Severity),
                Targets = WafSpecParser.ParseTargets(entry.Id, entry.Targets, entry.Headers, out var headerNames),
                Paranoia = entry.Paranoia ?? 1,
                Description = entry.Description,
                HeaderNames = headerNames,
                Check = WafSpecParser.ParseCheck(entry.Id, entry.Check),
                Regex = entry.Pattern,
                Contains = entry.Contains?.ToArray(),
                Prefilter = entry.Prefilter?.ToArray(),
                IgnoreCase = entry.IgnoreCase ?? true,
            });
            if (!ids.Add(rule.Id))
            {
                throw new WafRuleException($"The rule pack lists '{rule.Id}' twice.");
            }

            rules.Add(rule);
        }

        return rules;
    }

    // JSON DTOs: public setters are for System.Text.Json (no code writes them).
    private sealed class PackFile
    {
        public string? Pack { get; set; } // NOSONAR S1144 -- System.Text.Json binder.
        [JsonPropertyName("rules")]
        public List<PackRule>? RuleEntries { get; set; } // NOSONAR S1144 -- System.Text.Json binder.
    }

    private sealed class PackRule
    {
        public string? Id { get; set; } // NOSONAR S1144 -- System.Text.Json binder.
        public string? Category { get; set; } // NOSONAR S1144 -- System.Text.Json binder.
        public string? Severity { get; set; } // NOSONAR S1144 -- System.Text.Json binder.
        public int? Paranoia { get; set; } // NOSONAR S1144 -- System.Text.Json binder.
        public List<string>? Targets { get; set; } // NOSONAR S1144 -- System.Text.Json binder.
        public List<string>? Headers { get; set; } // NOSONAR S1144 -- System.Text.Json binder.
        public string? Pattern { get; set; } // NOSONAR S1144 -- System.Text.Json binder.
        public List<string>? Contains { get; set; } // NOSONAR S1144 -- System.Text.Json binder.
        public List<string>? Prefilter { get; set; } // NOSONAR S1144 -- System.Text.Json binder.
        public string? Check { get; set; } // NOSONAR S1144 -- System.Text.Json binder.
        public string? Description { get; set; } // NOSONAR S1144 -- System.Text.Json binder.
        public bool? IgnoreCase { get; set; } // NOSONAR S1144 -- System.Text.Json binder.
    }
}

/// <summary>
/// Reads <c>waf.rulesFile</c>: YAML with a <c>rules</c> list. A legacy JSON deny-list (<c>denyPaths</c>) has no
/// <c>rules</c> key and yields no custom rules.
/// </summary>
public static class WafCustomRules
{
    private const int MaxRules = 1000;

    /// <summary>Loads and compiles the rules of a file. Throws <see cref="WafRuleException"/> naming every problem found.</summary>
    public static IReadOnlyList<WafRule> LoadFile(string path)
    {
        string text;
        try
        {
            text = File.ReadAllText(path);
        }
        catch (IOException ex)
        {
            throw new WafRuleException($"waf.rulesFile '{path}' cannot be read: {ex.Message}");
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new WafRuleException($"waf.rulesFile '{path}' cannot be read: {ex.Message}");
        }

        return Parse(text, path);
    }

    /// <summary>Parses YAML rule text. <paramref name="source"/> names the file in messages.</summary>
    public static IReadOnlyList<WafRule> Parse(string yaml, string source = "waf.rulesFile")
    {
        RulesFile? file;
        try
        {
            var deserializer = new DeserializerBuilder()
                .WithNamingConvention(YamlDotNet.Serialization.NamingConventions.CamelCaseNamingConvention.Instance)
                .IgnoreUnmatchedProperties()
                .Build();
            file = deserializer.Deserialize<RulesFile>(yaml);
        }
        catch (YamlDotNet.Core.YamlException ex)
        {
            throw new WafRuleException($"{source} is not valid YAML: {ex.Message}");
        }

        if (file?.Rules is not { Count: > 0 } entries)
        {
            return [];
        }

        if (entries.Count > MaxRules)
        {
            throw new WafRuleException($"{source} lists {entries.Count} rules. The most allowed is {MaxRules}.");
        }

        var packIds = new HashSet<string>(WafRulePack.Rules.Select(r => r.Id), StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var rules = new List<WafRule>(entries.Count);
        var problems = new List<string>();
        foreach (var entry in entries)
        {
            try
            {
                var rule = BuildRule(entry);
                if (packIds.Contains(rule.Id))
                {
                    throw new WafRuleException($"WAF rule '{rule.Id}' reuses an id of {WafRulePack.Version}. Pick another id.");
                }

                if (!seen.Add(rule.Id))
                {
                    throw new WafRuleException($"WAF rule '{rule.Id}' is listed twice.");
                }

                rules.Add(rule);
            }
            catch (WafRuleException ex)
            {
                problems.Add(ex.Message);
            }
        }

        if (problems.Count > 0)
        {
            throw new WafRuleException($"{source}: {string.Join(" ", problems)}");
        }

        return rules;
    }

    private static WafRule BuildRule(RuleEntry entry)
    {
        var id = entry.Id;
        var action = WafAction.Score;
        if (!string.IsNullOrWhiteSpace(entry.Action) && !Enum.TryParse(entry.Action.Trim(), true, out action))
        {
            throw new WafRuleException($"WAF rule '{id}' action '{entry.Action}' must be score, block, or log.");
        }

        var transforms = new List<WafTransform>();
        foreach (var name in entry.Transformations ?? [])
        {
            if (!Enum.TryParse<WafTransform>(name?.Trim(), true, out var transform))
            {
                throw new WafRuleException(
                    $"WAF rule '{id}' transformation '{name}' must be urlDecode, lowercase, removeNulls, or htmlEntityDecode.");
            }

            transforms.Add(transform);
        }

        return WafRule.Build(new WafRule.Spec
        {
            Id = id,
            Category = entry.Category,
            Severity = string.IsNullOrWhiteSpace(entry.Severity) ? WafSeverity.Error : WafSpecParser.ParseSeverity(id, entry.Severity),
            Targets = WafSpecParser.ParseTargets(id, entry.Targets, null, out var headerNames),
            Paranoia = entry.Paranoia ?? 1,
            Action = action,
            Score = entry.Score,
            Description = entry.Description,
            HeaderNames = headerNames,
            Transforms = [.. transforms],
            Regex = entry.Regex,
            Contains = entry.Contains?.ToArray(),
            IgnoreCase = entry.IgnoreCase ?? false,
            IsCustom = true,
        });
    }

    // YAML/JSON DTOs: public setters are for the binder (no code writes them).
    private sealed class RulesFile
    {
        public List<RuleEntry>? Rules { get; set; } // NOSONAR S1144 -- binder-populated DTO.
    }

    private sealed class RuleEntry
    {
        public string? Id { get; set; } // NOSONAR S1144 -- binder-populated DTO.
        public string? Description { get; set; } // NOSONAR S1144 -- binder-populated DTO.
        public string? Category { get; set; } // NOSONAR S1144 -- binder-populated DTO.
        public string? Severity { get; set; } // NOSONAR S1144 -- binder-populated DTO.
        public int? Paranoia { get; set; } // NOSONAR S1144 -- binder-populated DTO.
        public List<string>? Targets { get; set; } // NOSONAR S1144 -- binder-populated DTO.
        public string? Regex { get; set; } // NOSONAR S1144 -- binder-populated DTO.
        public List<string>? Contains { get; set; } // NOSONAR S1144 -- binder-populated DTO.
        public List<string>? Transformations { get; set; } // NOSONAR S1144 -- binder-populated DTO.
        public string? Action { get; set; } // NOSONAR S1144 -- binder-populated DTO.
        public int? Score { get; set; } // NOSONAR S1144 -- binder-populated DTO.
        public bool? IgnoreCase { get; set; } // NOSONAR S1144 -- binder-populated DTO.
    }
}

internal static class WafSpecParser
{
    public static WafSeverity ParseSeverity(string? id, string? text)
    {
        if (Enum.TryParse<WafSeverity>(text?.Trim(), true, out var severity) && Enum.IsDefined(severity))
        {
            return severity;
        }

        throw new WafRuleException($"WAF rule '{id}' severity '{text}' must be notice, warning, error, or critical.");
    }

    public static WafCheck ParseCheck(string? id, string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return WafCheck.None;
        }

        if (Enum.TryParse<WafCheck>(text.Trim(), true, out var check) && check != WafCheck.None)
        {
            return check;
        }

        throw new WafRuleException($"WAF rule '{id}' check '{text}' is not a known check.");
    }

    /// <summary>
    /// Reads <c>path</c>, <c>query</c>, <c>header</c>, <c>cookie</c>, <c>body</c>, and <c>header:Name</c> (a header rule that
    /// reads only that header).
    /// </summary>
    public static WafTargets ParseTargets(string? id, IEnumerable<string>? names, IEnumerable<string>? extraHeaders, out string[]? headerNames)
    {
        var targets = WafTargets.None;
        List<string>? headers = null;
        foreach (var raw in names ?? [])
        {
            var name = raw?.Trim() ?? string.Empty;
            var colon = name.IndexOf(':', StringComparison.Ordinal);
            if (colon > 0)
            {
                if (!name.StartsWith("header:", StringComparison.OrdinalIgnoreCase) || colon == name.Length - 1)
                {
                    throw new WafRuleException($"WAF rule '{id}' target '{name}' must be header:<Name>.");
                }

                targets |= WafTargets.Header;
                (headers ??= []).Add(name[(colon + 1)..].Trim());
                continue;
            }

            targets |= name.ToLowerInvariant() switch
            {
                "path" => WafTargets.Path,
                "query" => WafTargets.Query,
                "header" => WafTargets.Header,
                "cookie" => WafTargets.Cookie,
                "body" => WafTargets.Body,
                _ => throw new WafRuleException(
                    $"WAF rule '{id}' target '{name}' must be path, query, header, header:<Name>, cookie, or body."),
            };
        }

        if (extraHeaders is not null)
        {
            (headers ??= []).AddRange(extraHeaders);
        }

        headerNames = headers?.ToArray();
        return targets;
    }
}
