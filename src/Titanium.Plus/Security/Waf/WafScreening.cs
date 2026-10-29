using System.Collections.Concurrent;
using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.Extensions.Logging;
using Titanium.Web.Proxy.Abstractions.Middleware;
using Titanium.Web.Proxy.Abstractions.Routing;
using Titanium.Web.Proxy.EventArguments;
using Titanium.Web.Proxy.Http;

namespace Titanium.Plus.Security.Waf;

/// <summary>The rule pack, custom rules, and cached profiles behind a screening middleware.</summary>
internal sealed class WafEngine
{
    public WafEngine(WafOptions options, IReadOnlyList<WafRule>? customRules = null)
    {
        Options = options;
        var rules = new List<WafRule>(WafRulePack.Rules);
        if (customRules is { Count: > 0 })
        {
            rules.AddRange(customRules);
        }

        Rules = rules;
        Profiles = new WafProfiles(options, rules);
    }

    public WafOptions Options { get; }

    public IReadOnlyList<WafRule> Rules { get; }

    public WafProfiles Profiles { get; }

    public WafProfile DefaultProfile => Profiles.Default;

    /// <summary>Loads <c>waf.rulesFile</c> (when set) and builds the engine. Throws when the file cannot be used.</summary>
    public static WafEngine Create(WafOptions options)
    {
        IReadOnlyList<WafRule> custom = [];
        if (options.RulesFile is { } path)
        {
            if (!File.Exists(path))
            {
                throw new WafRuleException($"waf.rulesFile '{path}' does not exist.");
            }

            custom = WafCustomRules.LoadFile(path);
        }

        return new WafEngine(options, custom);
    }

    /// <summary>Screens one request. Allocates nothing when no rule matches.</summary>
    public WafVerdict Inspect(in WafRequest request, WafProfile profile) =>
        profile.Mode == WafMode.Off
            ? WafVerdict.Clean(WafMode.Off)
            : WafScanner.ForThread().Run(Options, profile, request);
}

/// <summary>Counters of the WAF, exposed on the Prometheus scrape. Safe to update from any thread.</summary>
public sealed class WafStats
{
    private readonly ConcurrentDictionary<string, StrongBox<long>> _categories = new(StringComparer.Ordinal);
    private readonly long[] _bodyOutcomes = new long[Enum.GetValues<WafBodyOutcome>().Length];
    private long _detectRequests;
    private long _blockRequests;
    private long _wouldBlock;
    private long _blocked;
    private long _timeouts;

    /// <summary>Requests screened in the given mode.</summary>
    public long Requests(WafMode mode) =>
        mode == WafMode.Detect ? Volatile.Read(ref _detectRequests) : mode == WafMode.Block ? Volatile.Read(ref _blockRequests) : 0;

    /// <summary>Requests whose score reached the threshold, in detect or block mode.</summary>
    public long WouldBlock => Volatile.Read(ref _wouldBlock);

    /// <summary>Requests refused with 403.</summary>
    public long Blocked => Volatile.Read(ref _blocked);

    public long RuleTimeouts => Volatile.Read(ref _timeouts);

    /// <summary>Requests with at least one match in the category.</summary>
    public long Detections(string category) => _categories.TryGetValue(category, out var box) ? Volatile.Read(ref box.Value) : 0;

    /// <summary>Requests whose body was handled as <paramref name="outcome"/>.</summary>
    public long Bodies(WafBodyOutcome outcome) => Volatile.Read(ref _bodyOutcomes[(int)outcome]);

    internal void Record(in WafVerdict verdict, WafBodyOutcome body)
    {
        if (verdict.Mode == WafMode.Detect)
        {
            Interlocked.Increment(ref _detectRequests);
        }
        else if (verdict.Mode == WafMode.Block)
        {
            Interlocked.Increment(ref _blockRequests);
        }

        Interlocked.Increment(ref _bodyOutcomes[(int)body]);
        if (verdict.RuleTimeouts > 0)
        {
            Interlocked.Add(ref _timeouts, verdict.RuleTimeouts);
        }

        if (verdict.WouldBlock)
        {
            Interlocked.Increment(ref _wouldBlock);
        }

        if (verdict.Blocked)
        {
            Interlocked.Increment(ref _blocked);
        }

        var matches = verdict.Matches;
        for (var i = 0; i < matches.Count; i++)
        {
            var category = matches[i].Category;
            var first = true;
            for (var j = 0; j < i; j++)
            {
                first &= !string.Equals(matches[j].Category, category, StringComparison.Ordinal);
            }

            if (first)
            {
                Interlocked.Increment(ref _categories.GetOrAdd(category, static _ => new StrongBox<long>()).Value);
            }
        }
    }

    /// <summary>Appends the WAF series to a Prometheus text exposition.</summary>
    public void AppendTo(StringBuilder sb, int ruleCount)
    {
        sb.AppendLine("# HELP titanium_waf_info Rule pack in use.");
        sb.AppendLine("# TYPE titanium_waf_info gauge");
        sb.Append("titanium_waf_info{pack=\"").Append(WafRulePack.Version).Append("\"} ").Append(ruleCount).AppendLine();
        sb.AppendLine("# HELP titanium_waf_requests_total Requests screened by the WAF, by mode.");
        sb.AppendLine("# TYPE titanium_waf_requests_total counter");
        sb.Append("titanium_waf_requests_total{mode=\"detect\"} ").Append(Requests(WafMode.Detect)).AppendLine();
        sb.Append("titanium_waf_requests_total{mode=\"block\"} ").Append(Requests(WafMode.Block)).AppendLine();
        sb.AppendLine("# HELP titanium_waf_would_block_total Requests whose anomaly score reached the block threshold.");
        sb.AppendLine("# TYPE titanium_waf_would_block_total counter");
        sb.Append("titanium_waf_would_block_total ").Append(WouldBlock).AppendLine();
        sb.AppendLine("# HELP titanium_waf_blocked_total Requests refused with 403 by the WAF.");
        sb.AppendLine("# TYPE titanium_waf_blocked_total counter");
        sb.Append("titanium_waf_blocked_total ").Append(Blocked).AppendLine();
        sb.AppendLine("# HELP titanium_waf_detections_total Requests with at least one rule match, by rule category.");
        sb.AppendLine("# TYPE titanium_waf_detections_total counter");
        foreach (var pair in _categories.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            sb.Append("titanium_waf_detections_total{category=\"").Append(pair.Key).Append("\"} ")
                .Append(Volatile.Read(ref pair.Value.Value)).AppendLine();
        }

        sb.AppendLine("# HELP titanium_waf_bodies_total Request bodies by how the WAF handled them.");
        sb.AppendLine("# TYPE titanium_waf_bodies_total counter");
        foreach (var outcome in Enum.GetValues<WafBodyOutcome>())
        {
            sb.Append("titanium_waf_bodies_total{outcome=\"").Append(outcome.ToString().ToLowerInvariant()).Append("\"} ")
                .Append(Bodies(outcome)).AppendLine();
        }

        sb.AppendLine("# HELP titanium_waf_rule_timeouts_total Rule matches stopped by the match timeout.");
        sb.AppendLine("# TYPE titanium_waf_rule_timeouts_total counter");
        sb.Append("titanium_waf_rule_timeouts_total ").Append(RuleTimeouts).AppendLine();
    }
}

/// <summary>
/// Signature-based request screening. Runs in <c>BeforeRequest</c> on the session path (it reads headers, cookies,
/// and small bodies), applies the profile of the route's <c>waf</c> policy when there is one, scores the matches,
/// and refuses the request with 403 only in block mode.
/// </summary>
public sealed class WafScreeningMiddleware : IProxyMiddleware, IRequiresSessionPathMiddleware
{
    private static readonly string[] ViewHeaders = ["User-Agent", "Referer", "Cookie", "Origin", "X-Forwarded-Host", "X-Original-URL", "X-Rewrite-URL"];

    private readonly WafEngine _engine;
    private readonly Dictionary<string, RoutePolicyConfig> _policies = new(StringComparer.Ordinal);
    private readonly ILogger? _logger;

    public WafScreeningMiddleware(
        WafOptions options,
        IReadOnlyList<WafRule>? customRules = null,
        IReadOnlyList<RoutePolicyConfig>? policies = null,
        ILogger? logger = null)
        : this(new WafEngine(options, customRules), policies, logger)
    {
    }

    internal WafScreeningMiddleware(WafEngine engine, IReadOnlyList<RoutePolicyConfig>? policies, ILogger? logger)
    {
        _engine = engine;
        _logger = logger;
        foreach (var policy in policies ?? [])
        {
            if (string.Equals(policy.Kind, "waf", StringComparison.Ordinal))
            {
                // Build now so a bad policy fails at startup, not on the first request that reaches its route.
                _ = engine.Profiles.ForPolicy(policy);
                _policies[policy.Name] = policy;
            }
        }
    }

    public WafStats Stats { get; } = new();

    /// <summary>True when at least one <c>waf</c> policy exists, so a request is matched to its route.</summary>
    public bool HasRoutePolicies => _policies.Count > 0;

    internal WafEngine Engine => _engine;

    /// <inheritdoc />
    public async ValueTask InvokeAsync(
        ProxyMiddlewareContext context, ProxyMiddlewareDelegate next, CancellationToken cancellationToken)
    {
        if (context.Session is SessionEventArgs session)
        {
            if (await ScreenSessionAsync(context, session, cancellationToken).ConfigureAwait(false))
            {
                return;
            }
        }
        else if (context.Request is { } view && ScreenView(context, view))
        {
            return;
        }

        await next(context, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Returns true when the request was refused.</summary>
    private async ValueTask<bool> ScreenSessionAsync(
        ProxyMiddlewareContext context, SessionEventArgs session, CancellationToken cancellationToken)
    {
        var profile = ResolveProfile(session);
        if (profile.Mode == WafMode.Off)
        {
            return false;
        }

        var request = session.HttpClient.Request;
        var outcome = DecideBody(session, request);
        var body = ReadOnlyMemory<byte>.Empty;
        if (outcome == WafBodyOutcome.Inspected)
        {
            try
            {
                body = await session.GetRequestBody(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is InvalidOperationException or IOException)
            {
                // The body cannot be read here (already streamed, or a tunnel). Screen the rest of the request.
                outcome = WafBodyOutcome.NotEligible;
                _logger?.LogDebug("Plus WAF: request body not read: {Reason}", ex.Message);
            }
        }

        var verdict = _engine.Inspect(
            new WafRequest
            {
                Target = request.RequestUriString,
                Headers = request.Headers,
                Body = body,
                ContentType = request.ContentType,
            },
            profile);
        return Conclude(context, verdict, outcome, request.Method, request.RequestUriString);
    }

    private bool ScreenView(ProxyMiddlewareContext context, MiddlewareRequestView view)
    {
        var profile = _engine.DefaultProfile;
        if (profile.Mode == WafMode.Off)
        {
            return false;
        }

        HeaderCollection? headers = null;
        if (view.GetHeaderValues is { } lookup)
        {
            foreach (var name in ViewHeaders)
            {
                if (lookup(name) is not { } values)
                {
                    continue;
                }

                headers ??= new HeaderCollection();
                foreach (var value in values)
                {
                    headers.AddHeader(name, value);
                }
            }
        }

        var verdict = _engine.Inspect(new WafRequest { Target = view.Path, Headers = headers }, profile);
        return Conclude(context, verdict, WafBodyOutcome.None, view.Method, view.Path);
    }

    private bool Conclude(
        ProxyMiddlewareContext context, in WafVerdict verdict, WafBodyOutcome outcome, string? method, string? target)
    {
        Stats.Record(verdict, outcome);
        if (verdict.Matches.Count > 0)
        {
            Log(verdict, method, target);
        }

        if (!verdict.Blocked)
        {
            return false;
        }

        CidrAccessMiddleware.Deny(context, HttpStatusCode.Forbidden, "forbidden");
        return true;
    }

    private WafProfile ResolveProfile(SessionEventArgs session)
    {
        if (_policies.Count == 0)
        {
            return _engine.DefaultProfile;
        }

        if (session.PeekRoute() is { Policies: { Count: > 0 } names })
        {
            for (var i = 0; i < names.Count; i++)
            {
                if (_policies.TryGetValue(names[i], out var policy))
                {
                    return _engine.Profiles.ForPolicy(policy);
                }
            }
        }

        return _engine.DefaultProfile;
    }

    private WafBodyOutcome DecideBody(SessionEventArgs session, Request request)
    {
        var limit = _engine.Options.InspectBodyBytes;
        if (request.IsBodyRead)
        {
            // An earlier hook already buffered it. Use what is there, which is also what the origin will get.
            var length = request.Body.Length;
            return WafBodyPolicy.Decide(length > 0, length, request.ContentType, null, limit);
        }

        var outcome = WafBodyPolicy.Decide(request.HasBody, request.ContentLength, request.ContentType, request.ContentEncoding, limit);
        return outcome == WafBodyOutcome.Inspected && !session.CanBufferBody(request) ? WafBodyOutcome.TooLarge : outcome;
    }

    private void Log(in WafVerdict verdict, string? method, string? target)
    {
        if (_logger is null)
        {
            return;
        }

        var path = target ?? string.Empty;
        var cut = path.IndexOfAny(['?', '#']);
        if (cut >= 0)
        {
            path = path[..cut];
        }

        if (path.Length > 200)
        {
            path = path[..200];
        }

        var rules = string.Join(',', verdict.Matches.Select(m => m.RuleId));
        var action = verdict.Blocked ? "blocked" : verdict.WouldBlock ? "would block" : "noted";
        _logger.LogWarning(
            "Plus WAF {Action} ({Mode}): score={Score} rules={Rules} {Method} {Path}",
            action, verdict.Mode.ToString().ToLowerInvariant(), verdict.Score, rules, method, path);
    }
}
