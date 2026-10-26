using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
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
    private static readonly ConcurrentDictionary<string, Window> Windows = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, int> Connections = new(StringComparer.Ordinal);

    internal static TimeProvider Clock { get; set; } = TimeProvider.System;

    internal static void ResetForTests()
    {
        Windows.Clear();
        Connections.Clear();
        _rateWrites = 0;
        Clock = TimeProvider.System;
    }

    private const char HeldSeparator = '\u001f';

    /// <summary>Gives back every slot named in a held-policy value (one name, or several joined by a separator).</summary>
    public static void Release(string policyNames) => ReleaseHeld(policyNames);

    private static void ReleaseHeld(string? policyNames)
    {
        if (string.IsNullOrEmpty(policyNames))
        {
            return;
        }

        foreach (var policyName in policyNames.Split(HeldSeparator))
        {
            Connections.AddOrUpdate(policyName, 0, (_, count) => count > 0 ? count - 1 : 0);
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
            args.ClientRemoteEndPoint?.Address);
        return Apply(args, decision);
    }

    public static bool TryHandleLite(
        ReverseProxyOptions options,
        TransparentBaseProxyEndPoint endPoint,
        Request request,
        IPEndPoint? client,
        out int status,
        out string? body,
        out List<KeyValuePair<string, string>>? headers)
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

        var decision = Evaluate(options, route, request, client?.Address);
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
        IPAddress? client)
    {
        var names = route.Policies;
        var policies = options.Policies;
        if (names is not { Count: > 0 } || policies is not { Count: > 0 })
        {
            return default;
        }

        var decision = default(PolicyDecision);
        string? heldNames = null;
        var method = request.Method;
        var contentLength = request.ContentLength;

        // A later policy can deny after a connection slot was taken. Give those slots back.
        PolicyDecision Deny(PolicyDecision stop)
        {
            ReleaseHeld(heldNames);
            return stop;
        }

        foreach (var name in names)
        {
            RoutePolicyConfig? policy = null;
            for (var i = 0; i < policies.Count; i++)
            {
                if (string.Equals(policies[i].Name, name, StringComparison.Ordinal))
                {
                    policy = policies[i];
                    break;
                }
            }

            if (policy is null)
            {
                continue;
            }

            switch (policy.Kind)
            {
                case "maxRequestBodySize" when policy.MaxRequestBodyBytes is long max && contentLength > max:
                    return Deny(Stop(413, "payload too large"));
                case "requireHeader" when !HeaderSatisfies(HeaderValue(request, policy.HeaderName ?? ""), policy.HeaderValue, valueOptional: true):
                    return Deny(Stop(401, string.Empty));
                case "apiKey" when !HeaderSatisfies(HeaderValue(request, policy.HeaderName ?? ""), policy.HeaderValue, valueOptional: false):
                    return Deny(Stop(401, string.Empty));
                case "timeout" when policy.TimeoutSeconds is int seconds:
                    decision = decision with { TimeoutSeconds = seconds };
                    break;
                case "cors":
                    if (string.Equals(method, "OPTIONS", StringComparison.OrdinalIgnoreCase))
                    {
                        return Deny(new PolicyDecision
                        {
                            Rejected = true,
                            Status = 204,
                            Headers = CorsHeaders(policy, HeaderValue(request, "Origin")),
                        });
                    }

                    break;
                case "rateLimit" when !AllowRate(policy, client):
                    return Deny(Stop(429, string.Empty));
                case "connectionLimit" when !AllowConnection(policy):
                    return Deny(Stop(503, string.Empty));
                case "connectionLimit":
                    heldNames = heldNames is null ? policy.Name : heldNames + HeldSeparator + policy.Name;
                    decision = decision with { Held = heldNames };
                    break;
                case "ipAllowDeny" when !AllowAddress(policy, client):
                    return Deny(Stop(403, string.Empty));
            }
        }

        return decision;
    }

    private static PolicyDecision Stop(int status, string body) => new()
    {
        Rejected = true,
        Status = status,
        Body = body,
    };

    private static bool AllowRate(RoutePolicyConfig policy, IPAddress? client)
    {
        var limit = policy.Limit ?? 1;
        var window = TimeSpan.FromSeconds(policy.WindowSeconds ?? 1);
        var key = policy.Name + "|" + (client?.ToString() ?? "");
        var now = Clock.GetUtcNow();
        var state = Windows.GetOrAdd(key, _ => new Window());
        bool allowed;
        lock (state)
        {
            if (state.Start == default || now - state.Start >= window)
            {
                state.Start = now;
                state.Count = 0;
                state.Span = window;
            }

            state.Count++;
            state.Seen = now;
            allowed = state.Count <= limit;
        }

        // The sweep takes other windows' locks. Run it after this one is released so two writers
        // that sweep at the same time cannot wait on each other.
        SweepIdleWindows(now);
        return allowed;
    }

    private static int _rateWrites;

    internal static int RateWindowCount => Windows.Count;

    private static void SweepIdleWindows(DateTimeOffset now)
    {
        if ((Interlocked.Increment(ref _rateWrites) & 31) != 0)
        {
            return;
        }

        foreach (var pair in Windows)
        {
            var state = pair.Value;
            if (state.Span <= TimeSpan.Zero)
            {
                continue;
            }

            lock (state)
            {
                if (now - state.Seen > state.Span + state.Span)
                {
                    Windows.TryRemove(pair);
                }
            }
        }
    }

    private static bool AllowConnection(RoutePolicyConfig policy)
    {
        var limit = policy.Limit ?? 1;
        while (true)
        {
            var count = Connections.GetOrAdd(policy.Name, 0);
            if (count >= limit)
            {
                return false;
            }

            if (Connections.TryUpdate(policy.Name, count + 1, count))
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

    private sealed class Window
    {
        public DateTimeOffset Start;
        public DateTimeOffset Seen;
        public TimeSpan Span;
        public int Count;
    }

    private readonly struct PolicyDecision
    {
        public bool Rejected { get; init; }
        public int Status { get; init; }
        public string? Body { get; init; }
        public string? Held { get; init; }
        public int? TimeoutSeconds { get; init; }
        public List<KeyValuePair<string, string>>? Headers { get; init; }
    }
}
