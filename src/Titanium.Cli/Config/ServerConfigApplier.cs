using System.Globalization;
using System.Net;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using Titanium.Web.Proxy;
using Titanium.Web.Proxy.Configuration.Models;
using Titanium.Web.Proxy.Models;
using Titanium.Web.Proxy.Network;
using Titanium.Web.Proxy.Options;

namespace Titanium.Cli.Config;

/// <summary>
///     Maps <see cref="ServerConfig"/> onto a live <see cref="ProxyServer"/>.
///     Apply order: profile (if set), then individual overlays. Null fields leave current values.
/// </summary>
internal static class ServerConfigApplier
{
    public static void Apply(ProxyServer proxy, ServerConfig? server)
    {
        if (server is null)
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(server.Profile) &&
            Enum.TryParse<ProxyProfile>(server.Profile, ignoreCase: true, out var profile))
        {
            proxy.Profile = profile;
        }

        ApplyProtocolFlags(proxy, server);
        ApplyTimeouts(proxy, server.Timeouts);
        ApplyPooling(proxy, server.Pooling);
        ApplyLimits(proxy, server.Limits);
        ApplyPolicyModes(proxy, server.PolicyModes);
        ApplyTls(proxy, server.Tls);
        ApplyUpstream(proxy, server.Upstream);
        ApplyAuth(proxy, server.Auth);
        ApplyCertificateManager(proxy, server.CertificateManager);
    }

    private static void ApplyProtocolFlags(ProxyServer proxy, ServerConfig server)
    {
        if (server.EnableHttp2 is bool http2)
        {
            proxy.EnableHttp2 = http2;
        }

        if (server.EnableHttp3 is bool http3)
        {
            if (http3)
            {
                proxy.TryEnableHttp3IfSupported();
            }
            else
            {
                proxy.SetHttp3Enabled(false);
            }
        }

        if (server.EnableRfc8441 is bool rfc8441)
        {
            proxy.EnableRfc8441 = rfc8441;
        }

        if (server.EnableQpackDynamicTable is bool qpack)
        {
            proxy.EnableQpackDynamicTable = qpack;
        }

        if (server.EnableHttpsSvcbDnsDiscovery is bool svcb)
        {
            proxy.EnableHttpsSvcbDnsDiscovery = svcb;
        }

        if (server.Enable100ContinueBehaviour is bool expect100)
        {
            proxy.Enable100ContinueBehaviour = expect100;
        }

        if (server.CompatibilityMode100Continue is bool compat100)
        {
            proxy.CompatibilityMode100Continue = compat100;
        }

        if (server.EnableWinAuth is bool winAuth)
        {
            proxy.EnableWinAuth = winAuth;
        }

        if (server.EnableRequestTimingCapture is bool timing)
        {
            proxy.EnableRequestTimingCapture = timing;
        }

        if (server.EnableHttpInterception is bool intercept)
        {
            proxy.EnableHttpInterception = intercept;
        }

        if (!string.IsNullOrWhiteSpace(server.OriginHttpVersionPolicy) &&
            Enum.TryParse<OriginHttpVersionPolicy>(server.OriginHttpVersionPolicy, ignoreCase: true, out var originPolicy))
        {
            proxy.OriginHttpVersionPolicy = originPolicy;
        }

        if (server.ViaHeaderPseudonym is not null)
        {
            proxy.ViaHeaderPseudonym = server.ViaHeaderPseudonym;
        }

        if (server.BlockPrivateNetworkDestinations is bool blockPrivate)
        {
            proxy.BlockPrivateNetworkDestinations = blockPrivate;
        }

        if (!string.IsNullOrWhiteSpace(server.CheckCertificateRevocation) &&
            Enum.TryParse<X509RevocationMode>(server.CheckCertificateRevocation, ignoreCase: true, out var revocation))
        {
            proxy.CheckCertificateRevocation = revocation;
        }

        if (!string.IsNullOrWhiteSpace(server.DnsServerEndPoint) &&
            TryParseEndPoint(server.DnsServerEndPoint, out var dns))
        {
            proxy.DnsServerEndPoint = dns;
        }
    }

    private static void ApplyTimeouts(ProxyServer proxy, TimeoutsConfig? timeouts)
    {
        if (timeouts is null)
        {
            return;
        }

        if (timeouts.ConnectionTimeOutSeconds is int connection)
        {
            proxy.ConnectionTimeOutSeconds = connection;
        }

        if (timeouts.ConnectTimeOutSeconds is int connect)
        {
            proxy.ConnectTimeOutSeconds = connect;
        }

        if (timeouts.ClientHeaderTimeoutSeconds is int clientHeader)
        {
            proxy.ClientHeaderTimeoutSeconds = clientHeader;
        }

        if (timeouts.ResponseHeaderTimeoutSeconds is int responseHeader)
        {
            proxy.ResponseHeaderTimeoutSeconds = responseHeader;
        }

        if (timeouts.IdleReadTimeoutSeconds is int idleRead)
        {
            proxy.IdleReadTimeoutSeconds = idleRead;
        }

        if (timeouts.IdleWriteTimeoutSeconds is int idleWrite)
        {
            proxy.IdleWriteTimeoutSeconds = idleWrite;
        }

        if (timeouts.RequestTimeoutSeconds is int request)
        {
            proxy.RequestTimeoutSeconds = request;
        }

        if (timeouts.NetworkFailureRetryAttempts is int retries)
        {
            proxy.NetworkFailureRetryAttempts = retries;
        }
    }

    private static void ApplyPooling(ProxyServer proxy, PoolingConfig? pooling)
    {
        if (pooling is null)
        {
            return;
        }

        if (pooling.EnableConnectionPool is bool pool)
        {
            proxy.EnableConnectionPool = pool;
        }

        if (pooling.EnableTcpServerConnectionPrefetch is bool prefetch)
        {
            proxy.EnableTcpServerConnectionPrefetch = prefetch;
        }

        if (pooling.EnableIpv6UnreachableSoftSkip is bool ipv6)
        {
            proxy.EnableIpv6UnreachableSoftSkip = ipv6;
        }

        if (pooling.MaxCachedConnections is int maxCached)
        {
            proxy.MaxCachedConnections = maxCached;
        }

        if (pooling.MaxConcurrentHttp11HttpsOriginCreates is int maxCreates)
        {
            proxy.MaxConcurrentHttp11HttpsOriginCreates = maxCreates;
        }

        if (pooling.MaxConcurrentClientConnections is int maxClients)
        {
            proxy.MaxConcurrentClientConnections = maxClients;
        }

        if (pooling.NoDelay is bool noDelay)
        {
            proxy.NoDelay = noDelay;
        }

        if (pooling.EnableTcpKeepAlive is bool keepAlive)
        {
            proxy.EnableTcpKeepAlive = keepAlive;
        }

        if (pooling.TcpTimeWaitSeconds is int timeWait)
        {
            proxy.TcpTimeWaitSeconds = timeWait;
        }

        if (pooling.ListenerBackLog is int backLog)
        {
            proxy.ListenerBackLog = backLog;
        }

        if (pooling.ReuseSocket is bool reuse)
        {
            proxy.ReuseSocket = reuse;
        }

        if (pooling.ThreadPoolWorkerThread is int workers)
        {
            proxy.ThreadPoolWorkerThread = workers;
        }
    }

    private static void ApplyLimits(ProxyServer proxy, LimitsConfig? limits)
    {
        if (limits is null)
        {
            return;
        }

        var current = proxy.ResourceLimits;
        var duration = limits.MaxOpenHeaderBlockDurationSeconds is int seconds
            ? TimeSpan.FromSeconds(seconds)
            : current.MaxOpenHeaderBlockDuration;

        var built = ProxyResourceLimits.Create(
            limits.MaxHeaderLineBytes ?? current.MaxHeaderLineBytes,
            limits.MaxHeaderCount ?? current.MaxHeaderCount,
            limits.MaxHeaderAggregateBytes ?? current.MaxHeaderAggregateBytes,
            limits.MaxEncodedBodyBytes ?? current.MaxEncodedBodyBytes,
            limits.MaxDecodedBodyBytes ?? current.MaxDecodedBodyBytes,
            limits.MaxDecompressionRatio ?? current.MaxDecompressionRatio,
            limits.MaxConcurrentClients ?? current.MaxConcurrentClients,
            limits.MaxConcurrentStreamsPerConnection ?? current.MaxConcurrentStreamsPerConnection,
            limits.MaxPeerInitiatedIncompleteStreamResets ?? current.MaxPeerInitiatedIncompleteStreamResets,
            limits.MaxOpenHeaderBlockFrames ?? current.MaxOpenHeaderBlockFrames,
            duration,
            limits.ConnectionPoolingEnabled ?? current.ConnectionPoolingEnabled,
            limits.MaxCachedConnectionsPerHost ?? current.MaxCachedConnectionsPerHost,
            limits.MaxCertificateCacheEntries ?? current.MaxCertificateCacheEntries);

        built = built.WithMaxOriginHttp2ConnectionsPerAuthority(
            limits.MaxOriginHttp2ConnectionsPerAuthority ?? current.MaxOriginHttp2ConnectionsPerAuthority);
        built = built.WithCertificateCacheBounds(
            built.MaxCertificateCacheEntries,
            limits.MaxCertificateDiskCacheEntries ?? current.MaxCertificateDiskCacheEntries);

        proxy.ResourceLimits = built;

        if (limits.MaxBufferedBodyBytes is int buffered)
        {
            proxy.MaxBufferedBodyBytes = buffered;
        }

        if (limits.MaxDecodedHeaderListBytes is int headerList)
        {
            proxy.MaxDecodedHeaderListBytes = headerList;
        }

        if (limits.MaxWebSocketFramePayloadBytes is int wsFrame)
        {
            proxy.MaxWebSocketFramePayloadBytes = wsFrame;
        }
    }

    private static void ApplyPolicyModes(ProxyServer proxy, PolicyModesConfig? policy)
    {
        if (policy is null)
        {
            return;
        }

        var current = proxy.PolicyModes;
        var modes = ProxyPolicyModes.Create(
            ParsePolicyMode(policy.BodyBudget, current[PolicyFamily.BodyBudget]),
            ParsePolicyMode(policy.DecompressionRatio, current[PolicyFamily.DecompressionRatio]),
            ParsePolicyMode(policy.HeaderLimits, current[PolicyFamily.HeaderLimits]),
            ParsePolicyMode(policy.AdmissionControl, current[PolicyFamily.AdmissionControl]),
            ParsePolicyMode(policy.Http2AbuseBudget, current[PolicyFamily.Http2AbuseBudget]));

        if (policy.AllowAmbiguousFraming == true)
        {
            modes = modes.WithAllowAmbiguousFramingEnabled();
        }

        proxy.PolicyModes = modes;
    }

    private static PolicyMode ParsePolicyMode(string? value, PolicyMode fallback) =>
        !string.IsNullOrWhiteSpace(value) && Enum.TryParse<PolicyMode>(value, ignoreCase: true, out var mode)
            ? mode
            : fallback;

    private static void ApplyTls(ProxyServer proxy, TlsConfig? tls)
    {
        if (tls is null)
        {
            return;
        }

        if (tls.SupportedSslProtocols is { Count: > 0 } clientProtocols &&
            TryParseSslProtocols(clientProtocols, out var client))
        {
            proxy.SupportedSslProtocols = client;
        }

        if (tls.SupportedServerSslProtocols is { Count: > 0 } serverProtocols &&
            TryParseSslProtocols(serverProtocols, out var server))
        {
            proxy.SupportedServerSslProtocols = server;
        }
    }

    private static void ApplyUpstream(ProxyServer proxy, UpstreamConfig? upstream)
    {
        if (upstream is null)
        {
            return;
        }

        if (upstream.ForwardToUpstreamGateway is bool forward)
        {
            proxy.ForwardToUpstreamGateway = forward;
        }

        if (!string.IsNullOrWhiteSpace(upstream.UpstreamProxyConfigurationScript) &&
            Uri.TryCreate(upstream.UpstreamProxyConfigurationScript, UriKind.Absolute, out var pac))
        {
            proxy.UpstreamProxyConfigurationScript = pac;
        }

        if (upstream.HttpProxy is not null)
        {
            proxy.UpStreamHttpProxy = ToExternalProxy(upstream.HttpProxy);
        }

        if (upstream.HttpsProxy is not null)
        {
            proxy.UpStreamHttpsProxy = ToExternalProxy(upstream.HttpsProxy);
        }

        if (!string.IsNullOrWhiteSpace(upstream.UpStreamEndPoint) &&
            TryParseEndPoint(upstream.UpStreamEndPoint, out var ep))
        {
            proxy.UpStreamEndPoint = ep;
        }

        if (!string.IsNullOrWhiteSpace(upstream.UpStreamEndPointIPv4) &&
            TryParseEndPoint(upstream.UpStreamEndPointIPv4, out var ep4))
        {
            proxy.UpStreamEndPointIPv4 = ep4;
        }

        if (!string.IsNullOrWhiteSpace(upstream.UpStreamEndPointIPv6) &&
            TryParseEndPoint(upstream.UpStreamEndPointIPv6, out var ep6))
        {
            proxy.UpStreamEndPointIPv6 = ep6;
        }
    }

    private static ExternalProxy? ToExternalProxy(ExternalProxyConfig config)
    {
        if (string.IsNullOrWhiteSpace(config.HostName) || config.Port is < 1 or > 65535)
        {
            return null;
        }

        var proxy = new ExternalProxy(config.HostName, config.Port)
        {
            UserName = config.UserName,
            Password = config.Password,
            UseDefaultCredentials = config.UseDefaultCredentials,
            BypassLocalhost = config.BypassLocalhost,
            ProxyDnsRequests = config.ProxyDnsRequests,
        };

        if (Enum.TryParse<ExternalProxyType>(config.ProxyType, ignoreCase: true, out var type))
        {
            proxy.ProxyType = type;
        }

        if (config.NextHop is not null)
        {
            proxy.NextHop = ToExternalProxy(config.NextHop);
        }

        return proxy;
    }

    private static void ApplyAuth(ProxyServer proxy, AuthConfig? auth)
    {
        if (auth is null)
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(auth.ProxyAuthenticationRealm))
        {
            proxy.ProxyAuthenticationRealm = auth.ProxyAuthenticationRealm;
        }

        if (auth.ProxyAuthenticationSchemes is { Count: > 0 } schemes)
        {
            proxy.ProxyAuthenticationSchemes = schemes.ToArray();
        }
    }

    private static void ApplyCertificateManager(ProxyServer proxy, CertificateManagerConfig? certs)
    {
        if (certs is null)
        {
            return;
        }

        var cm = proxy.CertificateManager;

        if (!string.IsNullOrWhiteSpace(certs.CertificateEngine) &&
            Enum.TryParse<CertificateEngine>(certs.CertificateEngine, ignoreCase: true, out var engine))
        {
            cm.CertificateEngine = engine;
        }

        if (!string.IsNullOrWhiteSpace(certs.LeafCertificateKeyAlgorithm) &&
            Enum.TryParse<CertificateKeyAlgorithm>(certs.LeafCertificateKeyAlgorithm, ignoreCase: true, out var algo))
        {
            cm.LeafCertificateKeyAlgorithm = algo;
        }

        if (certs.PfxFilePath is not null)
        {
            cm.PfxFilePath = certs.PfxFilePath;
        }

        if (certs.PfxPassword is not null)
        {
            cm.PfxPassword = certs.PfxPassword;
        }

        if (certs.OverwritePfxFile is bool overwrite)
        {
            cm.OverwritePfxFile = overwrite;
        }

        if (certs.CertificateValidDays is int validDays)
        {
            cm.CertificateValidDays = validDays;
        }

        if (certs.CertificateGraceDays is int graceDays)
        {
            cm.CertificateGraceDays = graceDays;
        }

        if (certs.CertificateCacheTimeOutMinutes is int cacheMinutes)
        {
            cm.CertificateCacheTimeOutMinutes = cacheMinutes;
        }

        if (!string.IsNullOrWhiteSpace(certs.RootCertificateName))
        {
            cm.RootCertificateName = certs.RootCertificateName;
        }

        if (!string.IsNullOrWhiteSpace(certs.RootCertificateIssuerName))
        {
            cm.RootCertificateIssuerName = certs.RootCertificateIssuerName;
        }

        if (certs.SaveFakeCertificates is bool saveFakes)
        {
            cm.SaveFakeCertificates = saveFakes;
        }

        if (certs.DisableWildCardCertificates is bool disableWildcard)
        {
            cm.DisableWildCardCertificates = disableWildcard;
        }
    }

    internal static bool TryParseSslProtocols(IEnumerable<string> names, out SslProtocols protocols)
    {
        protocols = SslProtocols.None;
        var any = false;
        foreach (var name in names)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            if (!Enum.TryParse<SslProtocols>(name.Trim(), ignoreCase: true, out var part))
            {
                protocols = SslProtocols.None;
                return false;
            }

            protocols |= part;
            any = true;
        }

        return any;
    }

    internal static bool TryParseEndPoint(string value, out IPEndPoint endPoint)
    {
        endPoint = null!;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        // Prefer URI-style for IPv6: [2001:db8::1]:443
        if (value.StartsWith('[') && value.Contains(']', StringComparison.Ordinal))
        {
            var close = value.IndexOf(']');
            var hostPart = value[1..close];
            var portPart = value[(close + 1)..].TrimStart(':');
            if (IPAddress.TryParse(hostPart, out var ip6) &&
                int.TryParse(portPart, NumberStyles.Integer, CultureInfo.InvariantCulture, out var port6) &&
                port6 is >= 0 and <= 65535)
            {
                endPoint = new IPEndPoint(ip6, port6);
                return true;
            }

            return false;
        }

        var lastColon = value.LastIndexOf(':');
        if (lastColon <= 0 || lastColon == value.Length - 1)
        {
            return false;
        }

        var host = value[..lastColon];
        var portText = value[(lastColon + 1)..];
        if (!IPAddress.TryParse(host, out var ip) ||
            !int.TryParse(portText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var port) ||
            port is < 0 or > 65535)
        {
            return false;
        }

        endPoint = new IPEndPoint(ip, port);
        return true;
    }
}
