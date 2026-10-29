using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Runtime.CompilerServices;
using System.Threading;
using Titanium.Web.Proxy.Abstractions;
using Titanium.Web.Proxy.Abstractions.Network;
using Titanium.Web.Proxy.Extensions;
using Titanium.Web.Proxy.Abstractions.Diagnostics;
using Titanium.Web.Proxy.Abstractions.Routing;
using Titanium.Web.Proxy.EventArguments;
using Titanium.Web.Proxy.Http;
using Titanium.Web.Proxy.Models;

namespace Titanium.Web.Proxy.Routing;

/// <summary>Enforces route policies before a destination is selected. No work when the route lists none.</summary>
internal static class RoutePolicies
{
    /// <summary>
    ///     Counters and slots that belong to one <see cref="ReverseProxyOptions" /> instance, so two proxies in a process
    ///     do not share them and a reload (a new options instance) starts clean.
    /// </summary>
    internal sealed class Runtime
    {
        public RateLimiter Rates { get; } = new();
        public ConcurrentDictionary<string, int> Connections { get; } = new(StringComparer.Ordinal);

        public void Reset()
        {
            Rates.Reset();
            Connections.Clear();
        }
    }

    private static readonly ConditionalWeakTable<Runtime, object> LiveRuntimes = new();

    /// <summary>The enforcement state owned by <paramref name="options" />, created on first use.</summary>
    internal static Runtime RuntimeFor(ReverseProxyOptions options) =>
        options.GetOrCreatePolicyRuntime(static () => Register(new Runtime()));

    private static Runtime Register(Runtime runtime)
    {
        LiveRuntimes.Add(runtime, runtime);
        return runtime;
    }

    internal static TimeProvider Clock { get; set; } = TimeProvider.System;

    /// <summary>Restores the clock and clears the counters of every live options instance.</summary>
    internal static void ResetForTests()
    {
        foreach (var pair in LiveRuntimes)
        {
            pair.Key.Reset();
        }

        Clock = TimeProvider.System;
    }

    private const char HeldSeparator = '\u001f';

    /// <summary>Gives back every slot named in a held-policy value (one name, or several joined by a separator).</summary>
    public static void Release(ReverseProxyOptions options, string policyNames) => ReleaseHeld(RuntimeFor(options), policyNames);

    internal static void ReleaseHeld(Runtime? runtime, string? policyNames)
    {
        if (runtime is null || string.IsNullOrEmpty(policyNames))
        {
            return;
        }

        foreach (var policyName in policyNames.Split(HeldSeparator))
        {
            runtime.Connections.AddOrUpdate(policyName, 0, (_, count) => count > 0 ? count - 1 : 0);
        }
    }

    /// <summary>True when any route lists a policy. Paths that skip the session cannot enforce policies.</summary>
    internal static bool AnyRouteUsesPolicies(ReverseProxyOptions options)
    {
        if (options.Policies is not { Count: > 0 } || options.Routes is not { Count: > 0 } routes)
        {
            return false;
        }

        for (var r = 0; r < routes.Count; r++)
        {
            if (routes[r].Policies is { Count: > 0 })
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    ///     True when the route holds a connection slot for the life of the request. Terminate-lite has no
    ///     end-of-request hook to give the slot back, so such a route must take the session path.
    /// </summary>
    internal static bool HoldsConnectionSlot(ReverseProxyOptions options)
    {
        if (options.Policies is not { Count: > 0 } policies || options.Routes is not { Count: > 0 } routes)
        {
            return false;
        }

        for (var r = 0; r < routes.Count; r++)
        {
            if (routes[r].Policies is not { Count: > 0 } names)
            {
                continue;
            }

            for (var n = 0; n < names.Count; n++)
            {
                for (var p = 0; p < policies.Count; p++)
                {
                    if (string.Equals(policies[p].Name, names[n], StringComparison.Ordinal) &&
                        string.Equals(policies[p].Kind, "connectionLimit", StringComparison.Ordinal))
                    {
                        return true;
                    }
                }
            }
        }

        return false;
    }

    /// <summary>
    ///     True when a route needs the full session path to enforce a policy. An <c>extAuth</c> policy
    ///     awaits an authorization service and edits the upstream request, and an <c>oidc</c> policy awaits a login
    ///     and edits headers and cookies. A route that names a per-route plugin needs the session too. Terminate-lite can do none of these.
    /// </summary>
    internal static bool RequiresSessionPath(ReverseProxyOptions options) => options.HasAsyncPolicies;

    /// <summary>Gives back the connection slot a passed policy check holds, when a later check refuses the request.</summary>
    internal static void ReleaseSessionSlot(SessionEventArgsBase session)
    {
        if (session.Extras is { HeldConnectionPolicy: { } held } extras)
        {
            extras.HeldConnectionPolicy = null;
            ReleaseHeld(extras.HeldConnectionRuntime, held);
            extras.HeldConnectionRuntime = null;
        }
    }

    public static bool TryDeny(ProxyServer server, SessionEventArgsBase session, RouteConfig route)
    {
        if (route.Policies is not { Count: > 0 } || server.ReverseProxy?.Policies is not { Count: > 0 })
        {
            return false;
        }

        if (session is not SessionEventArgs args)
        {
            return false;
        }

        var decision = Evaluate(
            server.ReverseProxy,
            route,
            args.HttpClient.Request,
            args.ClientRemoteEndPoint?.Address,
            server.ForwardedHeaders?.TrustedProxies);
        return Apply(args, decision);
    }

    public static bool TryHandleLite(
        ReverseProxyOptions options,
        TransparentBaseProxyEndPoint endPoint,
        Request request,
        IPEndPoint? client,
        out int status,
        out string? body,
        out List<KeyValuePair<string, string>>? headers,
        IReadOnlyList<string>? trustedProxies = null)
    {
        status = 0;
        body = null;
        headers = null;
        if (options.Policies is not { Count: > 0 } || options.Routes is not { Count: > 0 })
        {
            return false;
        }

        var matcher = options.RouteMatcher ?? new RouteMatcher();
        var path = request.RequestUriString8.GetString();
        var route = matcher.Match(
            new RouteMatchContext(request.Host ?? endPoint.ForwardHost, path, request.Method ?? "GET", null, null),
            options.Routes);
        if (route?.Policies is not { Count: > 0 })
        {
            return false;
        }

        var decision = Evaluate(options, route, request, client?.Address, trustedProxies);
        if (!decision.Rejected)
        {
            return false;
        }

        status = decision.Status;
        body = decision.Body;
        headers = decision.Headers;
        return true;
    }

    private static bool Apply(SessionEventArgs args, PolicyDecision decision)
    {
        if (decision.TimeoutSeconds is int timeout)
        {
            args.RequestTimeout = TimeSpan.FromSeconds(timeout);
        }

        if (decision.Held is not null)
        {
            args.Extras ??= new RoutingFeatureState();
            args.Extras.HeldConnectionPolicy = decision.Held;
            args.Extras.HeldConnectionRuntime = decision.HeldRuntime;
        }

        if (!decision.Rejected)
        {
            return false;
        }

        if (decision.Status != 204)
        {
            args.ForwarderError = ForwarderError.PolicyDenied;
        }

        args.GenericResponse(decision.Body ?? string.Empty, (HttpStatusCode)decision.Status, decision.Headers?.Select(pair => new HttpHeader(pair.Key, pair.Value)));
        return true;
    }

    private static string? HeaderValue(Request request, string name)
    {
        var headers = request.Headers.GetHeaders(name);
        return headers is { Count: > 0 } ? headers[0].Value : null;
    }

    private static PolicyDecision Evaluate(
        ReverseProxyOptions options,
        RouteConfig route,
        Request request,
        IPAddress? client,
        IReadOnlyList<string>? trustedProxies)
    {
        var names = route.Policies;
        var policies = options.Policies;
        if (names is not { Count: > 0 } || policies is not { Count: > 0 })
        {
            return default;
        }

        var decision = default(PolicyDecision);
        string? heldNames = null;
        var runtime = RuntimeFor(options);
        foreach (var name in names)
        {
            var policy = FindNamedPolicy(policies, name);
            if (policy is null)
            {
                continue;
            }

            var stop = ApplyKind(runtime, policy, route, request, client, trustedProxies, ref decision, ref heldNames);
            if (stop is { } denied)
            {
                ReleaseHeld(runtime, heldNames);
                return denied;
            }
        }

        return decision;
    }

    private static RoutePolicyConfig? FindNamedPolicy(IReadOnlyList<RoutePolicyConfig> policies, string name)
    {
        for (var i = 0; i < policies.Count; i++)
        {
            if (string.Equals(policies[i].Name, name, StringComparison.Ordinal))
            {
                return policies[i];
            }
        }

        return null;
    }

    /// <summary>Null means keep evaluating. A decision means stop and give back any held connection slots.</summary>
    private static PolicyDecision? ApplyKind(
        Runtime runtime,
        RoutePolicyConfig policy,
        RouteConfig route,
        Request request,
        IPAddress? client,
        IReadOnlyList<string>? trustedProxies,
        ref PolicyDecision decision,
        ref string? heldNames)
    {
        var method = request.Method;
        var contentLength = request.ContentLength;
        switch (policy.Kind)
        {
            case "maxRequestBodySize" when policy.MaxRequestBodyBytes is long max && contentLength > max:
                return Stop(413, "payload too large");
            case "requireHeader" when !HeaderSatisfies(HeaderValue(request, policy.HeaderName ?? ""), policy.HeaderValue, valueOptional: true):
                return Stop(401, string.Empty);
            case "apiKey" when !HeaderSatisfies(HeaderValue(request, policy.HeaderName ?? ""), policy.HeaderValue, valueOptional: false):
                return Stop(401, string.Empty);
            case "timeout" when policy.TimeoutSeconds is int seconds:
                decision = decision with { TimeoutSeconds = seconds };
                return null;
            case "cors" when string.Equals(method, "OPTIONS", StringComparison.OrdinalIgnoreCase):
                return new PolicyDecision
                {
                    Rejected = true,
                    Status = 204,
                    Headers = CorsHeaders(policy, HeaderValue(request, "Origin")),
                };
            case "rateLimit":
                return CheckRate(runtime, policy, route, request, client, trustedProxies);
            case "connectionLimit" when !AllowConnection(runtime, policy):
                return Stop(503, string.Empty);
            case "connectionLimit":
                heldNames = heldNames is null ? policy.Name : heldNames + HeldSeparator + policy.Name;
                decision = decision with { Held = heldNames, HeldRuntime = runtime };
                return null;
            case "ipAllowDeny" when !AllowAddress(policy, client):
                return Stop(403, string.Empty);
            default:
                return null;
        }
    }

    private static PolicyDecision Stop(int status, string body) => new()
    {
        Rejected = true,
        Status = status,
        Body = body,
    };

    /// <summary>Rate-limit counters held for <paramref name="options" />.</summary>
    internal static int RateWindowCount(ReverseProxyOptions options) => RuntimeFor(options).Rates.Count;

    /// <summary>Null means the request passes. Otherwise a 429 with Retry-After, and RateLimit-* when asked.</summary>
    private static PolicyDecision? CheckRate(
        Runtime runtime,
        RoutePolicyConfig policy,
        RouteConfig route,
        Request request,
        IPAddress? client,
        IReadOnlyList<string>? trustedProxies)
    {
        var verdict = runtime.Rates.Check(policy, route.Id, request, client, trustedProxies, Clock.GetUtcNow());
        if (verdict.Allowed)
        {
            return null;
        }

        var headers = new List<KeyValuePair<string, string>>(4)
        {
            new("Retry-After", verdict.RetryAfterSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture)),
        };
        if (policy.EmitHeaders == true)
        {
            headers.Add(new("RateLimit-Limit", verdict.Limit.ToString(System.Globalization.CultureInfo.InvariantCulture)));
            headers.Add(new("RateLimit-Remaining", verdict.Remaining.ToString(System.Globalization.CultureInfo.InvariantCulture)));
            headers.Add(new("RateLimit-Reset", verdict.ResetSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        }

        return new PolicyDecision
        {
            Rejected = true,
            Status = 429,
            Body = string.Empty,
            Headers = headers,
        };
    }

    private static bool AllowConnection(Runtime runtime, RoutePolicyConfig policy)
    {
        var limit = policy.Limit ?? 1;
        var connections = runtime.Connections;
        while (true)
        {
            var count = connections.GetOrAdd(policy.Name, 0);
            if (count >= limit)
            {
                return false;
            }

            if (connections.TryUpdate(policy.Name, count + 1, count))
            {
                return true;
            }
        }
    }

    private static bool AllowAddress(RoutePolicyConfig policy, IPAddress? client)
    {
        if (client is null)
        {
            return string.IsNullOrEmpty(policy.Allow);
        }

        var rules = AddressRuleCache.GetValue(policy, static p => new AddressRules(p.Allow, p.Deny));
        if (rules.Deny.Length > 0 && MatchesAny(client, rules.Deny))
        {
            return false;
        }

        // An allow list that parses to nothing admits nobody. It never falls open.
        if (rules.HasAllow)
        {
            return MatchesAny(client, rules.Allow);
        }

        return true;
    }

    private static readonly ConditionalWeakTable<RoutePolicyConfig, AddressRules> AddressRuleCache = new();

    /// <summary>Parsed once per policy. IPv4 and IPv6 prefixes, and IPv4-mapped IPv6 clients, all match.</summary>
    private sealed class AddressRules(string? allow, string? deny)
    {
        public bool HasAllow { get; } = !string.IsNullOrWhiteSpace(allow);
        public CidrPrefix[] Allow { get; } = Parse(allow);
        public CidrPrefix[] Deny { get; } = Parse(deny);

        private static CidrPrefix[] Parse(string? csv)
        {
            if (string.IsNullOrWhiteSpace(csv))
            {
                return [];
            }

            var parts = csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var list = new List<CidrPrefix>(parts.Length);
            foreach (var part in parts)
            {
                if (CidrPrefix.TryParse(part, out var prefix) && prefix is not null)
                {
                    list.Add(prefix);
                }
            }

            return [.. list];
        }
    }

    private static bool MatchesAny(IPAddress client, CidrPrefix[] prefixes)
    {
        for (var i = 0; i < prefixes.Length; i++)
        {
            if (prefixes[i].Contains(client))
            {
                return true;
            }
        }

        return false;
    }

    private static List<KeyValuePair<string, string>> CorsHeaders(RoutePolicyConfig policy, string? origin)
    {
        var allowOrigin = policy.Origins ?? "*";
        if (allowOrigin != "*" && origin is not null && !allowOrigin.Split(',', StringSplitOptions.TrimEntries).Contains(origin, StringComparer.Ordinal))
        {
            allowOrigin = allowOrigin.Split(',', StringSplitOptions.TrimEntries)[0];
        }
        else if (allowOrigin != "*" && origin is not null)
        {
            allowOrigin = origin;
        }

        return
        [
            new("Access-Control-Allow-Origin", allowOrigin),
            new("Vary", "Origin"),
            new("Access-Control-Allow-Methods", policy.Methods ?? "GET, POST, OPTIONS"),
            new("Access-Control-Allow-Headers", policy.HeaderName ?? "*"),
        ];
    }

    /// <summary>
    /// A header policy fails closed. A missing header never satisfies it. With no configured value,
    /// <c>requireHeader</c> accepts any non-empty header and <c>apiKey</c> accepts nothing.
    /// </summary>
    private static bool HeaderSatisfies(string? actual, string? expected, bool valueOptional)
    {
        if (string.IsNullOrEmpty(actual))
        {
            return false;
        }

        if (string.IsNullOrEmpty(expected))
        {
            return valueOptional;
        }

        return FixedEquals(actual, expected);
    }

    private static bool FixedEquals(string? left, string? right)
    {
        var a = SHA256.HashData(Encoding.UTF8.GetBytes(left ?? string.Empty));
        var b = SHA256.HashData(Encoding.UTF8.GetBytes(right ?? string.Empty));
        return CryptographicOperations.FixedTimeEquals(a, b);
    }

    private readonly struct PolicyDecision
    {
        public bool Rejected { get; init; }
        public int Status { get; init; }
        public string? Body { get; init; }
        public string? Held { get; init; }
        public Runtime? HeldRuntime { get; init; }
        public int? TimeoutSeconds { get; init; }
        public List<KeyValuePair<string, string>>? Headers { get; init; }
    }
}
