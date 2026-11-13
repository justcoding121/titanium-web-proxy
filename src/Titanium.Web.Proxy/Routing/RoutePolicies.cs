using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
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
using Titanium.Web.Proxy.Transforms;

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
        public RateLimiter Rates { get; }
        public ConcurrentDictionary<string, int> Connections { get; } = new(StringComparer.Ordinal);

        public Runtime(IFleetRateStore? fleetStore = null) => Rates = new RateLimiter(fleetStore);

        public void Reset()
        {
            Rates.Reset();
            Connections.Clear();
        }
    }

    private static readonly ConditionalWeakTable<Runtime, object> LiveRuntimes = new();

    /// <summary>The enforcement state owned by <paramref name="options" />, created on first use.</summary>
    internal static Runtime RuntimeFor(ReverseProxyOptions options) =>
        options.GetOrCreatePolicyRuntime(() => Register(new Runtime(options.FleetRateStore)));

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

    /// <summary>Separates a policy name from a per-client or per-route slot. Not the held-list separator.</summary>
    private const char SlotSeparator = '\u001e';

    /// <summary>Gives back every slot named in a held-policy value (one name, or several joined by a separator).</summary>
    public static void Release(ReverseProxyOptions options, string policyNames) => ReleaseHeld(RuntimeFor(options), policyNames);

    internal static void ReleaseHeld(Runtime? runtime, string? policyNames)
    {
        if (runtime is null || string.IsNullOrEmpty(policyNames))
        {
            return;
        }

        foreach (var slot in policyNames.Split(HeldSeparator))
        {
            var left = runtime.Connections.AddOrUpdate(slot, 0, static (_, count) => count > 0 ? count - 1 : 0);
            if (left == 0)
            {
                // Drops the entry only while it is still zero, so a concurrent acquire is left in place.
                ((ICollection<KeyValuePair<string, int>>)runtime.Connections).Remove(new KeyValuePair<string, int>(slot, 0));
            }
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

    public static bool TryHandleLite( // NOSONAR S107 -- Lite path keeps policy inputs and rejection outputs explicit for hot terminate paths.
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

            var stop = ApplyKind(options, runtime, policy, route, request, client, trustedProxies, ref decision, ref heldNames);
            if (stop is { } denied)
            {
                ReleaseHeld(runtime, heldNames);
                // A preflight already carries its own CORS headers. Any other local refusal inherits the route's cors policy.
                return denied.Status == 204 ? denied : WithLocalCors(options, route, request, denied);
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
    private static PolicyDecision? ApplyKind( // NOSONAR S107 -- Policy kind dispatch shares mutable held-slot state; a bag type would obscure ref semantics.
        ReverseProxyOptions options,
        Runtime runtime,
        RoutePolicyConfig policy,
        RouteConfig route,
        Request request,
        IPAddress? client,
        IReadOnlyList<string>? trustedProxies,
        ref PolicyDecision decision,
        ref string? heldNames)
    {
        var contentLength = request.ContentLength;
        switch (policy.Kind)
        {
            case "maxRequestBodySize" when policy.MaxRequestBodyBytes is long max && contentLength > max:
                return Stop(413, "payload too large");
            case "requireHeader" when IsRealPreflight(request) && FindCorsPolicy(options, route) is not null:
                return null;
            case "requireHeader" when !HeaderSatisfies(HeaderValue(request, policy.HeaderName ?? ""), policy.HeaderValue, valueOptional: true):
                return Stop(401, string.Empty);
            case "apiKey" when IsRealPreflight(request) && FindCorsPolicy(options, route) is not null:
                return null;
            case "apiKey" when !HeaderSatisfies(HeaderValue(request, policy.HeaderName ?? ""), policy.HeaderValue, valueOptional: false):
                return Stop(401, string.Empty);
            case "timeout" when policy.TimeoutSeconds is int seconds:
                decision = decision with { TimeoutSeconds = seconds };
                return null;
            case "cors":
                return CorsPreflight(policy, request);
            case "rateLimit":
                return CheckRate(runtime, policy, route, request, client, trustedProxies);
            case "httpsRedirect":
                return HttpsRedirect(policy, request, client, trustedProxies);
            case "connectionLimit" when !AllowConnection(runtime, policy, route, request, client, trustedProxies):
                return Stop(503, string.Empty);
            case "connectionLimit":
                var slot = ConnectionSlotName(policy, route, request, client, trustedProxies);
                heldNames = heldNames is null ? slot : heldNames + HeldSeparator + slot;
                decision = decision with { Held = heldNames, HeldRuntime = runtime };
                return null;
            case "ipAllowDeny" when !AllowAddress(policy, client):
                return Stop(403, string.Empty);
            case "geo" when !AllowCountry(options.GeoIpResolver, policy, client):
                return Stop(403, string.Empty);
            default:
                return null;
        }
    }

    /// <summary>
    ///     Redirects a cleartext request. An https request passes, and so does a cleartext request whose
    ///     socket peer is a trusted proxy and whose <c>X-Forwarded-Proto</c> is <c>https</c>.
    /// </summary>
    private static PolicyDecision? HttpsRedirect(
        RoutePolicyConfig policy,
        Request request,
        IPAddress? client,
        IReadOnlyList<string>? trustedProxies)
    {
        if (request.IsHttps)
        {
            return null;
        }

        if (client is not null &&
            ForwardedHeadersApplier.IsTrusted(client, trustedProxies) &&
            string.Equals(HeaderValue(request, "X-Forwarded-Proto"), "https", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (!TryHttpsLocation(request, policy.Port, out var location))
        {
            return Stop(400, string.Empty);
        }

        return new PolicyDecision
        {
            Rejected = true,
            Status = policy.RedirectStatus ?? 308,
            Body = string.Empty,
            Headers = [new KeyValuePair<string, string>("Location", location)],
        };
    }

    private static bool TryHttpsLocation(Request request, int? port, out string location)
    {
        location = string.Empty;
        if (!TryHostWithoutPort(request.Host, out var host))
        {
            return false;
        }

        var suffix = port is int configured && configured != 443
            ? ":" + configured.ToString(CultureInfo.InvariantCulture)
            : string.Empty;
        location = "https://" + host + suffix + RequestPathAndQuery(request);
        return true;
    }

    private static string RequestPathAndQuery(Request request)
    {
        var raw = request.RequestUriString8.GetString();
        if (string.IsNullOrEmpty(raw))
        {
            return "/";
        }

        if (raw[0] == '/')
        {
            return raw;
        }

        var scheme = raw.IndexOf("://", StringComparison.Ordinal);
        if (scheme >= 0)
        {
            var slash = raw.IndexOf('/', scheme + 3);
            return slash >= 0 ? raw[slash..] : "/";
        }

        return "/";
    }

    /// <summary>Host is letters, digits, hyphen, and dot, or an IPv6 address in brackets, with an optional port.</summary>
    private static bool TryHostWithoutPort(string? value, out string host)
    {
        host = string.Empty;
        if (string.IsNullOrEmpty(value) || value.Contains(' ') || value.Contains('@') || value.Contains('/'))
        {
            return false;
        }

        if (value[0] == '[')
        {
            var end = value.IndexOf(']');
            if (end <= 1 || value.AsSpan(1, end - 1).IndexOfAnyExcept("0123456789abcdefABCDEF:.") >= 0)
            {
                return false;
            }

            host = value[..(end + 1)];
            var rest = value[(end + 1)..];
            return rest.Length == 0 || (rest[0] == ':' && IsPort(rest[1..]));
        }

        var colon = value.LastIndexOf(':');
        if (colon > 0 && value.IndexOf(':') == colon)
        {
            host = value[..colon];
            return IsHostName(host) && IsPort(value[(colon + 1)..]);
        }

        host = value;
        return IsHostName(host);
    }

    private static bool IsPort(string text) =>
        ushort.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var port) && port > 0;

    private static bool IsHostName(string name)
    {
        if (name.Length is 0 or > 253 || name[0] is '.' or '-' || name[^1] is '.' or '-')
        {
            return false;
        }

        foreach (var c in name)
        {
            if (!(char.IsAsciiLetterOrDigit(c) || c is '.' or '-'))
            {
                return false;
            }
        }

        return true;
    }

    private static PolicyDecision Stop(int status, string body) => new()
    {
        Rejected = true,
        Status = status,
        Body = body,
    };

    /// <summary>Rate-limit counters held for <paramref name="options" />.</summary>
    internal static int RateWindowCount(ReverseProxyOptions options) => RuntimeFor(options).Rates.Count;

    /// <summary>Connection-limit counters held for <paramref name="options" />. Zero after every slot is released.</summary>
    internal static int ConnectionSlotCount(ReverseProxyOptions options) => RuntimeFor(options).Connections.Count;

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

    private static bool AllowConnection(
        Runtime runtime,
        RoutePolicyConfig policy,
        RouteConfig route,
        Request request,
        IPAddress? client,
        IReadOnlyList<string>? trustedProxies)
    {
        var limit = policy.Limit ?? 1;
        var slot = ConnectionSlotName(policy, route, request, client, trustedProxies);
        var connections = runtime.Connections;
        while (true)
        {
            var count = connections.GetOrAdd(slot, 0);
            if (count >= limit)
            {
                return false;
            }

            if (connections.TryUpdate(slot, count + 1, count))
            {
                return true;
            }
        }
    }

    /// <summary>
    ///     Unset <see cref="RoutePolicyConfig.Key"/> is one counter named for the policy.
    ///     <c>client</c> and <c>forwardedClient</c> append the address; <c>route</c> appends the route id.
    ///     A value that contains a separator is hashed so release cannot split the wrong slot.
    /// </summary>
    internal static string ConnectionSlotName(
        RoutePolicyConfig policy,
        RouteConfig route,
        Request request,
        IPAddress? client,
        IReadOnlyList<string>? trustedProxies)
    {
        if (string.IsNullOrEmpty(policy.Key))
        {
            return policy.Name;
        }

        string value;
        if (policy.Key.Equals("route", StringComparison.OrdinalIgnoreCase))
        {
            value = route.Id;
        }
        else if (policy.Key.Equals("forwardedClient", StringComparison.OrdinalIgnoreCase))
        {
            value = RateLimiter.ResolveForwardedClient(request, client, trustedProxies)?.ToString() ?? string.Empty;
        }
        else
        {
            value = client?.ToString() ?? string.Empty;
        }

        if (value.Contains(HeldSeparator) || value.Contains(SlotSeparator))
        {
            value = RateLimiter.HashValue(value);
        }

        return policy.Name + SlotSeparator + value;
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

    private static bool AllowCountry(IGeoIpResolver? resolver, RoutePolicyConfig policy, IPAddress? client)
    {
        if (client is null)
        {
            return string.IsNullOrEmpty(policy.Allow);
        }

        var rules = CountryRuleCache.GetValue(policy, static p => new CountryRules(p.Allow, p.Deny));
        string? country = null;
        if (resolver?.TryGetCountry(client, out var code) == true && !string.IsNullOrEmpty(code))
        {
            country = code;
        }

        if (country is not null)
        {
            if (rules.Deny.Contains(country))
            {
                return false;
            }

            if (rules.HasAllow)
            {
                return rules.Allow.Contains(country);
            }

            return true;
        }

        return !rules.HasAllow;
    }

    private static readonly ConditionalWeakTable<RoutePolicyConfig, CountryRules> CountryRuleCache = new();

    private sealed class CountryRules(string? allow, string? deny)
    {
        public bool HasAllow { get; } = !string.IsNullOrWhiteSpace(allow);
        public HashSet<string> Allow { get; } = Parse(allow);
        public HashSet<string> Deny { get; } = Parse(deny);

        private static HashSet<string> Parse(string? csv)
        {
            if (string.IsNullOrWhiteSpace(csv))
            {
                return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            }

            var parts = csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var set = new HashSet<string>(parts.Length, StringComparer.OrdinalIgnoreCase);
            foreach (var part in parts)
            {
                set.Add(part.ToUpperInvariant());
            }

            return set;
        }
    }

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

    /// <summary>
    /// A preflight is <c>OPTIONS</c> with both <c>Origin</c> and <c>Access-Control-Request-Method</c>.
    /// Anything else, including a plain <c>OPTIONS</c>, is left for the origin.
    /// </summary>
    private static PolicyDecision? CorsPreflight(RoutePolicyConfig policy, Request request)
    {
        if (!string.Equals(request.Method, "OPTIONS", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var origin = HeaderValue(request, "Origin");
        var requestMethod = HeaderValue(request, "Access-Control-Request-Method");
        if (string.IsNullOrEmpty(origin) || string.IsNullOrEmpty(requestMethod))
        {
            return null;
        }

        return new PolicyDecision
        {
            Rejected = true,
            Status = 204,
            Headers = CorsHeaders(policy, request, origin, HeaderValue(request, "Access-Control-Request-Headers")),
        };
    }

    private static List<KeyValuePair<string, string>> CorsHeaders(RoutePolicyConfig policy, Request request, string origin, string? requestHeaders)
    {
        var headers = new List<KeyValuePair<string, string>>
        {
            new("Vary", "Origin, Access-Control-Request-Method, Access-Control-Request-Headers"),
        };

        var allowOrigin = AllowedOrigin(policy.Origins, origin);
        if (allowOrigin is null)
        {
            return headers;
        }

        headers.Add(new("Access-Control-Allow-Origin", allowOrigin));
        headers.Add(new("Access-Control-Allow-Methods", string.IsNullOrWhiteSpace(policy.Methods) ? "GET, POST, OPTIONS" : policy.Methods));
        if (AllowHeaders(policy, requestHeaders) is { } allowHeaders)
        {
            headers.Add(new("Access-Control-Allow-Headers", allowHeaders));
        }

        if (policy.AllowCredentials == true)
        {
            headers.Add(new("Access-Control-Allow-Credentials", "true"));
        }

        if (policy.MaxAgeSeconds is int maxAge)
        {
            headers.Add(new("Access-Control-Max-Age", maxAge.ToString(CultureInfo.InvariantCulture)));
        }

        if (policy.AllowPrivateNetwork == true &&
            string.Equals(HeaderValue(request, "Access-Control-Request-Private-Network"), "true", StringComparison.OrdinalIgnoreCase))
        {
            headers.Add(new("Access-Control-Allow-Private-Network", "true"));
            headers[0] = new KeyValuePair<string, string>(
                headers[0].Key,
                headers[0].Value + ", Access-Control-Request-Private-Network");
        }

        return headers;
    }

    /// <summary>
    /// Headers for a proxy-generated response on a route that lists <c>cors</c>.
    /// Null when the route has no such policy, the request has no <c>Origin</c>, or the origin is not allowed.
    /// </summary>
    internal static List<KeyValuePair<string, string>>? TryGetCorsResponseHeaders(
        RouteConfig route, Request request, ReverseProxyOptions options)
    {
        var policy = FindCorsPolicy(options, route);
        if (policy is null)
        {
            return null;
        }

        var origin = HeaderValue(request, "Origin");
        if (string.IsNullOrEmpty(origin))
        {
            return null;
        }

        var allowOrigin = AllowedOrigin(policy.Origins, origin);
        if (allowOrigin is null)
        {
            return null;
        }

        var headers = new List<KeyValuePair<string, string>>(4)
        {
            new("Access-Control-Allow-Origin", allowOrigin),
        };
        if (policy.AllowCredentials == true && !string.Equals(allowOrigin, "*", StringComparison.Ordinal))
        {
            headers.Add(new("Access-Control-Allow-Credentials", "true"));
        }

        if (!string.IsNullOrWhiteSpace(policy.ExposeHeaders))
        {
            headers.Add(new("Access-Control-Expose-Headers", policy.ExposeHeaders.Trim()));
        }

        // A wildcard answer does not vary. A list does, so caches do not reuse one origin's reply for another.
        var configured = policy.Origins?.Trim();
        if (!string.IsNullOrWhiteSpace(configured) && !string.Equals(configured, "*", StringComparison.Ordinal))
        {
            headers.Add(new("Vary", "Origin"));
        }

        return headers;
    }

    /// <summary>Writes <see cref="TryGetCorsResponseHeaders"/> onto a response, merging an existing <c>Vary</c>.</summary>
    internal static void ApplyLocalCors(Response response, RouteConfig route, Request request, ReverseProxyOptions options)
    {
        var extra = TryGetCorsResponseHeaders(route, request, options);
        if (extra is null)
        {
            return;
        }

        for (var i = 0; i < extra.Count; i++)
        {
            AddMerged(response.Headers, extra[i]);
        }
    }

    private static PolicyDecision WithLocalCors(
        ReverseProxyOptions options, RouteConfig route, Request request, PolicyDecision denied)
    {
        var extra = TryGetCorsResponseHeaders(route, request, options);
        if (extra is null)
        {
            return denied;
        }

        if (denied.Headers is not { Count: > 0 })
        {
            return denied with { Headers = extra };
        }

        var merged = new List<KeyValuePair<string, string>>(denied.Headers);
        for (var i = 0; i < extra.Count; i++)
        {
            merged = MergeHeader(merged, extra[i]);
        }

        return denied with { Headers = merged };
    }

    private static List<KeyValuePair<string, string>> MergeHeader(
        List<KeyValuePair<string, string>> headers, KeyValuePair<string, string> extra)
    {
        if (!string.Equals(extra.Key, "Vary", StringComparison.OrdinalIgnoreCase))
        {
            headers.Add(extra);
            return headers;
        }

        for (var i = 0; i < headers.Count; i++)
        {
            if (!string.Equals(headers[i].Key, "Vary", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!ContainsToken(headers[i].Value, extra.Value))
            {
                headers[i] = new KeyValuePair<string, string>(headers[i].Key, headers[i].Value + ", " + extra.Value);
            }

            return headers;
        }

        headers.Add(extra);
        return headers;
    }

    private static void AddMerged(HeaderCollection headers, KeyValuePair<string, string> extra)
    {
        if (!string.Equals(extra.Key, "Vary", StringComparison.OrdinalIgnoreCase))
        {
            headers.AddHeader(extra.Key, extra.Value);
            return;
        }

        var existing = headers.GetHeaders("Vary");
        if (existing is not { Count: > 0 })
        {
            headers.AddHeader("Vary", extra.Value);
            return;
        }

        if (ContainsToken(existing[0].Value, extra.Value))
        {
            return;
        }

        var merged = existing[0].Value + ", " + extra.Value;
        headers.RemoveHeader("Vary");
        headers.AddHeader("Vary", merged);
    }

    private static bool ContainsToken(string list, string token)
    {
        var start = 0;
        while (start < list.Length)
        {
            var comma = list.IndexOf(',', start);
            if (comma < 0)
            {
                comma = list.Length;
            }

            if (string.Equals(list[start..comma].Trim(), token, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            start = comma + 1;
        }

        return false;
    }

    private static bool IsRealPreflight(Request request)
    {
        if (!string.Equals(request.Method, "OPTIONS", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return !string.IsNullOrEmpty(HeaderValue(request, "Origin"))
            && !string.IsNullOrEmpty(HeaderValue(request, "Access-Control-Request-Method"));
    }

    private static RoutePolicyConfig? FindCorsPolicy(ReverseProxyOptions options, RouteConfig route)
    {
        if (route.Policies is not { Count: > 0 } names || options.Policies is not { Count: > 0 } policies)
        {
            return null;
        }

        for (var i = 0; i < names.Count; i++)
        {
            var policy = FindNamedPolicy(policies, names[i]);
            if (policy is not null && string.Equals(policy.Kind, "cors", StringComparison.Ordinal))
            {
                return policy;
            }
        }

        return null;
    }

    /// <summary><c>*</c> or a missing list allows every origin. A list echoes a match, and an unlisted origin gets nothing.</summary>
    internal static string? AllowedOrigin(string? configured, string origin) =>
        CorsOriginMatcher.Match(configured, origin);

    private static string? AllowHeaders(RoutePolicyConfig policy, string? requestHeaders)
    {
        if (!string.IsNullOrWhiteSpace(policy.HeaderName))
        {
            return policy.HeaderName;
        }

        // A credentialed preflight cannot send Allow-Headers: *. Reflect the headers the browser asked for.
        if (policy.AllowCredentials == true)
        {
            return string.IsNullOrWhiteSpace(requestHeaders) ? null : requestHeaders;
        }

        return "*";
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
