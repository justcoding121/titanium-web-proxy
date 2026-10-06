using System.Net;
using System.Security.Authentication;
using Titanium.Web.Proxy.Abstractions.Clusters;
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
        "explicit", "transparent", "socks", "quic",
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
        ValidateRoutes(config.Routes, clusterIds, errors);
        ValidateListeners(config.Listeners, errors);
        ValidateServer(config.Server, errors);
        return errors;
    }

    private static HashSet<string> ValidateClusters(IEnumerable<ClusterConfig> clusters, List<string> errors)
    {
        var clusterIds = new HashSet<string>(StringComparer.Ordinal);
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
            }
        }

        return clusterIds;
    }

    private static void ValidateRoutes(
        IEnumerable<RouteConfig> routes,
        HashSet<string> clusterIds,
        List<string> errors)
    {
        foreach (var route in routes)
        {
            if (string.IsNullOrWhiteSpace(route.Id))
            {
                errors.Add("Route is missing Id.");
            }

            if (string.IsNullOrWhiteSpace(route.ClusterId) || !clusterIds.Contains(route.ClusterId))
            {
                errors.Add($"Route '{route.Id}' references unknown cluster '{route.ClusterId}'.");
            }

            if (route.Match is null)
            {
                errors.Add($"Route '{route.Id}' is missing Match.");
            }

            var limits = route.Limits;
            if (limits is null)
            {
                continue;
            }

            RequirePositiveIfSet(limits.RequestTimeoutSeconds, $"routes.{route.Id}.limits.requestTimeoutSeconds", errors);
            RequirePositiveIfSet(limits.IdleTimeoutSeconds, $"routes.{route.Id}.limits.idleTimeoutSeconds", errors);
            RequirePositiveIfSet(limits.MaxBufferedBodyBytes, $"routes.{route.Id}.limits.maxBufferedBodyBytes", errors);
            RequirePositiveIfSet(limits.MaxWebSocketFramePayloadBytes, $"routes.{route.Id}.limits.maxWebSocketFramePayloadBytes", errors);
        }
    }

    private static void ValidateListeners(IEnumerable<ListenerConfig> listeners, List<string> errors)
    {
        foreach (var listener in listeners)
        {
            if (listener.Port is < 1 or > 65535)
            {
                errors.Add($"Listener port {listener.Port} is out of range.");
            }

            if (!string.IsNullOrWhiteSpace(listener.Type) && !KnownListenerTypes.Contains(listener.Type))
            {
                errors.Add($"Listener type '{listener.Type}' is unknown (explicit, transparent, socks, quic).");
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

        ValidateTls(server.Tls, errors);
        ValidateTimeouts(server.Timeouts, errors);
        ValidatePooling(server.Pooling, errors);
        ValidateLimits(server.Limits, errors);
        ValidatePolicyModes(server.PolicyModes, errors);
        ValidateUpstream(server.Upstream, errors);
        ValidateCertificateManager(server.CertificateManager, errors);
        ValidateAccessLog(server.AccessLog, errors);
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
    public static IReadOnlyList<string> CollectWarnings(TwpConfig config)
    {
        var warnings = new List<string>();
        var limits = config.Server?.Limits;
        if (limits?.MaxDecompressionRatio is not null)
            warnings.Add("server.limits.maxDecompressionRatio is reserved and not enforced. Body expansion is bounded by maxBufferedBodyBytes.");
        if (limits?.MaxEncodedBodyBytes is not null)
            warnings.Add("server.limits.maxEncodedBodyBytes is reserved and not enforced. The enforced whole-body budget is maxBufferedBodyBytes.");
        if (limits?.MaxDecodedBodyBytes is not null)
            warnings.Add("server.limits.maxDecodedBodyBytes is reserved and not enforced. The enforced whole-body budget is maxBufferedBodyBytes.");
        return warnings;
    }
}
