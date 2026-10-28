using System.Net;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Titanium.Web.Proxy.Abstractions.Clusters;
using Titanium.Web.Proxy.Abstractions.Network;
using Titanium.Web.Proxy.Abstractions.Routing;
using Titanium.Web.Proxy.Configuration.Models;

namespace Titanium.Web.Proxy.Configuration;

/// <summary>Validates a loaded <see cref="TwpConfig"/> for obvious structural errors.</summary>
public static class TwpConfigValidator
{
    private static readonly HashSet<string> KnownProfiles = new(StringComparer.OrdinalIgnoreCase)
    {
        "Balanced", "LegacyCompatible", "PublicFacing",
    };

    private static readonly HashSet<string> KnownPolicyModes = new(StringComparer.OrdinalIgnoreCase)
    {
        "Disabled", "Observe", "Enforce",
    };

    private static readonly HashSet<string> KnownListenerTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "explicit", "transparent", "socks", "quic", "masque",
    };

    private static readonly HashSet<string> KnownProxyTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "Http", "Socks4", "Socks5",
    };

    public static IReadOnlyList<string> Validate(TwpConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        var errors = new List<string>();
        var clusterIds = ValidateClusters(config.Clusters, errors);
        var policyNames = ValidatePolicies(config.Policies, errors);
        ValidateRoutes(config.Routes, clusterIds, policyNames, errors);
        ValidateListeners(config.Listeners, clusterIds, errors);
        ValidateServer(config.Server, errors);
        return errors;
    }

    private static HashSet<string> ValidateClusters(IEnumerable<ClusterConfig> clusters, List<string> errors)
    {
        var clusterIds = new HashSet<string>(StringComparer.Ordinal);
        var allDestinationIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var cluster in clusters)
        {
            if (string.IsNullOrWhiteSpace(cluster.Id))
            {
                errors.Add("Cluster is missing Id.");
                continue;
            }

            if (!clusterIds.Add(cluster.Id))
            {
                errors.Add($"Duplicate cluster id '{cluster.Id}'.");
            }

            if (cluster.Destinations is null || cluster.Destinations.Count == 0)
            {
                errors.Add($"Cluster '{cluster.Id}' has no destinations.");
                continue;
            }

            var destinationIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var dest in cluster.Destinations)
            {
                ValidateDestination(cluster.Id, dest, destinationIds, allDestinationIds, errors);
            }

            ValidateHttpClient(cluster.Id, cluster.HttpClient, errors);
            ValidateRetry($"clusters.{cluster.Id}.retry", cluster.Retry, errors);
            ValidateClusterProxyProtocol(cluster, errors);
        }

        return clusterIds;
    }

    private static void ValidateClusterProxyProtocol(ClusterConfig cluster, List<string> errors)
    {
        if (cluster.SendProxyProtocol is null)
        {
            return;
        }

        if (!IsProxyProtocolMode(cluster.SendProxyProtocol))
        {
            errors.Add($"Cluster '{cluster.Id}' sendProxyProtocol '{cluster.SendProxyProtocol}' is not none or v2.");
        }
    }

    private static bool IsProxyProtocolMode(string value) =>
        value.Equals("none", StringComparison.OrdinalIgnoreCase)
        || value.Equals("v2", StringComparison.OrdinalIgnoreCase);

    private static void ValidateRetry(string path, RetryConfig? retry, List<string> errors)
    {
        if (retry is null)
        {
            return;
        }

        RequireNonNegative(retry.Attempts, $"{path}.attempts", errors);
        RequireNonNegative(retry.MaxBufferedBodyBytes, $"{path}.maxBufferedBodyBytes", errors);
        RequireNonNegative(retry.PerTryTimeoutMilliseconds, $"{path}.perTryTimeoutMilliseconds", errors);
        RequireNonNegative(retry.BackoffMilliseconds, $"{path}.backoffMilliseconds", errors);
        RequireNonNegative(retry.MaxBackoffMilliseconds, $"{path}.maxBackoffMilliseconds", errors);
        RequireNonNegative(retry.BudgetMinConcurrent, $"{path}.budgetMinConcurrent", errors);

        if (retry.JitterPercent is < 0 or > 100)
        {
            errors.Add($"{path}.jitterPercent must be between 0 and 100.");
        }

        if (retry.BudgetPercent is < 0 or > 1000)
        {
            errors.Add($"{path}.budgetPercent must be between 0 and 1000 (0 = unlimited).");
        }

        if (retry.StatusCodes is { } codes)
        {
            if (codes.Count == 0)
            {
                errors.Add($"{path}.statusCodes must list at least one status (or be omitted for 502, 503, 504).");
            }

            foreach (var code in codes)
            {
                if (code is not (429 or (>= 500 and <= 599)))
                {
                    errors.Add($"{path}.statusCodes: {code} is not retryable (use 429 or 500-599).");
                }
            }
        }
    }

    /// <summary>
    /// Upstream TLS files are checked at load time so a typo is a config error, not a 502 on the first request.
    /// The proxy applies the same rules at run time and fails closed.
    /// </summary>
    private static void ValidateHttpClient(string clusterId, ClusterHttpClientConfig? http, List<string> errors)
    {
        if (http is null)
        {
            return;
        }

        var prefix = $"Cluster '{clusterId}' httpClient";
        var hasCertificate = !string.IsNullOrWhiteSpace(http.ClientCertificatePath);
        var hasKey = !string.IsNullOrWhiteSpace(http.ClientCertificateKeyPath);
        if (http.ServerName is not null && string.IsNullOrWhiteSpace(http.ServerName))
        {
            errors.Add($"{prefix}: serverName must not be blank.");
        }

        if (hasKey && !hasCertificate)
        {
            errors.Add($"{prefix}: clientCertificateKeyPath requires clientCertificatePath.");
        }

        if (!string.IsNullOrEmpty(http.ClientCertificatePasswordEnv) && !hasCertificate)
        {
            errors.Add($"{prefix}: clientCertificatePasswordEnv requires clientCertificatePath.");
        }

        if (hasCertificate)
        {
            ValidateClientCertificateFiles(prefix, http, hasKey, errors);
        }

        if (!string.IsNullOrWhiteSpace(http.CaBundlePath))
        {
            ValidateCaBundle(prefix, http.CaBundlePath, errors);
        }
    }

    private static void ValidateClientCertificateFiles(string prefix, ClusterHttpClientConfig http, bool hasKey,
        List<string> errors)
    {
        var certPath = http.ClientCertificatePath!;
        if (!File.Exists(certPath))
        {
            errors.Add($"{prefix}: clientCertificatePath '{certPath}' was not found.");
            return;
        }

        if (hasKey && !File.Exists(http.ClientCertificateKeyPath!))
        {
            errors.Add($"{prefix}: clientCertificateKeyPath '{http.ClientCertificateKeyPath}' was not found.");
            return;
        }

        string? password = null;
        if (!string.IsNullOrEmpty(http.ClientCertificatePasswordEnv))
        {
            password = Environment.GetEnvironmentVariable(http.ClientCertificatePasswordEnv);
            if (password is null)
            {
                errors.Add($"{prefix}: environment variable '{http.ClientCertificatePasswordEnv}' (clientCertificatePasswordEnv) is not set.");
                return;
            }
        }

        try
        {
            using var certificate = hasKey
                ? LoadPemPair(certPath, http.ClientCertificateKeyPath!, password)
                : X509CertificateLoader.LoadPkcs12FromFile(certPath, password);
            if (!certificate.HasPrivateKey)
            {
                errors.Add($"{prefix}: clientCertificatePath '{certPath}' has no private key.");
            }
        }
        catch (Exception ex) when (ex is CryptographicException or ArgumentException or IOException)
        {
            errors.Add($"{prefix}: client certificate does not parse ({ex.Message}).");
        }
    }

    private static X509Certificate2 LoadPemPair(string certPath, string keyPath, string? password)
    {
        var keyPem = File.ReadAllText(keyPath);
        return keyPem.Contains("ENCRYPTED PRIVATE KEY", StringComparison.Ordinal)
            ? X509Certificate2.CreateFromEncryptedPemFile(certPath, password, keyPath)
            : X509Certificate2.CreateFromPemFile(certPath, keyPath);
    }

    private static void ValidateCaBundle(string prefix, string path, List<string> errors)
    {
        if (!File.Exists(path))
        {
            errors.Add($"{prefix}: caBundlePath '{path}' was not found.");
            return;
        }

        try
        {
            var roots = ClientCertificateAuthorityFiles.Load(path);
            if (roots.Count == 0)
            {
                errors.Add($"{prefix}: caBundlePath contains no certificates.");
            }
        }
        catch (Exception ex) when (ex is CryptographicException or ArgumentException or IOException)
        {
            errors.Add($"{prefix}: caBundlePath does not parse ({ex.Message}).");
        }
    }
    private static void ValidateDestination(
        string clusterId,
        DestinationConfig dest,
        HashSet<string> destinationIds,
        HashSet<string> allDestinationIds,
        List<string> errors)
    {
        if (string.IsNullOrWhiteSpace(dest.Id))
        {
            errors.Add($"Cluster '{clusterId}' has a destination with an empty id.");
        }
        else if (!destinationIds.Add(dest.Id) || !allDestinationIds.Add(dest.Id))
        {
            errors.Add($"Duplicate destination id '{dest.Id}'.");
        }

        if (string.IsNullOrWhiteSpace(dest.Address))
        {
            errors.Add($"Destination '{dest.Id}' has an empty address.");
        }
        else if (dest.Address.StartsWith("unix:", StringComparison.Ordinal) && OperatingSystem.IsWindows())
        {
            errors.Add($"Destination '{dest.Id}' uses a unix socket, which is not supported on Windows.");
        }

        if (dest.DnsRefreshSeconds < 0)
        {
            errors.Add($"Destination '{dest.Id}' dnsRefreshSeconds must be >= 0.");
        }

        if (dest.Port is < 0 or > 65535)
        {
            errors.Add($"Destination '{dest.Id}' port {dest.Port} is out of range.");
        }

        if (dest.Weight < 1)
        {
            errors.Add($"Destination '{dest.Id}' weight must be >= 1.");
        }
    }

    private static readonly HashSet<string> KnownPolicyKinds = new(StringComparer.Ordinal)
    {
        "maxRequestBodySize", "requireHeader", "apiKey", "timeout", "cors", "rateLimit", "connectionLimit", "ipAllowDeny", "extAuth",
    };

    private static readonly HashSet<string> ExtAuthMethods = new(StringComparer.Ordinal)
    {
        "GET", "HEAD", "POST", "PUT", "PATCH", "DELETE", "OPTIONS",
    };

    // The proxy sets these on every subrequest, so a configured sendHeaders entry could only shadow them.
    private static readonly HashSet<string> ExtAuthReservedHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "X-Original-Method", "X-Original-Uri", "X-Original-Host", "X-Forwarded-For", "X-Titanium-ExtAuth",
    };

    // Hop-by-hop and framing headers describe one connection. They cannot carry a decision.
    private static readonly HashSet<string> ExtAuthForbiddenHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Connection", "Keep-Alive", "Proxy-Connection", "Proxy-Authenticate", "Proxy-Authorization", "TE", "Trailer",
        "Transfer-Encoding", "Upgrade", "Content-Length", "Host",
    };

    /// <summary>
    /// A policy that is misspelled, incomplete, or never defined would be skipped at run time, which turns a
    /// protection into a no-op. Every such case is a load-time error instead.
    /// </summary>
    private static HashSet<string> ValidatePolicies(IEnumerable<RoutePolicyConfig>? policies, List<string> errors)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        if (policies is null)
        {
            return names;
        }

        foreach (var policy in policies)
        {
            if (string.IsNullOrWhiteSpace(policy.Name))
            {
                errors.Add("Policy is missing Name.");
                continue;
            }

            if (!names.Add(policy.Name))
            {
                errors.Add($"Duplicate policy name '{policy.Name}'.");
            }

            if (!KnownPolicyKinds.Contains(policy.Kind ?? string.Empty))
            {
                errors.Add($"Policy '{policy.Name}' has unknown kind '{policy.Kind}'.");
                continue;
            }

            ValidatePolicyParameters(policy, errors);
        }

        return names;
    }

    private static void ValidatePolicyParameters(RoutePolicyConfig policy, List<string> errors)
    {
        var name = policy.Name;
        switch (policy.Kind)
        {
            case "maxRequestBodySize" when policy.MaxRequestBodyBytes is not > 0:
                errors.Add($"Policy '{name}' (maxRequestBodySize) needs maxRequestBodyBytes > 0.");
                break;
            case "requireHeader" when string.IsNullOrWhiteSpace(policy.HeaderName):
                errors.Add($"Policy '{name}' (requireHeader) needs headerName.");
                break;
            case "apiKey" when string.IsNullOrWhiteSpace(policy.HeaderName) || string.IsNullOrEmpty(policy.HeaderValue):
                errors.Add($"Policy '{name}' (apiKey) needs headerName and headerValue.");
                break;
            case "timeout" when policy.TimeoutSeconds is not > 0:
                errors.Add($"Policy '{name}' (timeout) needs timeoutSeconds > 0.");
                break;
            case "rateLimit" or "connectionLimit":
                if (policy.Limit is < 1)
                {
                    errors.Add($"Policy '{name}' limit must be >= 1.");
                }

                if (policy.WindowSeconds is < 1)
                {
                    errors.Add($"Policy '{name}' windowSeconds must be >= 1.");
                }

                if (policy.Kind == "rateLimit")
                {
                    ValidateRateLimit(policy, errors);
                }

                break;
            case "ipAllowDeny":
                if (string.IsNullOrWhiteSpace(policy.Allow) && string.IsNullOrWhiteSpace(policy.Deny))
                {
                    errors.Add($"Policy '{name}' (ipAllowDeny) needs allow or deny.");
                }

                ValidatePrefixList(name, "allow", policy.Allow, errors);
                ValidatePrefixList(name, "deny", policy.Deny, errors);
                break;
            case "extAuth":
                ValidateExtAuth(policy, errors);
                break;
        }
    }

    private static void ValidateRateLimit(RoutePolicyConfig policy, List<string> errors)
    {
        var name = policy.Name;
        if (policy.Key is not null && !IsRateLimitKey(policy.Key))
        {
            errors.Add(
                $"Policy '{name}' (rateLimit) key '{policy.Key}' must be client, forwardedClient, route, header:<Name>, or cookie:<Name>.");
        }

        if (policy.Algorithm is not null && policy.Algorithm is not ("fixedWindow" or "tokenBucket"))
        {
            errors.Add($"Policy '{name}' (rateLimit) algorithm '{policy.Algorithm}' must be fixedWindow or tokenBucket.");
        }

        if (policy.Burst is < 1)
        {
            errors.Add($"Policy '{name}' (rateLimit) burst must be >= 1.");
        }

        if (policy.MaxKeys is < 1000 or > 10_000_000)
        {
            errors.Add($"Policy '{name}' (rateLimit) maxKeys must be 1000-10000000.");
        }
    }

    private static bool IsRateLimitKey(string key)
    {
        if (key is "client" or "forwardedClient" or "route")
        {
            return true;
        }

        const int prefixLength = 7;
        return key.Length > prefixLength &&
               (key.StartsWith("header:", StringComparison.Ordinal) ||
                key.StartsWith("cookie:", StringComparison.Ordinal)) &&
               IsHeaderToken(key[prefixLength..]);
    }

    private static void ValidateExtAuth(RoutePolicyConfig policy, List<string> errors)
    {
        var name = policy.Name;
        if (!Uri.TryCreate(policy.Url, UriKind.Absolute, out var url) ||
            (url.Scheme != Uri.UriSchemeHttp && url.Scheme != Uri.UriSchemeHttps) ||
            !string.IsNullOrEmpty(url.UserInfo))
        {
            errors.Add($"Policy '{name}' (extAuth) needs url, an absolute http or https URL without credentials.");
        }

        if (policy.Method is not null && !ExtAuthMethods.Contains(policy.Method.Trim().ToUpperInvariant()))
        {
            errors.Add($"Policy '{name}' (extAuth) method '{policy.Method}' must be one of GET, HEAD, POST, PUT, PATCH, DELETE, OPTIONS.");
        }

        if (policy.TimeoutMilliseconds is < 1 or > 60_000)
        {
            errors.Add($"Policy '{name}' (extAuth) timeoutMilliseconds must be 1-60000.");
        }

        if (policy.CacheSeconds is < 0)
        {
            errors.Add($"Policy '{name}' (extAuth) cacheSeconds must be >= 0.");
        }

        if (policy.CacheMaxEntries is < 1 or > 1_000_000)
        {
            errors.Add($"Policy '{name}' (extAuth) cacheMaxEntries must be 1-1000000.");
        }

        ValidateExtAuthHeaders(name, "sendHeaders", policy.SendHeaders, rejectReserved: true, errors);
        ValidateExtAuthHeaders(name, "copyResponseHeaders", policy.CopyResponseHeaders, rejectReserved: false, errors);
        ValidateExtAuthHeaders(name, "denyResponseHeaders", policy.DenyResponseHeaders, rejectReserved: false, errors);
    }

    private static void ValidateExtAuthHeaders(
        string policyName, string field, IReadOnlyList<string>? headers, bool rejectReserved, List<string> errors)
    {
        if (headers is null)
        {
            return;
        }

        foreach (var header in headers)
        {
            var trimmed = header?.Trim();
            if (string.IsNullOrEmpty(trimmed) || !IsHeaderToken(trimmed))
            {
                errors.Add($"Policy '{policyName}' (extAuth) {field} entry '{header}' is not a header name.");
            }
            else if (ExtAuthForbiddenHeaders.Contains(trimmed))
            {
                errors.Add($"Policy '{policyName}' (extAuth) {field} entry '{trimmed}' is a connection-level header.");
            }
            else if (rejectReserved && ExtAuthReservedHeaders.Contains(trimmed))
            {
                errors.Add($"Policy '{policyName}' (extAuth) {field} entry '{trimmed}' is set by the proxy.");
            }
        }
    }

    private static bool IsHeaderToken(string value)
    {
        foreach (var c in value)
        {
            if (!(char.IsAsciiLetterOrDigit(c) || "!#$%&'*+-.^_`|~".Contains(c, StringComparison.Ordinal)))
            {
                return false;
            }
        }

        return true;
    }

    private static void ValidatePrefixList(string name, string field, string? csv, List<string> errors)
    {
        if (string.IsNullOrWhiteSpace(csv))
        {
            return;
        }

        foreach (var part in csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!CidrPrefix.TryParse(part, out _))
            {
                errors.Add($"Policy '{name}' {field} entry '{part}' is not an address or CIDR prefix.");
            }
        }
    }

    private static void ValidateRoutes(
        IEnumerable<RouteConfig> routes,
        HashSet<string> clusterIds,
        HashSet<string> policyNames,
        List<string> errors)
    {
        var routeIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var route in routes)
        {
            ValidateRouteIdentity(route, routeIds, clusterIds, errors);
            ValidateRouteMatch(route, errors);
            ValidateRoutePolicies(route, policyNames, errors);
            ValidateRouteMirror(route, clusterIds, errors);
            ValidateRouteRedirect(route, errors);
            ValidateRouteTransforms(route, errors);
            ValidateRouteLimits(route, errors);
            ValidateRouteCompression(route, errors);
            ValidateRouteGrpcWeb(route, errors);
            ValidateRetry($"routes.{route.Id}.retry", route.Retry, errors);
        }
    }

    private static void ValidateRouteIdentity(RouteConfig route, HashSet<string> routeIds, HashSet<string> clusterIds, List<string> errors)
    {
        if (string.IsNullOrWhiteSpace(route.Id))
        {
            errors.Add("Route is missing Id.");
        }
        else if (!routeIds.Add(route.Id))
        {
            errors.Add($"Duplicate route id '{route.Id}'.");
        }

        if (route.Redirect is null && route.DirectResponse is null &&
            (string.IsNullOrWhiteSpace(route.ClusterId) || !clusterIds.Contains(route.ClusterId)))
        {
            errors.Add($"Route '{route.Id}' references unknown cluster '{route.ClusterId}'.");
        }
    }

    private static void ValidateRouteMatch(RouteConfig route, List<string> errors)
    {
        if (route.Match is null)
        {
            errors.Add($"Route '{route.Id}' is missing Match.");
            return;
        }

        if (route.Match.PathKind == PathMatchKind.Template)
        {
            ValidateTemplate(route, errors);
            return;
        }

        if (route.Match.PathKind == PathMatchKind.Regex && !string.IsNullOrEmpty(route.Match.Path) &&
            !CompilesAsRegex(route.Match.Path))
        {
            errors.Add($"Route '{route.Id}' has a path regex that does not compile.");
        }
    }

    private static void ValidateRoutePolicies(RouteConfig route, HashSet<string> policyNames, List<string> errors)
    {
        if (route.Policies is null)
        {
            return;
        }

        foreach (var policyName in route.Policies.Where(policyName => !policyNames.Contains(policyName)))
        {
            errors.Add($"Route '{route.Id}' references unknown policy '{policyName}'.");
        }
    }

    private static void ValidateRouteMirror(RouteConfig route, HashSet<string> clusterIds, List<string> errors)
    {
        if (route.Mirror is not { } mirror)
        {
            return;
        }

        if (!clusterIds.Contains(mirror.ClusterId))
        {
            errors.Add($"Route '{route.Id}' mirrors to unknown cluster '{mirror.ClusterId}'.");
        }

        if (mirror.SamplePercent is < 0 or > 100)
        {
            errors.Add($"Route '{route.Id}' mirror samplePercent must be 0-100.");
        }

        if (mirror.MaxConcurrency is < 1 or > 16)
        {
            errors.Add($"Route '{route.Id}' mirror maxConcurrency must be 1-16.");
        }

        if (mirror.MaxBodyBytes is < 0 or > MirrorConfig.MaxAllowedBodyBytes)
        {
            errors.Add($"Route '{route.Id}' mirror maxBodyBytes must be 0-{MirrorConfig.MaxAllowedBodyBytes}.");
        }
    }

    private static void ValidateRouteRedirect(RouteConfig route, List<string> errors)
    {
        if (route.Redirect is { Status: < 300 or > 399 } badRedirect)
        {
            errors.Add($"Route '{route.Id}' redirect status {badRedirect.Status} is not a 3xx code.");
        }
    }

    private static void ValidateRouteTransforms(RouteConfig route, List<string> errors)
    {
        if (route.Transforms is null)
        {
            return;
        }

        foreach (var transform in route.Transforms)
        {
            if (!TransformKinds.TryGetRequiredParameters(transform.Kind, out var required))
            {
                errors.Add($"Route '{route.Id}' has unknown transform kind '{transform.Kind}'.");
                continue;
            }

            foreach (var name in required.Where(name => transform.Parameters is null || !transform.Parameters.ContainsKey(name)))
            {
                errors.Add($"Route '{route.Id}' transform '{transform.Kind}' requires parameter '{name}'.");
            }
        }
    }

    private static void ValidateRouteLimits(RouteConfig route, List<string> errors)
    {
        var limits = route.Limits;
        if (limits is null)
        {
            return;
        }

        RequirePositiveIfSet(limits.RequestTimeoutSeconds, $"routes.{route.Id}.limits.requestTimeoutSeconds", errors);
        RequirePositiveIfSet(limits.IdleTimeoutSeconds, $"routes.{route.Id}.limits.idleTimeoutSeconds", errors);
        RequirePositiveIfSet(limits.MaxBufferedBodyBytes, $"routes.{route.Id}.limits.maxBufferedBodyBytes", errors);
        RequirePositiveIfSet(limits.MaxWebSocketFramePayloadBytes, $"routes.{route.Id}.limits.maxWebSocketFramePayloadBytes", errors);
    }

    private static void ValidateListeners(
        IEnumerable<ListenerConfig> listeners, HashSet<string> clusterIds, List<string> errors)
    {
        foreach (var listener in listeners)
        {
            ValidateListenerBounds(listener, errors);
            ValidatePassthrough(listener, clusterIds, errors);
            ValidateMasqueListener(listener, errors);
            ValidateClientCertificate(listener, errors);
            ValidateTrustedProxySources(listener, errors);
        }
    }

    private static void ValidateListenerBounds(ListenerConfig listener, List<string> errors)
    {
        if (listener.Port is < 1 or > 65535)
        {
            errors.Add($"Listener port {listener.Port} is out of range.");
        }

        if (!string.IsNullOrWhiteSpace(listener.Type) && !KnownListenerTypes.Contains(listener.Type))
        {
            errors.Add($"Listener type '{listener.Type}' is unknown (explicit, transparent, socks, quic, masque).");
        }

        if (listener.MaxCachedConnections is <= 0)
        {
            errors.Add($"Listener port {listener.Port}: maxCachedConnections must be positive.");
        }

        if (listener.MaxConcurrentClients is <= 0)
        {
            errors.Add($"Listener port {listener.Port}: maxConcurrentClients must be positive.");
        }

        if (listener.HandshakeTimeoutSeconds is < 0)
        {
            errors.Add($"Listener port {listener.Port}: handshakeTimeoutSeconds must be >= 0.");
        }

        if (listener.IdleTimeoutSeconds is < 0)
        {
            errors.Add($"Listener port {listener.Port}: idleTimeoutSeconds must be >= 0.");
        }

        if (listener.AltSvc?.Port is < 1 or > 65535)
        {
            errors.Add($"Listener port {listener.Port}: altSvc.port must be between 1 and 65535.");
        }

        if (listener.AltSvc?.MaxAgeSeconds is < 1 or > 31536000)
        {
            errors.Add($"Listener port {listener.Port}: altSvc.maxAgeSeconds must be between 1 and 31536000.");
        }
    }

    private static void ValidatePassthrough(ListenerConfig listener, HashSet<string> clusterIds, List<string> errors)
    {
        var prefix = $"Listener port {listener.Port}:";
        var routes = listener.SniRoutes ?? [];
        var hasDefault = !string.IsNullOrWhiteSpace(listener.SniDefaultClusterId);
        var usesSni = routes.Count > 0 || hasDefault;

        if (usesSni)
        {
            if (!listener.Passthrough)
            {
                errors.Add($"{prefix} sniRoutes and sniDefaultClusterId require passthrough: true.");
            }

            if (listener.DecryptSsl)
            {
                errors.Add($"{prefix} sniRoutes and sniDefaultClusterId require decryptSsl: false (the proxy reads the ClientHello but never decrypts).");
            }

            if (!IsTransparentListener(listener))
            {
                errors.Add($"{prefix} sniRoutes and sniDefaultClusterId apply only to transparent listeners.");
            }
        }
        else if (listener.Passthrough && string.IsNullOrEmpty(listener.ForwardHost))
        {
            errors.Add($"{prefix} passthrough needs sniRoutes, sniDefaultClusterId, or forwardHost.");
        }

        if (hasDefault && !clusterIds.Contains(listener.SniDefaultClusterId!))
        {
            errors.Add($"{prefix} sniDefaultClusterId '{listener.SniDefaultClusterId}' is not a defined cluster.");
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < routes.Count; i++)
        {
            var route = routes[i];
            var name = route.ServerName?.Trim();
            if (!IsValidSniPattern(name))
            {
                errors.Add($"{prefix} sniRoutes[{i}].serverName '{route.ServerName}' must be an exact host name or one leading '*.' label followed by a suffix.");
            }
            else if (!seen.Add(name!))
            {
                errors.Add($"{prefix} sniRoutes[{i}].serverName '{name}' is listed twice.");
            }

            if (string.IsNullOrWhiteSpace(route.ClusterId))
            {
                errors.Add($"{prefix} sniRoutes[{i}].clusterId is required.");
            }
            else if (!clusterIds.Contains(route.ClusterId))
            {
                errors.Add($"{prefix} sniRoutes[{i}].clusterId '{route.ClusterId}' is not a defined cluster.");
            }
        }

        if (listener.SniReadTimeoutSeconds is < 1 or > 60)
        {
            errors.Add($"{prefix} sniReadTimeoutSeconds must be between 1 and 60.");
        }

        if (!string.IsNullOrWhiteSpace(listener.SendProxyProtocol))
        {
            if (!listener.SendProxyProtocol.Equals("none", StringComparison.OrdinalIgnoreCase)
                && !listener.SendProxyProtocol.Equals("v2", StringComparison.OrdinalIgnoreCase))
            {
                errors.Add($"{prefix} sendProxyProtocol '{listener.SendProxyProtocol}' is not none or v2.");
            }
            else if (!listener.Passthrough && listener.SendProxyProtocol.Equals("v2", StringComparison.OrdinalIgnoreCase))
            {
                errors.Add($"{prefix} sendProxyProtocol v2 requires passthrough: true.");
            }
        }
    }

    /// <summary>An exact DNS-style name, or <c>*.</c> followed by a suffix with no further wildcard.</summary>
    private static bool IsValidSniPattern(string? pattern)
    {
        if (string.IsNullOrEmpty(pattern))
        {
            return false;
        }

        var host = pattern.StartsWith("*.", StringComparison.Ordinal) ? pattern[2..] : pattern;
        if (host.Length == 0 || host.Length > 253 || host.StartsWith('.') || host.EndsWith('.') || host.Contains(".."))
        {
            return false;
        }

        foreach (var ch in host)
        {
            if (!(char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_' or '.'))
            {
                return false;
            }
        }

        return true;
    }

    private static void ValidateMasqueListener(ListenerConfig listener, List<string> errors)
    {
        if (string.Equals(listener.Type, "masque", StringComparison.OrdinalIgnoreCase)
            && listener.Masque?.RequireAuth != false
            && listener.Masque?.Credentials is not { Count: > 0 })
        {
            errors.Add(
                $"Listener port {listener.Port}: masque listener requires credentials or requireAuth: false.");
        }

        if (listener.Masque is not { } masque)
        {
            return;
        }

        ValidateMasquePortsAndLimits(listener, masque, errors);
        if (masque.Credentials is not { } credentials)
        {
            return;
        }

        var seenUsers = new HashSet<string>(StringComparer.Ordinal);
        foreach (var credential in credentials)
        {
            if (string.IsNullOrWhiteSpace(credential.Username) || string.IsNullOrWhiteSpace(credential.PasswordEnv))
            {
                errors.Add($"Listener port {listener.Port}: masque credential requires username and passwordEnv.");
                continue;
            }

            if (!seenUsers.Add(credential.Username))
            {
                errors.Add($"Listener port {listener.Port}: masque credential username '{credential.Username}' is duplicated.");
            }
        }
    }

    private static void ValidateMasquePortsAndLimits(ListenerConfig listener, MasqueListenerConfig masque, List<string> errors)
    {
        if (masque.AllowedPorts is { } ports)
        {
            if (ports.Length == 0)
            {
                errors.Add($"Listener port {listener.Port}: masque.allowedPorts must not be empty.");
            }

            foreach (var allowed in ports.Where(allowed => allowed is < 1 or > 65535))
            {
                errors.Add($"Listener port {listener.Port}: masque.allowedPorts contains {allowed}.");
            }
        }

        if (masque.MaxTunnels is int maxTunnels && masque.MaxTunnelsPerClient is int perClient && perClient > maxTunnels)
        {
            errors.Add($"Listener port {listener.Port}: masque.maxTunnelsPerClient must not exceed maxTunnels.");
        }

        AddUnless(errors, listener.Port, "masque.maxDatagramsPerSecond must be at most 1000000.", masque.MaxDatagramsPerSecond is > 1_000_000);
        AddUnless(errors, listener.Port, "masque.maxBytesPerSecond must be at most 1073741824.", masque.MaxBytesPerSecond is > 1_073_741_824);
        AddUnless(errors, listener.Port, "masque.maxTunnels must be positive.", masque.MaxTunnels is <= 0);
        AddUnless(errors, listener.Port, "masque.maxTunnelsPerClient must be positive.", masque.MaxTunnelsPerClient is <= 0);
        AddUnless(errors, listener.Port, "masque.idleTimeoutSeconds must be positive.", masque.IdleTimeoutSeconds is <= 0);
        AddUnless(errors, listener.Port, "masque.maxDatagramsPerSecond must be positive.", masque.MaxDatagramsPerSecond is <= 0);
        AddUnless(errors, listener.Port, "masque.maxBytesPerSecond must be positive.", masque.MaxBytesPerSecond is <= 0);
    }

    private static void AddUnless(List<string> errors, int port, string message, bool failed)
    {
        if (failed)
        {
            errors.Add($"Listener port {port}: {message}");
        }
    }

    private static void ValidateClientCertificate(ListenerConfig listener, List<string> errors)
    {
        if (!string.IsNullOrWhiteSpace(listener.ClientCertificateMode)
            && !listener.ClientCertificateMode.Equals("none", StringComparison.OrdinalIgnoreCase)
            && !listener.ClientCertificateMode.Equals("allow", StringComparison.OrdinalIgnoreCase)
            && !listener.ClientCertificateMode.Equals("require", StringComparison.OrdinalIgnoreCase))
        {
            errors.Add(
                $"Listener port {listener.Port}: clientCertificateMode '{listener.ClientCertificateMode}' is not none, allow, or require.");
        }

        if ((IsClientCertificateModeSet(listener.ClientCertificateMode)
                || !string.IsNullOrWhiteSpace(listener.ClientCertificateCaPath))
            && !IsTransparentListener(listener))
        {
            errors.Add(
                $"Listener port {listener.Port}: clientCertificateMode and clientCertificateCaPath apply only to transparent listeners.");
        }

        if (string.IsNullOrWhiteSpace(listener.ClientCertificateCaPath))
        {
            return;
        }

        if (!string.Equals(listener.ClientCertificateMode, "allow", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(listener.ClientCertificateMode, "require", StringComparison.OrdinalIgnoreCase))
        {
            errors.Add($"Listener port {listener.Port}: clientCertificateCaPath requires clientCertificateMode allow or require.");
        }

        if (!File.Exists(listener.ClientCertificateCaPath))
        {
            errors.Add($"Listener port {listener.Port}: clientCertificateCaPath '{listener.ClientCertificateCaPath}' was not found.");
            return;
        }

        try
        {
            var roots = ClientCertificateAuthorityFiles.Load(listener.ClientCertificateCaPath);
            if (roots.Count == 0)
            {
                errors.Add($"Listener port {listener.Port}: clientCertificateCaPath contains no certificates.");
            }
        }
        catch (Exception ex)
        {
            errors.Add($"Listener port {listener.Port}: clientCertificateCaPath does not parse ({ex.Message}).");
        }
    }

    private static void ValidateTrustedProxySources(ListenerConfig listener, List<string> errors)
    {
        if (listener.TrustedProxyProtocolSources is not { Count: > 0 } sources)
        {
            return;
        }

        foreach (var source in sources)
        {
            if (!CidrPrefix.TryParse(source, out _))
            {
                errors.Add($"Listener port {listener.Port}: trustedProxyProtocolSources '{source}' is not a CIDR prefix.");
            }
        }
    }

    /// <summary>Same engine options the matcher uses, so what loads is what matches.</summary>
    private static bool CompilesAsRegex(string? pattern)
    {
        if (string.IsNullOrEmpty(pattern))
        {
            return false;
        }

        try
        {
            _ = new System.Text.RegularExpressions.Regex(
                pattern,
                System.Text.RegularExpressions.RegexOptions.NonBacktracking |
                System.Text.RegularExpressions.RegexOptions.CultureInvariant,
                TimeSpan.FromMilliseconds(250));
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    private static void ValidateTemplate(RouteConfig route, List<string> errors)
    {
        var path = route.Match?.Path;
        if (string.IsNullOrEmpty(path))
        {
            return;
        }

        foreach (var part in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (part.Length < 2 || part[0] != '{' || part[^1] != '}')
            {
                continue;
            }

            var inner = part[1..^1];
            var colon = inner.IndexOf(':');
            if (colon < 0 || colon == inner.Length - 1)
            {
                continue;
            }

            var constraint = inner[(colon + 1)..];
            var known = constraint is "int" or "guid" or "alpha" ||
                        (constraint.StartsWith("regex(", StringComparison.Ordinal) && constraint.EndsWith(')'));
            if (!known)
            {
                errors.Add($"Route '{route.Id}' has unknown template constraint '{constraint}'.");
            }
            else if (constraint.StartsWith("regex(", StringComparison.Ordinal) && !CompilesAsRegex(constraint[6..^1]))
            {
                errors.Add($"Route '{route.Id}' template constraint '{constraint}' does not compile.");
            }
        }
    }

    private static void ValidateServer(ServerConfig? server, List<string> errors)
    {
        if (server is null)
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(server.Profile) && !KnownProfiles.Contains(server.Profile))
        {
            errors.Add($"server.profile '{server.Profile}' is unknown (Balanced, LegacyCompatible, PublicFacing).");
        }

        if (!string.IsNullOrWhiteSpace(server.OriginHttpVersionPolicy) &&
            !server.OriginHttpVersionPolicy.Equals("PreserveClientVersion", StringComparison.OrdinalIgnoreCase) &&
            !server.OriginHttpVersionPolicy.Equals("NormalizeToHttp11", StringComparison.OrdinalIgnoreCase))
        {
            errors.Add($"server.originHttpVersionPolicy '{server.OriginHttpVersionPolicy}' is unknown.");
        }

        if (!string.IsNullOrWhiteSpace(server.CheckCertificateRevocation) &&
            !Enum.TryParse(typeof(System.Security.Cryptography.X509Certificates.X509RevocationMode),
                server.CheckCertificateRevocation, ignoreCase: true, out _))
        {
            errors.Add($"server.checkCertificateRevocation '{server.CheckCertificateRevocation}' is unknown.");
        }

        if (!string.IsNullOrWhiteSpace(server.DnsServerEndPoint) && !TryParseIpEndPoint(server.DnsServerEndPoint))
            errors.Add($"server.dnsServerEndPoint '{server.DnsServerEndPoint}' is not a host:port address.");

        if (server.DecryptFailureBypassTtlMinutes is <= 0)
            errors.Add("server.decryptFailureBypassTtlMinutes must be positive.");
        if (server.DecryptFailureBypassMaxEntries is <= 0)
            errors.Add("server.decryptFailureBypassMaxEntries must be positive.");
        if (server.DecryptFailureBypassThreshold is <= 0)
            errors.Add("server.decryptFailureBypassThreshold must be positive.");

        ValidateForwardedHeaders(server.ForwardedHeaders, errors);
        ValidateTls(server.Tls, errors);
        ValidateTimeouts(server.Timeouts, errors);
        ValidatePooling(server.Pooling, errors);
        ValidateLimits(server.Limits, errors);
        ValidatePolicyModes(server.PolicyModes, errors);
        ValidateUpstream(server.Upstream, errors);
        ValidateCertificateManager(server.CertificateManager, errors);
        ValidateAccessLog(server.AccessLog, errors);
        ValidateResponseCompression(server.ResponseCompression, errors);
    }

    private static readonly string[] CompressionAlgorithmNames = ["br", "gzip"];

    private static void ValidateResponseCompression(ResponseCompressionConfig? compression, List<string> errors)
    {
        if (compression is null)
        {
            return;
        }

        const string prefix = "server.responseCompression";
        if (compression.MinBytes < 1)
        {
            errors.Add($"{prefix}.minBytes must be at least 1.");
        }

        if (compression.MaxBytes < 1 || compression.MaxBytes > ResponseCompressionConfig.DefaultMaxBytes)
        {
            errors.Add($"{prefix}.maxBytes must be 1-{ResponseCompressionConfig.DefaultMaxBytes}.");
        }
        else if (compression.MinBytes > compression.MaxBytes)
        {
            errors.Add($"{prefix}.minBytes must not exceed maxBytes.");
        }

        if (compression.Algorithms is { } algorithms)
        {
            if (algorithms.Count == 0)
            {
                errors.Add($"{prefix}.algorithms must list at least one of: br, gzip.");
            }

            foreach (var name in algorithms.Where(name =>
                         !CompressionAlgorithmNames.Contains(name?.Trim(), StringComparer.OrdinalIgnoreCase)))
            {
                errors.Add($"{prefix}.algorithms entry '{name}' is unknown. Use br or gzip.");
            }
        }

        if (!string.IsNullOrWhiteSpace(compression.Level) &&
            !compression.Level.Trim().Equals("fastest", StringComparison.OrdinalIgnoreCase) &&
            !compression.Level.Trim().Equals("optimal", StringComparison.OrdinalIgnoreCase) &&
            !compression.Level.Trim().Equals("smallestSize", StringComparison.OrdinalIgnoreCase))
        {
            errors.Add($"{prefix}.level '{compression.Level}' is unknown. Use fastest, optimal, or smallestSize.");
        }

        if (compression.MimeTypes is { } mimeTypes)
        {
            foreach (var type in mimeTypes.Where(type => string.IsNullOrWhiteSpace(type) || !type.Contains('/')))
            {
                errors.Add($"{prefix}.mimeTypes entry '{type}' is not a media type such as text/* or application/json.");
            }
        }
    }

    private static void ValidateRouteCompression(RouteConfig route, List<string> errors)
    {
        if (route.Compression is { } mode && !mode.Trim().Equals("off", StringComparison.OrdinalIgnoreCase))
        {
            errors.Add($"Route '{route.Id}' compression '{mode}' is unknown. Only 'off' is supported.");
        }
    }

    private static void ValidateRouteGrpcWeb(RouteConfig route, List<string> errors)
    {
        if (route.GrpcWeb && (route.Redirect is not null || route.DirectResponse is not null))
        {
            errors.Add($"Route '{route.Id}' grpcWeb needs an origin. It cannot be combined with redirect or directResponse.");
        }
    }

    /// <summary>
    /// An entry that does not parse trusts nobody at run time, so a typo would silently stop a real proxy
    /// from being believed. Reject it at load instead.
    /// </summary>
    private static void ValidateForwardedHeaders(ForwardedHeadersConfig? forwarded, List<string> errors)
    {
        if (forwarded?.TrustedProxies is not { } proxies)
        {
            return;
        }

        foreach (var entry in proxies.Where(entry => !CidrPrefix.TryParse(entry, out _)))
        {
            errors.Add($"server.forwardedHeaders.trustedProxies entry '{entry}' is not an address or CIDR prefix.");
        }
    }

    private static void ValidateAccessLog(AccessLogConfig? accessLog, List<string> errors)
    {
        if (accessLog is null)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(accessLog.Path))
        {
            errors.Add("server.accessLog.path is required when accessLog is set.");
        }

        if (accessLog.SampleRate is < 0 or > 1)
        {
            errors.Add("server.accessLog.sampleRate must be between 0 and 1.");
        }
    }

    private static void ValidateTimeouts(TimeoutsConfig? timeouts, List<string> errors)
    {
        if (timeouts is null)
        {
            return;
        }

        RequireNonNegative(timeouts.ConnectionTimeOutSeconds, "server.timeouts.connectionTimeOutSeconds", errors);
        RequireNonNegative(timeouts.ConnectTimeOutSeconds, "server.timeouts.connectTimeOutSeconds", errors);
        RequireNonNegative(timeouts.ClientHeaderTimeoutSeconds, "server.timeouts.clientHeaderTimeoutSeconds", errors);
        RequireNonNegative(timeouts.ResponseHeaderTimeoutSeconds, "server.timeouts.responseHeaderTimeoutSeconds", errors);
        RequireNonNegative(timeouts.IdleReadTimeoutSeconds, "server.timeouts.idleReadTimeoutSeconds", errors);
        RequireNonNegative(timeouts.IdleWriteTimeoutSeconds, "server.timeouts.idleWriteTimeoutSeconds", errors);
        RequireNonNegative(timeouts.RequestTimeoutSeconds, "server.timeouts.requestTimeoutSeconds", errors);
        RequireNonNegative(timeouts.NetworkFailureRetryAttempts, "server.timeouts.networkFailureRetryAttempts", errors);
    }

    private static void ValidatePooling(PoolingConfig? pooling, List<string> errors)
    {
        if (pooling is null)
        {
            return;
        }

        if (pooling.MaxCachedConnections is <= 0)
        {
            errors.Add("server.pooling.maxCachedConnections must be positive.");
        }

        if (pooling.MaxConcurrentHttp11HttpsOriginCreates is <= 0)
        {
            errors.Add("server.pooling.maxConcurrentHttp11HttpsOriginCreates must be positive.");
        }

        if (pooling.MaxConcurrentClientConnections is <= 0)
        {
            errors.Add("server.pooling.maxConcurrentClientConnections must be positive.");
        }

        RequireNonNegative(pooling.TcpTimeWaitSeconds, "server.pooling.tcpTimeWaitSeconds", errors);

        if (pooling.ListenerBackLog is <= 0)
        {
            errors.Add("server.pooling.listenerBackLog must be positive.");
        }

        if (pooling.ThreadPoolWorkerThread is <= 0)
        {
            errors.Add("server.pooling.threadPoolWorkerThread must be positive.");
        }
    }

    private static void ValidateLimits(LimitsConfig? limits, List<string> errors)
    {
        if (limits is null)
        {
            return;
        }

        RequirePositiveIfSet(limits.MaxHeaderLineBytes, "server.limits.maxHeaderLineBytes", errors);
        RequirePositiveIfSet(limits.MaxHeaderCount, "server.limits.maxHeaderCount", errors);
        RequirePositiveIfSet(limits.MaxHeaderAggregateBytes, "server.limits.maxHeaderAggregateBytes", errors);
        RequirePositiveIfSet(limits.MaxEncodedBodyBytes, "server.limits.maxEncodedBodyBytes", errors);
        RequirePositiveIfSet(limits.MaxDecodedBodyBytes, "server.limits.maxDecodedBodyBytes", errors);
        RequirePositiveIfSet(limits.MaxDecompressionRatio, "server.limits.maxDecompressionRatio", errors);
        RequirePositiveIfSet(limits.MaxConcurrentClients, "server.limits.maxConcurrentClients", errors);
        RequirePositiveIfSet(limits.MaxConcurrentStreamsPerConnection, "server.limits.maxConcurrentStreamsPerConnection", errors);
        RequirePositiveIfSet(limits.MaxPeerInitiatedIncompleteStreamResets, "server.limits.maxPeerInitiatedIncompleteStreamResets", errors);
        RequirePositiveIfSet(limits.MaxOpenHeaderBlockFrames, "server.limits.maxOpenHeaderBlockFrames", errors);
        RequirePositiveIfSet(limits.MaxOpenHeaderBlockDurationSeconds, "server.limits.maxOpenHeaderBlockDurationSeconds", errors);
        RequirePositiveIfSet(limits.MaxCachedConnectionsPerHost, "server.limits.maxCachedConnectionsPerHost", errors);
        RequirePositiveIfSet(limits.MaxOriginHttp2ConnectionsPerAuthority, "server.limits.maxOriginHttp2ConnectionsPerAuthority", errors);
        RequirePositiveIfSet(limits.MaxCertificateCacheEntries, "server.limits.maxCertificateCacheEntries", errors);
        RequirePositiveIfSet(limits.MaxCertificateDiskCacheEntries, "server.limits.maxCertificateDiskCacheEntries", errors);
        RequireNonNegative(limits.MaxBufferedBodyBytes, "server.limits.maxBufferedBodyBytes", errors);
        RequireNonNegative(limits.MaxDecodedHeaderListBytes, "server.limits.maxDecodedHeaderListBytes", errors);
        RequireNonNegative(limits.MaxWebSocketFramePayloadBytes, "server.limits.maxWebSocketFramePayloadBytes", errors);
        RequireInRange(limits.MaxHttp3FramePayloadBytes, 64L * 1024 * 1024, "server.limits.maxHttp3FramePayloadBytes", errors);
        RequireInRange(limits.MaxDeferredOutboundBytesPerStream, 64 * 1024 * 1024, "server.limits.maxDeferredOutboundBytesPerStream", errors);
        RequireInRange(limits.MaxTrailerHeaderCount, 10_000, "server.limits.maxTrailerHeaderCount", errors);
        RequireInRange(limits.MaxTrailerHeaderBlockBytes, 1024 * 1024, "server.limits.maxTrailerHeaderBlockBytes", errors);
        RequireInRange(limits.MaxHttp2CompressedHeaderBlockBytes, 16 * 1024 * 1024, "server.limits.maxHttp2CompressedHeaderBlockBytes", errors);
        RequireInRange(limits.MaxInterimResponses, 100, "server.limits.maxInterimResponses", errors);
        RequireInRange(limits.MaxAuthChallengeRounds, 20, "server.limits.maxAuthChallengeRounds", errors);
        RequireInRange(limits.MaxUpstreamProxyAuthenticationAttempts, 20, "server.limits.maxUpstreamProxyAuthenticationAttempts", errors);
        RequireInRange(limits.MaxWinAuthTokenBytes, 1024 * 1024, "server.limits.maxWinAuthTokenBytes", errors);
        RequireInRange(limits.Http2WindowUpdateTimeoutSeconds, 3600, "server.limits.http2WindowUpdateTimeoutSeconds", errors);
    }

    private static void ValidatePolicyModes(PolicyModesConfig? policy, List<string> errors)
    {
        if (policy is null)
        {
            return;
        }

        RequireKnownPolicy(policy.BodyBudget, "server.policyModes.bodyBudget", errors);
        RequireKnownPolicy(policy.DecompressionRatio, "server.policyModes.decompressionRatio", errors);
        RequireKnownPolicy(policy.HeaderLimits, "server.policyModes.headerLimits", errors);
        RequireKnownPolicy(policy.AdmissionControl, "server.policyModes.admissionControl", errors);
        RequireKnownPolicy(policy.Http2AbuseBudget, "server.policyModes.http2AbuseBudget", errors);
        RequireKnownPolicy(policy.Http2RelayValidation, "server.policyModes.http2RelayValidation", errors);
        RequireKnownPolicy(policy.WebSocketFrameBudget, "server.policyModes.webSocketFrameBudget", errors);
        RequireKnownPolicy(policy.Http1ReplaySafety, "server.policyModes.http1ReplaySafety", errors);
    }

    private static void ValidateUpstream(UpstreamConfig? upstream, List<string> errors)
    {
        if (upstream is null)
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(upstream.UpstreamProxyConfigurationScript) &&
            !Uri.TryCreate(upstream.UpstreamProxyConfigurationScript, UriKind.Absolute, out _))
        {
            errors.Add("server.upstream.upstreamProxyConfigurationScript must be an absolute URI.");
        }

        RequireEndPoint(upstream.UpStreamEndPoint, "server.upstream.upStreamEndPoint", errors);
        RequireEndPoint(upstream.UpStreamEndPointIPv4, "server.upstream.upStreamEndPointIPv4", errors);
        RequireEndPoint(upstream.UpStreamEndPointIPv6, "server.upstream.upStreamEndPointIPv6", errors);
        ValidateExternalProxy(upstream.HttpProxy, "server.upstream.httpProxy", errors);
        ValidateExternalProxy(upstream.HttpsProxy, "server.upstream.httpsProxy", errors);
    }

    private static void ValidateExternalProxy(ExternalProxyConfig? proxy, string path, List<string> errors)
    {
        if (proxy is null)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(proxy.HostName))
        {
            errors.Add($"{path}.hostName is required.");
        }

        if (proxy.Port is < 1 or > 65535)
        {
            errors.Add($"{path}.port is out of range.");
        }

        if (!string.IsNullOrWhiteSpace(proxy.ProxyType) && !KnownProxyTypes.Contains(proxy.ProxyType))
        {
            errors.Add($"{path}.proxyType '{proxy.ProxyType}' is unknown (Http, Socks4, Socks5).");
        }

        if (proxy.NextHop is not null)
        {
            ValidateExternalProxy(proxy.NextHop, $"{path}.nextHop", errors);
        }
    }

    private static void ValidateCertificateManager(CertificateManagerConfig? certs, List<string> errors)
    {
        if (certs is null)
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(certs.CertificateEngine) &&
            !certs.CertificateEngine.Equals("BouncyCastle", StringComparison.OrdinalIgnoreCase) &&
            !certs.CertificateEngine.Equals("BouncyCastleFast", StringComparison.OrdinalIgnoreCase) &&
            !certs.CertificateEngine.Equals("DefaultWindows", StringComparison.OrdinalIgnoreCase))
        {
            errors.Add($"server.certificateManager.certificateEngine '{certs.CertificateEngine}' is unknown.");
        }

        if (!string.IsNullOrWhiteSpace(certs.LeafCertificateKeyAlgorithm) &&
            !certs.LeafCertificateKeyAlgorithm.Equals("Rsa2048", StringComparison.OrdinalIgnoreCase) &&
            !certs.LeafCertificateKeyAlgorithm.Equals("EcdsaP256", StringComparison.OrdinalIgnoreCase))
        {
            errors.Add($"server.certificateManager.leafCertificateKeyAlgorithm '{certs.LeafCertificateKeyAlgorithm}' is unknown.");
        }

        if (certs.CertificateValidDays is <= 0)
        {
            errors.Add("server.certificateManager.certificateValidDays must be positive.");
        }

        if (certs.CertificateGraceDays is < 0)
        {
            errors.Add("server.certificateManager.certificateGraceDays must be >= 0.");
        }

        if (certs.CertificateCacheTimeOutMinutes is <= 0)
        {
            errors.Add("server.certificateManager.certificateCacheTimeOutMinutes must be positive.");
        }
    }

    private static void RequireKnownPolicy(string? value, string path, List<string> errors)
    {
        if (!string.IsNullOrWhiteSpace(value) && !KnownPolicyModes.Contains(value))
        {
            errors.Add($"{path} '{value}' is unknown (Disabled, Observe, Enforce).");
        }
    }

    private static void RequireNonNegative(int? value, string path, List<string> errors)
    {
        if (value is < 0)
        {
            errors.Add($"{path} must be >= 0.");
        }
    }

    private static void RequirePositiveIfSet(long? value, string path, List<string> errors)
    {
        if (value is <= 0)
        {
            errors.Add($"{path} must be positive (use null to leave default / disable).");
        }
    }

    private static void RequirePositiveIfSet(int? value, string path, List<string> errors)
    {
        if (value is <= 0)
        {
            errors.Add($"{path} must be positive (use null to leave default / disable).");
        }
    }

    private static void RequirePositiveIfSet(double? value, string path, List<string> errors)
    {
        if (value is <= 0)
        {
            errors.Add($"{path} must be positive (use null to leave default / disable).");
        }
    }

    private static void RequireInRange(long? value, long ceiling, string path, List<string> errors)
    {
        if (value is null) return;
        if (value <= 0)
            errors.Add($"{path} must be positive (use null to leave default / disable).");
        else if (value > ceiling)
            errors.Add($"{path} exceeds the ceiling of {ceiling}.");
    }

    private static void RequireInRange(int? value, int ceiling, string path, List<string> errors) =>
        RequireInRange(value.HasValue ? (long?)value.Value : null, ceiling, path, errors);

    private static void ValidateTls(TlsConfig? tls, List<string> errors)
    {
        if (tls is null) return;
        RejectUnknownProtocols(tls.SupportedSslProtocols, "server.tls.supportedSslProtocols", errors);
        RejectUnknownProtocols(tls.SupportedServerSslProtocols, "server.tls.supportedServerSslProtocols", errors);
        ValidateListenerTlsPolicy(tls, errors);
    }

    private static void ValidateListenerTlsPolicy(TlsConfig tls, List<string> errors)
    {
        if (tls.MinVersion is not null)
        {
            if (!TlsPolicyParser.TryParseMinVersion(tls.MinVersion, out var minimum))
            {
                errors.Add($"server.tls.minVersion '{tls.MinVersion}' is not Tls12 or Tls13.");
            }
            else if (tls.SupportedSslProtocols is { Count: > 0 } supported
                     && TryParseProtocolMask(supported, out var mask)
                     && mask != SslProtocols.None
                     && (mask & ~(LowestBit(minimum) - 1)) == SslProtocols.None)
            {
                errors.Add(
                    $"server.tls.minVersion {minimum} excludes every protocol in server.tls.supportedSslProtocols.");
            }
        }

        if (tls.CipherSuites is null)
        {
            return;
        }

        if (tls.CipherSuites.Count == 0)
        {
            errors.Add("server.tls.cipherSuites must list at least one suite (or be omitted for the OS default).");
            return;
        }

        foreach (var name in tls.CipherSuites)
        {
            if (!TlsPolicyParser.TryParseCipherSuite(name, out _))
            {
                errors.Add($"server.tls.cipherSuites '{name}' is not a TlsCipherSuite name.");
            }
        }
    }

    private static bool TryParseProtocolMask(IEnumerable<string> names, out SslProtocols mask)
    {
        mask = SslProtocols.None;
        foreach (var name in names)
        {
            if (string.IsNullOrWhiteSpace(name) || !Enum.TryParse<SslProtocols>(name.Trim(), ignoreCase: true, out var part))
            {
                return false;
            }

            mask |= part;
        }

        return true;
    }

    private static SslProtocols LowestBit(SslProtocols value) => (SslProtocols)((int)value & -(int)value);

    private static void CollectTlsWarnings(TlsConfig? tls, bool isWindows, List<string> warnings)
    {
        if (isWindows && tls?.CipherSuites is { Count: > 0 })
        {
            warnings.Add(
                "server.tls.cipherSuites is ignored on Windows: .NET has no cipher suite policy there. " +
                "The OS default applies. Use Linux or macOS to restrict listener cipher suites.");
        }
    }

    private static void RejectUnknownProtocols(IList<string>? names, string path, List<string> errors)
    {
        if (names is null) return;
        foreach (var name in names)
        {
            if (string.IsNullOrWhiteSpace(name) || !Enum.TryParse<SslProtocols>(name.Trim(), ignoreCase: true, out _))
                errors.Add($"{path} '{name}' is not an SslProtocols name.");
        }
    }

    private static void RequireEndPoint(string? value, string path, List<string> errors)
    {
        if (!string.IsNullOrWhiteSpace(value) && !TryParseIpEndPoint(value))
            errors.Add($"{path} '{value}' is not a host:port address.");
    }

    private static bool TryParseIpEndPoint(string value)
    {
        var text = value.Trim();
        string host;
        string portText;
        if (text.StartsWith('['))
        {
            var close = text.IndexOf(']');
            if (close < 0 || close + 2 >= text.Length || text[close + 1] != ':') return false;
            host = text[1..close];
            portText = text[(close + 2)..];
        }
        else
        {
            var colon = text.LastIndexOf(':');
            if (colon <= 0) return false;
            host = text[..colon];
            portText = text[(colon + 1)..];
        }

        return IPAddress.TryParse(host, out _) &&
               int.TryParse(portText, out var port) &&
               port is > 0 and <= 65535;
    }

    /// <summary>
    ///     Limits that are accepted by config but not enforced on a live path. Callers print these
    ///     without failing validation.
    /// </summary>
    public static IReadOnlyList<string> CollectWarnings(TwpConfig config) =>
        CollectWarnings(config, OperatingSystem.IsWindows());

    /// <summary>
    ///     Same as <see cref="CollectWarnings(TwpConfig)" /> for an explicit platform, so a Windows-only warning
    ///     can be checked from any host.
    /// </summary>
    public static IReadOnlyList<string> CollectWarnings(TwpConfig config, bool isWindows)
    {
        var warnings = new List<string>();
        CollectTlsWarnings(config.Server?.Tls, isWindows, warnings);
        CollectClusterProxyProtocolWarnings(config, warnings);
        var limits = config.Server?.Limits;
        if (limits?.MaxDecompressionRatio is not null)
            warnings.Add("server.limits.maxDecompressionRatio is reserved and not enforced. Body expansion is bounded by maxBufferedBodyBytes.");
        if (limits?.MaxEncodedBodyBytes is not null)
            warnings.Add("server.limits.maxEncodedBodyBytes is reserved and not enforced. The enforced whole-body budget is maxBufferedBodyBytes.");
        if (limits?.MaxDecodedBodyBytes is not null)
            warnings.Add("server.limits.maxDecodedBodyBytes is reserved and not enforced. The enforced whole-body budget is maxBufferedBodyBytes.");
        if (config.Server?.ResponseCompression is { Enabled: true })
        {
            warnings.Add(
                "server.responseCompression compresses HTTP/1.1 and HTTP/2-to-HTTP/1.1 responses only. " +
                "It is not applied on HTTP/2-to-HTTP/2, HTTP/3, or HTTP/1.1-to-HTTP/2 paths.");
        }

        if (config.Routes.Any(route => route.GrpcWeb))
        {
            warnings.Add(
                "routes[].grpcWeb translates binary application/grpc-web and application/grpc-web+proto POSTs (unary and " +
                "server-streaming) for an HTTP/2 origin, from HTTP/2 clients and from HTTP/1.1 clients on a translation bridge. " +
                "application/grpc-web-text is passed through untranslated, and an HTTP/1.1 origin is not translated.");
        }

        foreach (var listener in config.Listeners)
        {
            if (listener.AcceptProxyProtocol && listener.TrustedProxyProtocolSources is not { Count: > 0 })
            {
                warnings.Add(
                    $"Listener port {listener.Port}: acceptProxyProtocol is set with no trustedProxyProtocolSources. Any peer may send a preamble.");
            }

            if (listener.AltSvc is not null && listener.EnableHttp3 == false)
            {
                warnings.Add(
                    $"Listener port {listener.Port}: altSvc is set but HTTP/3 is off, so the header is not injected.");
            }

            if (string.Equals(listener.Type, "masque", StringComparison.OrdinalIgnoreCase)
                && listener.Masque?.RequireAuth == false
                && listener.Host is not ("127.0.0.1" or "::1" or "localhost"))
            {
                warnings.Add(
                    $"Listener port {listener.Port}: masque authentication is off on a non-loopback address.");
            }
        }

        return warnings;
    }

    private static void CollectClusterProxyProtocolWarnings(TwpConfig config, List<string> warnings)
    {
        var passthroughClusters = new HashSet<string>(StringComparer.Ordinal);
        foreach (var listener in config.Listeners.Where(l => l.Passthrough))
        {
            if (!string.IsNullOrEmpty(listener.SniDefaultClusterId))
            {
                passthroughClusters.Add(listener.SniDefaultClusterId);
            }

            foreach (var route in listener.SniRoutes ?? [])
            {
                if (!string.IsNullOrEmpty(route.ClusterId))
                {
                    passthroughClusters.Add(route.ClusterId);
                }
            }
        }

        foreach (var cluster in config.Clusters)
        {
            if (cluster.SendProxyProtocol is { } mode
                && mode.Equals("v2", StringComparison.OrdinalIgnoreCase)
                && !passthroughClusters.Contains(cluster.Id))
            {
                warnings.Add(
                    $"Cluster '{cluster.Id}' sets sendProxyProtocol v2, but no passthrough listener routes to it. " +
                    "HTTP origin connections are pooled across clients and never carry a PROXY header.");
            }
        }
    }

    private static bool IsClientCertificateModeSet(string? mode) =>
        !string.IsNullOrWhiteSpace(mode)
        && !mode.Equals("none", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    ///     A null type with a forward host is the classic transparent listener. An explicit type is not.
    /// </summary>
    private static bool IsTransparentListener(ListenerConfig listener) =>
        string.Equals(listener.Type, "transparent", StringComparison.OrdinalIgnoreCase)
        || (string.IsNullOrWhiteSpace(listener.Type) &&
            (!string.IsNullOrEmpty(listener.ForwardHost)
             || (listener.SniRoutes?.Count ?? 0) > 0
             || !string.IsNullOrEmpty(listener.SniDefaultClusterId)));
}
