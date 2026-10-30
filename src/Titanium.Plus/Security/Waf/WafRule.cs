using System.Buffers;
using System.Text.RegularExpressions;

namespace Titanium.Plus.Security.Waf;

/// <summary>A rule file or the built-in pack holds a rule that cannot be loaded.</summary>
public sealed class WafRuleException : InvalidOperationException
{
    public WafRuleException(string message) : base(message)
    {
    }
}

/// <summary>
/// One screening rule: an id, a category, a severity, the request parts it reads, and either a regular expression
/// or a list of strings. Rules are immutable and shared by every rule set that includes them.
/// </summary>
public sealed class WafRule
{
    /// <summary>Longest a single rule match may run. A rule that exceeds it is counted and ignored for that value.</summary>
    public static readonly TimeSpan MatchTimeout = TimeSpan.FromMilliseconds(5);

    private readonly Regex? _regex;
    private readonly SearchValues<string>? _needles;
    private readonly SearchValues<string>? _prefilter;
    private readonly WafTransform[] _transforms;

    private WafRule( // NOSONAR S107 -- Immutable rule record; a builder would duplicate load-time validation in Build(Spec).
        string id, string category, WafSeverity severity, WafTargets targets, int paranoia, WafAction action,
        int? score, string? description, string[]? headerNames, WafCheck check, WafTransform[] transforms, bool isCustom,
        Regex? regex, string[]? contains, string[]? prefilter, bool ignoreCase, string? pattern)
    {
        Id = id;
        Category = category;
        Severity = severity;
        Targets = targets;
        Paranoia = paranoia;
        Action = action;
        Score = score;
        Description = description;
        HeaderNames = headerNames;
        Check = check;
        _transforms = transforms;
        IsCustom = isCustom;
        _regex = regex;
        Pattern = pattern;
        Contains = contains;
        if (contains is { Length: > 0 })
        {
            _needles = SearchValues.Create(contains, ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
        }

        if (prefilter is { Length: > 0 })
        {
            _prefilter = SearchValues.Create(prefilter, StringComparison.OrdinalIgnoreCase);
        }
    }

    public string Id { get; }

    public string Category { get; }

    public WafSeverity Severity { get; }

    public WafTargets Targets { get; }

    /// <summary>The lowest paranoia level that runs the rule (1-3).</summary>
    public int Paranoia { get; }

    public WafAction Action { get; }

    /// <summary>Score override. Null uses the weight of <see cref="Severity"/>.</summary>
    public int? Score { get; }

    public string? Description { get; }

    /// <summary>When set, a <see cref="WafTargets.Header"/> rule reads only these header names (case-insensitive).</summary>
    public IReadOnlyList<string>? HeaderNames { get; }

    public WafCheck Check { get; }

    public bool IsCustom { get; }

    public string? Pattern { get; }

    public IReadOnlyList<string>? Contains { get; }

    public IReadOnlyList<WafTransform> Transforms => _transforms;

    internal bool HasTransforms => _transforms.Length > 0;

    internal bool IsPatternRule => _regex is not null || _needles is not null;

    /// <summary>The Regex object of a pattern rule, for tests that probe its engine and timeout.</summary>
    internal Regex? CompiledRegex => _regex;

    /// <summary>True when this rule reads the header with the given name.</summary>
    internal bool ReadsHeader(ReadOnlySpan<char> name)
    {
        if (HeaderNames is null)
        {
            return true;
        }

        for (var i = 0; i < HeaderNames.Count; i++)
        {
            if (name.Equals(HeaderNames[i], StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Matches a normalised value. A regular expression that runs past <see cref="MatchTimeout"/> counts one timeout
    /// and reports no match, so a hostile value costs at most the timeout per rule.
    /// </summary>
    internal bool Matches(ReadOnlySpan<char> value, ref int timeouts)
    {
        if (_prefilter is not null && !value.ContainsAny(_prefilter))
        {
            return false;
        }

        if (_needles is not null)
        {
            return value.ContainsAny(_needles);
        }

        if (_regex is null)
        {
            return false;
        }

        try
        {
            return _regex.IsMatch(value);
        }
        catch (RegexMatchTimeoutException)
        {
            timeouts++;
            return false;
        }
    }

    /// <summary>Applies the rule's own transformations to the raw value and matches the result.</summary>
    internal bool MatchesTransformed(ReadOnlySpan<char> raw, ref int timeouts)
    {
        if (_transforms.Length == 0)
        {
            return Matches(raw, ref timeouts);
        }

        var a = ArrayPool<char>.Shared.Rent(raw.Length);
        var b = ArrayPool<char>.Shared.Rent(raw.Length);
        try
        {
            var current = raw;
            var useA = true;
            foreach (var transform in _transforms)
            {
                var target = useA ? a : b;
                var length = WafText.Apply(transform, current, target);
                current = target.AsSpan(0, length);
                useA = !useA;
            }

            return Matches(current, ref timeouts);
        }
        finally
        {
            ArrayPool<char>.Shared.Return(a);
            ArrayPool<char>.Shared.Return(b);
        }
    }

    /// <summary>Everything needed to build a rule. The builder validates, lints, and compiles it.</summary>
    internal sealed class Spec
    {
        public string? Id { get; init; }
        public string? Category { get; init; }
        public WafSeverity Severity { get; init; } = WafSeverity.Error;
        public WafTargets Targets { get; init; }
        public int Paranoia { get; init; } = 1;
        public WafAction Action { get; init; } = WafAction.Score;
        public int? Score { get; init; }
        public string? Description { get; init; }
        public string[]? HeaderNames { get; init; }
        public WafCheck Check { get; init; }
        public WafTransform[] Transforms { get; init; } = [];
        public string? Regex { get; init; }
        public string[]? Contains { get; init; }
        public string[]? Prefilter { get; init; }
        public bool IgnoreCase { get; init; }
        public bool IsCustom { get; init; }
    }

    /// <summary>Validates a spec and compiles its pattern. Throws <see cref="WafRuleException"/> naming the rule.</summary>
    internal static WafRule Build(Spec spec)
    {
        var id = ValidateSpecIdentity(spec);
        ValidateSpecFields(spec, id);
        var hasRegex = !string.IsNullOrEmpty(spec.Regex);
        var regex = hasRegex ? CompileSpecRegex(spec, id) : null;

        var category = string.IsNullOrWhiteSpace(spec.Category) ? "custom" : spec.Category.Trim();
        return new WafRule(
            id, category, spec.Severity, spec.Targets, spec.Paranoia, spec.Action, spec.Score, spec.Description,
            spec.HeaderNames, spec.Check, spec.Transforms, spec.IsCustom, regex, spec.Contains, spec.Prefilter,
            spec.IgnoreCase, spec.Regex);
    }

    private static string ValidateSpecIdentity(Spec spec)
    {
        var id = spec.Id?.Trim();
        if (string.IsNullOrEmpty(id))
        {
            throw new WafRuleException("A WAF rule has no id.");
        }

        return id;
    }

    private static void ValidateSpecFields(Spec spec, string id)
    {
        if (spec.Targets == WafTargets.None)
        {
            throw new WafRuleException($"WAF rule '{id}' lists no targets.");
        }

        if (spec.Paranoia is < 1 or > 3)
        {
            throw new WafRuleException($"WAF rule '{id}' paranoia must be 1-3.");
        }

        if (spec.Score is < 0 or > 1000)
        {
            throw new WafRuleException($"WAF rule '{id}' score must be 0-1000.");
        }

        var hasRegex = !string.IsNullOrEmpty(spec.Regex);
        var hasContains = spec.Contains is { Length: > 0 };
        var patterns = (hasRegex ? 1 : 0) + (hasContains ? 1 : 0);
        if (spec.Check == WafCheck.None && patterns != 1)
        {
            throw new WafRuleException($"WAF rule '{id}' needs exactly one of regex or contains.");
        }

        if (spec.Check != WafCheck.None && patterns != 0)
        {
            throw new WafRuleException($"WAF rule '{id}' is a built-in check and cannot also have a pattern.");
        }

        if (hasContains && spec.Contains!.Any(string.IsNullOrEmpty))
        {
            throw new WafRuleException($"WAF rule '{id}' has an empty contains entry.");
        }
    }

    private static Regex CompileSpecRegex(Spec spec, string id)
    {
        var problems = WafPatternLinter.Lint(spec.Regex!, spec.IgnoreCase);
        if (problems.Count > 0)
        {
            throw new WafRuleException($"WAF rule '{id}' pattern rejected: {string.Join("; ", problems)}.");
        }

        var regex = WafPatternLinter.Compile(spec.Regex!, spec.IgnoreCase);
        WafPatternLinter.WarmUp(regex);
        return regex;
    }
}

/// <summary>
/// Load-time checks for rule patterns. A pattern runs on attacker-chosen input, so it must compile, must not match
/// the empty string, and must not nest an unbounded repeat inside an unbounded repeat (the shape behind catastrophic
/// backtracking). Compilation prefers the non-backtracking engine and always sets <see cref="WafRule.MatchTimeout"/>.
/// </summary>
public static class WafPatternLinter
{
    public const int MaxPatternLength = 1024;

    /// <summary>Returns the problems of a pattern. An empty list means it may be loaded.</summary>
    public static IReadOnlyList<string> Lint(string pattern, bool ignoreCase = false)
    {
        var problems = new List<string>();
        if (string.IsNullOrWhiteSpace(pattern))
        {
            problems.Add("the pattern is empty");
            return problems;
        }

        if (pattern.Length > MaxPatternLength)
        {
            problems.Add($"the pattern is longer than {MaxPatternLength} characters");
        }

        Regex compiled;
        try
        {
            compiled = Compile(pattern, ignoreCase);
        }
        catch (ArgumentException ex)
        {
            problems.Add("it does not compile (" + ex.Message + ")");
            return problems;
        }

        if (HasNestedUnboundedRepeat(pattern))
        {
            problems.Add("an unbounded repeat is nested inside another unbounded repeat");
        }

        try
        {
            if (compiled.IsMatch(string.Empty))
            {
                problems.Add("it matches the empty string, so it would match every value");
            }
        }
        catch (RegexMatchTimeoutException)
        {
            // An empty input cannot time out in practice. Nothing to report.
        }

        return problems;
    }

    /// <summary>Non-backtracking when the pattern allows it (no look-around or back-references), always with a timeout.</summary>
    internal static Regex Compile(string pattern, bool ignoreCase)
    {
        var options = RegexOptions.CultureInvariant | (ignoreCase ? RegexOptions.IgnoreCase : RegexOptions.None);
        try
        {
            return new Regex(pattern, options | RegexOptions.NonBacktracking, WafRule.MatchTimeout);
        }
        catch (NotSupportedException)
        {
            return new Regex(pattern, options, WafRule.MatchTimeout);
        }
    }

    /// <summary>True when the pattern runs on the non-backtracking engine.</summary>
    internal static bool IsNonBacktracking(Regex regex) => (regex.Options & RegexOptions.NonBacktracking) != 0;

    /// <summary>
    /// Builds the lazy automaton states for ordinary input at load, so the first request does not pay for them
    /// inside the match timeout.
    /// </summary>
    internal static void WarmUp(Regex regex)
    {
        const string sample = "GET /index.html?id=42&name=alice&q=hello+world HTTP/1.1 Mozilla/5.0 (X11; Linux x86_64)";
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                _ = regex.IsMatch(sample);
                return;
            }
            catch (RegexMatchTimeoutException)
            {
                // Keep trying: each call builds more of the automaton.
            }
        }
    }

    /// <summary>
    /// Finds a group that is repeated without a bound and itself holds an unbounded repeat, such as <c>(a+)+</c> or
    /// <c>(x*y)*</c>.
    /// </summary>
    internal static bool HasNestedUnboundedRepeat(string pattern) // NOSONAR S3776 -- Regex surface syntax scanner; splitting cases would obscure repeat nesting detection.
    {
        var stack = new Stack<bool>();
        var current = false;
        var lastWasGroup = false;
        var lastGroupInner = false;
        var i = 0;
        while (i < pattern.Length)
        {
            var c = pattern[i];
            switch (c)
            {
                case '\\':
                    i += 2;
                    lastWasGroup = false;
                    continue;
                case '[':
                    i = SkipCharacterClass(pattern, i);
                    lastWasGroup = false;
                    continue;
                case '(':
                    stack.Push(current);
                    current = false;
                    lastWasGroup = false;
                    i++;
                    if (i < pattern.Length && pattern[i] == '?')
                    {
                        // (?:  (?i)  (?<name>  (?=  ...  The prefix is not a repeat.
                        i++;
                        while (i < pattern.Length && pattern[i] is not (':' or ')' or '>'))
                        {
                            i++;
                        }

                        if (i < pattern.Length && pattern[i] is ':' or '>')
                        {
                            i++;
                        }
                    }

                    continue;
                case ')':
                    lastGroupInner = current;
                    current = stack.Count > 0 && stack.Pop() || current;
                    lastWasGroup = true;
                    i++;
                    continue;
                case '*' or '+':
                    if (lastWasGroup && lastGroupInner)
                    {
                        return true;
                    }

                    current = true;
                    lastWasGroup = false;
                    i++;
                    if (i < pattern.Length && pattern[i] == '?')
                    {
                        i++;
                    }

                    continue;
                case '{':
                    var close = pattern.IndexOf('}', i);
                    if (close < 0)
                    {
                        i++;
                        lastWasGroup = false;
                        continue;
                    }

                    var body = pattern.AsSpan(i + 1, close - i - 1);
                    var comma = body.IndexOf(',');
                    var unbounded = comma >= 0 && comma == body.Length - 1 && comma > 0;
                    if (unbounded)
                    {
                        if (lastWasGroup && lastGroupInner)
                        {
                            return true;
                        }

                        current = true;
                    }

                    lastWasGroup = false;
                    i = close + 1;
                    continue;
                default:
                    lastWasGroup = false;
                    i++;
                    continue;
            }
        }

        return false;
    }

    private static int SkipCharacterClass(string pattern, int start)
    {
        var i = start + 1;
        if (i < pattern.Length && pattern[i] == '^')
        {
            i++;
        }

        if (i < pattern.Length && pattern[i] == ']')
        {
            i++;
        }

        while (i < pattern.Length && pattern[i] != ']')
        {
            i += pattern[i] == '\\' ? 2 : 1;
        }

        return i + 1;
    }
}
