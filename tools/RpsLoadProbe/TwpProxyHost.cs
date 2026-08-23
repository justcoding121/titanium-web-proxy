using System.Net;
using System.Net.Quic;
using Titanium.Web.Proxy.Models;
using Titanium.Web.Proxy.Network;
using Titanium.Web.Proxy.RpsLoadProbe.Support;

namespace Titanium.Web.Proxy.RpsLoadProbe;

internal sealed class TwpProxyHost : IDisposable
{
    private readonly ProxyServer proxyServer;

    public int Port { get; }
    public string ListenUrl { get; }
    public bool IsExplicitProxy { get; }
    public ProxyServer Server => proxyServer;

    private TwpProxyHost(ProxyServer proxyServer, int port, string listenUrl, bool isExplicitProxy)
    {
        this.proxyServer = proxyServer;
        Port = port;
        ListenUrl = listenUrl;
        IsExplicitProxy = isExplicitProxy;
    }

    public static TwpProxyHost StartReverseHttp1(int originHttpPort)
    {
        var proxy = CreateBaseProxy(enableHttp2: false, enableHttp3: false);
        var endPoint = new TransparentProxyEndPoint(IPAddress.Loopback, 0, decryptSsl: false)
        {
            ForwardHost = "127.0.0.1",
            ForwardPort = originHttpPort,
            // Must be explicit: cleartext H1 uses !ForwardCleartext as originIsHttps.
            ForwardCleartext = true,
            MaxCachedConnections = 256
        };
        proxy.AddEndPoint(endPoint);
        proxy.Start();
        return new TwpProxyHost(proxy, endPoint.Port, $"http://127.0.0.1:{endPoint.Port}/", isExplicitProxy: false);
    }

    /// <summary>
    /// TLS-terminating reverse HTTP/1 — client TLS to cleartext HTTP origin (industry-standard topology).
    /// Cleartext <see cref="StartReverseHttp1"/> is kept as a separate raw-TCP baseline.
    /// </summary>
    public static TwpProxyHost StartReverseHttp1Tls(int originHttpPort)
    {
        var proxy = CreateBaseProxy(enableHttp2: false, enableHttp3: false);
        ConfigureSharedTestCa(proxy);

        var endPoint = new TransparentProxyEndPoint(IPAddress.Loopback, 0, decryptSsl: true)
        {
            ForwardHost = "127.0.0.1",
            ForwardPort = originHttpPort,
            ForwardCleartext = true,
            GenericCertificateName = "localhost",
            MaxCachedConnections = 256
        };
        proxy.AddEndPoint(endPoint);
        proxy.Start();
        WarmTlsTerminateCertificate(proxy, endPoint, "localhost");
        return new TwpProxyHost(proxy, endPoint.Port, $"https://127.0.0.1:{endPoint.Port}/", isExplicitProxy: false);
    }

    /// <summary>
    /// Cleartext reverse: client HTTP/1 plain → HTTPS HTTP/1 origin (outbound TLS only; no client decrypt).
    /// Completes the H1 plain/TLS × reverse square with <see cref="StartReverseHttp1"/> /
    /// <see cref="StartReverseHttp1Tls"/>; twin of <see cref="StartReverseH2c"/> for H1.
    /// </summary>
    public static TwpProxyHost StartReverseHttp1ToHttps(int originHttpsPort)
    {
        var proxy = CreateBaseProxy(enableHttp2: false, enableHttp3: false);
        ConfigureSharedTestCa(proxy);

        var endPoint = new TransparentProxyEndPoint(IPAddress.Loopback, 0, decryptSsl: false)
        {
            ForwardHost = "127.0.0.1",
            ForwardPort = originHttpsPort,
            ForwardCleartext = false,
            GenericCertificateName = "localhost",
            MaxCachedConnections = 256
        };
        endPoint.BeforeHttpAuthenticate += (_, args) =>
        {
            args.UpstreamHttpProtocol = UpstreamHttpProtocol.Http11;
            return Task.CompletedTask;
        };
        proxy.AddEndPoint(endPoint);
        proxy.Start();
        return new TwpProxyHost(proxy, endPoint.Port, $"http://127.0.0.1:{endPoint.Port}/", isExplicitProxy: false);
    }

    /// <summary>
    /// TLS-terminating reverse HTTP/2 matching native reverse peer: client h2 TLS → H2→H1 bridge → cleartext HTTP/1 origin.
    /// </summary>
    public static TwpProxyHost StartReverseHttp2Cleartext(int originHttpPort)
    {
        var proxy = CreateBaseProxy(enableHttp2: true, enableHttp3: false);
        ConfigureSharedTestCa(proxy);

        var endPoint = new TransparentProxyEndPoint(IPAddress.Loopback, 0, decryptSsl: true)
        {
            ForwardHost = "127.0.0.1",
            ForwardPort = originHttpPort,
            ForwardCleartext = true,
            GenericCertificateName = "localhost",
            MaxCachedConnections = 256
        };
        endPoint.BeforeSslAuthenticate += (_, args) =>
        {
            args.UpstreamHttpProtocol = UpstreamHttpProtocol.Http11;
            args.AllowHttpProtocolTranslation = true;
            return Task.CompletedTask;
        };
        proxy.AddEndPoint(endPoint);
        proxy.Start();
        WarmTlsTerminateCertificate(proxy, endPoint, "localhost");
        return new TwpProxyHost(proxy, endPoint.Port, $"https://127.0.0.1:{endPoint.Port}/", isExplicitProxy: false);
    }

    /// <summary>
    /// TLS-terminating reverse HTTP/2 → prior-knowledge h2c: client h2 TLS → cleartext HTTP/2 origin.
    /// </summary>
    public static TwpProxyHost StartReverseHttp2ToH2c(int originHttpPort)
    {
        var proxy = CreateBaseProxy(enableHttp2: true, enableHttp3: false);
        ConfigureSharedTestCa(proxy);

        var endPoint = new TransparentProxyEndPoint(IPAddress.Loopback, 0, decryptSsl: true)
        {
            ForwardHost = "127.0.0.1",
            ForwardPort = originHttpPort,
            ForwardCleartext = true,
            GenericCertificateName = "localhost",
            MaxCachedConnections = 256
        };
        endPoint.BeforeSslAuthenticate += (_, args) =>
        {
            args.UpstreamHttpProtocol = UpstreamHttpProtocol.Http2;
            return Task.CompletedTask;
        };
        proxy.AddEndPoint(endPoint);
        proxy.Start();
        return new TwpProxyHost(proxy, endPoint.Port, $"https://127.0.0.1:{endPoint.Port}/", isExplicitProxy: false);
    }

    /// <summary>Cleartext reverse: client prior-knowledge h2c → cleartext HTTP/2 origin.</summary>
    public static TwpProxyHost StartReverseH2cToH2c(int originHttpPort)
    {
        var proxy = CreateBaseProxy(enableHttp2: true, enableHttp3: false);
        ConfigureSharedTestCa(proxy);

        var endPoint = new TransparentProxyEndPoint(IPAddress.Loopback, 0, decryptSsl: false)
        {
            ForwardHost = "127.0.0.1",
            ForwardPort = originHttpPort,
            ForwardCleartext = true,
            GenericCertificateName = "localhost",
            MaxCachedConnections = 256
        };
        endPoint.BeforeHttpAuthenticate += (_, args) =>
        {
            args.UpstreamHttpProtocol = UpstreamHttpProtocol.Http2;
            return Task.CompletedTask;
        };
        proxy.AddEndPoint(endPoint);
        proxy.Start();
        return new TwpProxyHost(proxy, endPoint.Port, $"http://127.0.0.1:{endPoint.Port}/", isExplicitProxy: false);
    }

    /// <summary>Cleartext reverse: client prior-knowledge h2c → H2→H1 bridge → cleartext HTTP/1 origin.</summary>
    public static TwpProxyHost StartReverseH2cToH1(int originHttpPort)
    {
        var proxy = CreateBaseProxy(enableHttp2: true, enableHttp3: false);
        ConfigureSharedTestCa(proxy);

        var endPoint = new TransparentProxyEndPoint(IPAddress.Loopback, 0, decryptSsl: false)
        {
            ForwardHost = "127.0.0.1",
            ForwardPort = originHttpPort,
            ForwardCleartext = true,
            GenericCertificateName = "localhost",
            MaxCachedConnections = 256
        };
        endPoint.BeforeHttpAuthenticate += (_, args) =>
        {
            args.UpstreamHttpProtocol = UpstreamHttpProtocol.Http11;
            args.AllowHttpProtocolTranslation = true;
            return Task.CompletedTask;
        };
        proxy.AddEndPoint(endPoint);
        proxy.Start();
        return new TwpProxyHost(proxy, endPoint.Port, $"http://127.0.0.1:{endPoint.Port}/", isExplicitProxy: false);
    }

    /// <summary>Cleartext reverse: client prior-knowledge h2c → HTTPS origin with ALPN h2.</summary>
    public static TwpProxyHost StartReverseH2c(int originHttpsPort)
    {
        var proxy = CreateBaseProxy(enableHttp2: true, enableHttp3: false);
        ConfigureSharedTestCa(proxy);

        var endPoint = new TransparentProxyEndPoint(IPAddress.Loopback, 0, decryptSsl: false)
        {
            ForwardHost = "127.0.0.1",
            ForwardPort = originHttpsPort,
            ForwardCleartext = false,
            GenericCertificateName = "localhost",
            MaxCachedConnections = 256
        };
        endPoint.BeforeHttpAuthenticate += (_, args) =>
        {
            args.UpstreamHttpProtocol = UpstreamHttpProtocol.Http2;
            return Task.CompletedTask;
        };
        proxy.AddEndPoint(endPoint);
        proxy.Start();
        return new TwpProxyHost(proxy, endPoint.Port, $"http://127.0.0.1:{endPoint.Port}/", isExplicitProxy: false);
    }

    /// <summary>Cleartext reverse: client prior-knowledge h2c → H2→H3 bridge → QUIC/h3 origin.</summary>
    public static TwpProxyHost StartReverseH2cToH3(int originQuicPort)
    {
        if (!QuicListener.IsSupported)
            throw new PlatformNotSupportedException("QuicListener is not supported on this platform.");

        var proxy = CreateBaseProxy(enableHttp2: true, enableHttp3: true);
        ConfigureSharedTestCa(proxy);

        var endPoint = new TransparentProxyEndPoint(IPAddress.Loopback, 0, decryptSsl: false)
        {
            ForwardHost = "localhost",
            ForwardPort = originQuicPort,
            ForwardCleartext = false,
            GenericCertificateName = "localhost",
            MaxCachedConnections = 256
        };
        endPoint.BeforeHttpAuthenticate += (_, args) =>
        {
            args.UpstreamHttpProtocol = UpstreamHttpProtocol.Http3;
            args.AllowHttpProtocolTranslation = true;
            return Task.CompletedTask;
        };
        proxy.AddEndPoint(endPoint);
        proxy.Start();
        return new TwpProxyHost(proxy, endPoint.Port, $"http://127.0.0.1:{endPoint.Port}/", isExplicitProxy: false);
    }

    public static TwpProxyHost StartReverseHttp2(int originHttpsPort)
    {
        var proxy = CreateBaseProxy(enableHttp2: true, enableHttp3: false);
        ConfigureSharedTestCa(proxy);

        // Native h2↔h2 MITM to an HTTPS origin. TLS-terminate→cleartext (ForwardCleartext + H1 bridge)
        // matches native reverse peer topology but the per-stream H2→H1 bridge still errors under saturation;
        // keep the zero-error native path for publishable numbers until that bridge is hardened.
        var endPoint = new TransparentProxyEndPoint(IPAddress.Loopback, 0, decryptSsl: true)
        {
            ForwardHost = "127.0.0.1",
            ForwardPort = originHttpsPort,
            GenericCertificateName = "localhost"
        };
        proxy.AddEndPoint(endPoint);
        proxy.Start();
        return new TwpProxyHost(proxy, endPoint.Port, $"https://127.0.0.1:{endPoint.Port}/", isExplicitProxy: false);
    }

    public static TwpProxyHost StartReverseHttp3(int originHttpsPort)
    {
        if (!QuicListener.IsSupported)
            throw new PlatformNotSupportedException("QuicListener is not supported on this platform.");

        var proxy = CreateBaseProxy(enableHttp2: true, enableHttp3: true);
        ConfigureSharedTestCa(proxy);

        var endPoint = new TransparentProxyEndPoint(IPAddress.Loopback, 0, decryptSsl: true)
        {
            EnableHttp3 = true,
            ForwardHost = "localhost",
            ForwardPort = originHttpsPort,
            GenericCertificateName = "localhost",
            MaxInboundBidirectionalStreams = 256,
            MaxCachedConnections = 256
        };
        endPoint.BeforeQuicAuthenticate += (_, args) =>
        {
            args.UpstreamHttpProtocol = UpstreamHttpProtocol.Http3;
            return Task.CompletedTask;
        };
        proxy.AddEndPoint(endPoint);
        proxy.Start();
        WarmTlsTerminateCertificate(proxy, endPoint, "localhost");
        return new TwpProxyHost(proxy, endPoint.Port, $"https://localhost:{endPoint.Port}/", isExplicitProxy: false);
    }

    /// <summary>
    /// Dual-listen reverse: client QUIC/h3 (or TCP H1/H2) TLS terminate → cleartext HTTP/1 origin.
    /// </summary>
    public static TwpProxyHost StartReverseHttp3Cleartext(int originHttpPort)
    {
        if (!QuicListener.IsSupported)
            throw new PlatformNotSupportedException("QuicListener is not supported on this platform.");

        var proxy = CreateBaseProxy(enableHttp2: true, enableHttp3: true);
        ConfigureSharedTestCa(proxy);

        var endPoint = new TransparentProxyEndPoint(IPAddress.Loopback, 0, decryptSsl: true)
        {
            EnableHttp3 = true,
            ForwardHost = "127.0.0.1",
            ForwardPort = originHttpPort,
            ForwardCleartext = true,
            GenericCertificateName = "localhost",
            MaxInboundBidirectionalStreams = 256,
            MaxCachedConnections = 256
        };
        endPoint.BeforeQuicAuthenticate += (_, args) =>
        {
            args.UpstreamHttpProtocol = UpstreamHttpProtocol.Http11;
            return Task.CompletedTask;
        };
        endPoint.BeforeSslAuthenticate += (_, args) =>
        {
            args.UpstreamHttpProtocol = UpstreamHttpProtocol.Http11;
            args.AllowHttpProtocolTranslation = true;
            return Task.CompletedTask;
        };
        endPoint.BeforeQuicAuthenticate += (_, args) =>
        {
            args.UpstreamHttpProtocol = UpstreamHttpProtocol.Http11;
            return Task.CompletedTask;
        };
        proxy.AddEndPoint(endPoint);
        proxy.Start();
        WarmTlsTerminateCertificate(proxy, endPoint, "localhost");
        return new TwpProxyHost(proxy, endPoint.Port, $"https://localhost:{endPoint.Port}/", isExplicitProxy: false);
    }

    /// <summary>
    /// Client H2 TLS → H2→H1 bridge → origin HTTPS HTTP/1 (MITM: decrypt client, TLS to origin).
    /// </summary>
    /// <summary>
    /// Client H1 TLS → re-encrypt → origin HTTPS HTTP/1 (transparent dual-crypto). Fair twin of
    /// <see cref="StartReverseHttp1Tls"/> for MITM÷pass-through ratios; unlike
    /// <see cref="StartHttpsMitm"/> this avoids explicit CONNECT overhead.
    /// </summary>
    public static TwpProxyHost StartReverseHttp1Mitm(int originHttpsPort)
    {
        var proxy = CreateBaseProxy(enableHttp2: false, enableHttp3: false);
        ConfigureSharedTestCa(proxy);

        var endPoint = new TransparentProxyEndPoint(IPAddress.Loopback, 0, decryptSsl: true)
        {
            ForwardHost = "127.0.0.1",
            ForwardPort = originHttpsPort,
            ForwardCleartext = false,
            GenericCertificateName = "localhost",
            MaxCachedConnections = 256
        };
        proxy.AddEndPoint(endPoint);
        proxy.Start();
        WarmTlsTerminateCertificate(proxy, endPoint, "localhost");
        return new TwpProxyHost(proxy, endPoint.Port, $"https://127.0.0.1:{endPoint.Port}/", isExplicitProxy: false);
    }

    public static TwpProxyHost StartMitmHttp2ToHttp1(int originHttpsPort)
    {
        var proxy = CreateBaseProxy(enableHttp2: true, enableHttp3: false);
        ConfigureSharedTestCa(proxy);

        var endPoint = new TransparentProxyEndPoint(IPAddress.Loopback, 0, decryptSsl: true)
        {
            ForwardHost = "127.0.0.1",
            ForwardPort = originHttpsPort,
            GenericCertificateName = "localhost",
            MaxCachedConnections = 256
        };
        endPoint.BeforeSslAuthenticate += (_, args) =>
        {
            args.UpstreamHttpProtocol = UpstreamHttpProtocol.Http11;
            args.AllowHttpProtocolTranslation = true;
            return Task.CompletedTask;
        };
        proxy.AddEndPoint(endPoint);
        proxy.Start();
        WarmTlsTerminateCertificate(proxy, endPoint, "localhost");
        return new TwpProxyHost(proxy, endPoint.Port, $"https://127.0.0.1:{endPoint.Port}/", isExplicitProxy: false);
    }

    /// <summary>
    /// Dual-listen reverse: client H3 (HttpClient) → decrypt → origin HTTPS HTTP/1 (MITM dual-crypto).
    /// Fair twin of <see cref="StartReverseHttp3Cleartext"/>; same generator as reverse H3 arms.
    /// </summary>
    public static TwpProxyHost StartMitmHttp3ToHttp1(int originHttpsPort)
    {
        if (!QuicListener.IsSupported)
            throw new PlatformNotSupportedException("QuicListener is not supported on this platform.");

        var proxy = CreateBaseProxy(enableHttp2: true, enableHttp3: true);
        ConfigureSharedTestCa(proxy);

        var endPoint = new TransparentProxyEndPoint(IPAddress.Loopback, 0, decryptSsl: true)
        {
            EnableHttp3 = true,
            ForwardHost = "127.0.0.1",
            ForwardPort = originHttpsPort,
            ForwardCleartext = false,
            GenericCertificateName = "localhost",
            MaxInboundBidirectionalStreams = 256,
            MaxCachedConnections = 256
        };
        endPoint.BeforeQuicAuthenticate += (_, args) =>
        {
            args.UpstreamHttpProtocol = UpstreamHttpProtocol.Http11;
            args.AllowHttpProtocolTranslation = true;
            return Task.CompletedTask;
        };
        endPoint.BeforeSslAuthenticate += (_, args) =>
        {
            args.UpstreamHttpProtocol = UpstreamHttpProtocol.Http11;
            args.AllowHttpProtocolTranslation = true;
            return Task.CompletedTask;
        };
        proxy.AddEndPoint(endPoint);
        proxy.Start();
        WarmTlsTerminateCertificate(proxy, endPoint, "localhost");
        return new TwpProxyHost(proxy, endPoint.Port, $"https://localhost:{endPoint.Port}/", isExplicitProxy: false);
    }

    public static TwpProxyHost StartHttpsMitm(int? maxCachedConnections = null)
    {
        var proxy = CreateBaseProxy(enableHttp2: true, enableHttp3: false, maxCachedConnections);
        ConfigureSharedTestCa(proxy);

        var endPoint = new ExplicitProxyEndPoint(IPAddress.Loopback, 0, decryptSsl: true);
        proxy.AddEndPoint(endPoint);
        proxy.Start();
        return new TwpProxyHost(proxy, endPoint.Port, $"http://127.0.0.1:{endPoint.Port}/", isExplicitProxy: true);
    }

    /// <summary>
    /// Explicit intercepting proxy: client speaks cleartext HTTP to the proxy, origin is cleartext HTTP/1.
    /// Plain-client MITM twin of <see cref="StartReverseHttp1"/> (inspectable both legs; no forged cert).
    /// </summary>
    public static TwpProxyHost StartHttpMitm(int? maxCachedConnections = null)
    {
        var proxy = CreateBaseProxy(enableHttp2: false, enableHttp3: false, maxCachedConnections);
        var endPoint = new ExplicitProxyEndPoint(IPAddress.Loopback, 0, decryptSsl: false);
        proxy.AddEndPoint(endPoint);
        proxy.Start();
        return new TwpProxyHost(proxy, endPoint.Port, $"http://127.0.0.1:{endPoint.Port}/", isExplicitProxy: true);
    }

    /// <summary>
    /// Client H1 TLS → H1→H2 bridge → origin HTTPS with ALPN h2.
    /// </summary>
    public static TwpProxyHost StartReverseHttp11ToHttp2(int originHttpsPort)
    {
        var proxy = CreateBaseProxy(enableHttp2: true, enableHttp3: false);
        ConfigureSharedTestCa(proxy);

        var endPoint = new TransparentProxyEndPoint(IPAddress.Loopback, 0, decryptSsl: true)
        {
            ForwardHost = "127.0.0.1",
            ForwardPort = originHttpsPort,
            GenericCertificateName = "localhost",
            MaxCachedConnections = 256
        };
        endPoint.BeforeSslAuthenticate += (_, args) =>
        {
            args.UpstreamHttpProtocol = UpstreamHttpProtocol.Http2;
            args.AllowHttpProtocolTranslation = true;
            return Task.CompletedTask;
        };
        proxy.AddEndPoint(endPoint);
        proxy.Start();
        return new TwpProxyHost(proxy, endPoint.Port, $"https://127.0.0.1:{endPoint.Port}/", isExplicitProxy: false);
    }

    /// <summary>
    /// Client H2 TLS → H2→H3 cold bridge → origin QUIC/h3.
    /// </summary>
    public static TwpProxyHost StartReverseHttp2ToHttp3(int originQuicPort)
    {
        if (!QuicListener.IsSupported)
            throw new PlatformNotSupportedException("QuicListener is not supported on this platform.");

        var proxy = CreateBaseProxy(enableHttp2: true, enableHttp3: true);
        ConfigureSharedTestCa(proxy);

        var endPoint = new TransparentProxyEndPoint(IPAddress.Loopback, 0, decryptSsl: true)
        {
            ForwardHost = "localhost",
            ForwardPort = originQuicPort,
            GenericCertificateName = "localhost",
            MaxCachedConnections = 256
        };
        endPoint.BeforeSslAuthenticate += (_, args) =>
        {
            args.UpstreamHttpProtocol = UpstreamHttpProtocol.Http3;
            args.AllowHttpProtocolTranslation = true;
            return Task.CompletedTask;
        };
        proxy.AddEndPoint(endPoint);
        proxy.Start();
        return new TwpProxyHost(proxy, endPoint.Port, $"https://127.0.0.1:{endPoint.Port}/", isExplicitProxy: false);
    }

    /// <summary>
    /// Dual-listen reverse: client H3 → H3→H2 bridge → origin HTTPS with ALPN h2.
    /// </summary>
    public static TwpProxyHost StartReverseHttp3ToHttp2(int originHttpsPort)
    {
        if (!QuicListener.IsSupported)
            throw new PlatformNotSupportedException("QuicListener is not supported on this platform.");

        var proxy = CreateBaseProxy(enableHttp2: true, enableHttp3: true);
        ConfigureSharedTestCa(proxy);

        var endPoint = new TransparentProxyEndPoint(IPAddress.Loopback, 0, decryptSsl: true)
        {
            EnableHttp3 = true,
            ForwardHost = "127.0.0.1",
            ForwardPort = originHttpsPort,
            GenericCertificateName = "localhost",
            MaxInboundBidirectionalStreams = 256,
            MaxCachedConnections = 256
        };
        endPoint.BeforeQuicAuthenticate += (_, args) =>
        {
            args.UpstreamHttpProtocol = UpstreamHttpProtocol.Http2;
            return Task.CompletedTask;
        };
        endPoint.BeforeSslAuthenticate += (_, args) =>
        {
            args.UpstreamHttpProtocol = UpstreamHttpProtocol.Http2;
            return Task.CompletedTask;
        };
        proxy.AddEndPoint(endPoint);
        proxy.Start();
        WarmTlsTerminateCertificate(proxy, endPoint, "localhost");
        return new TwpProxyHost(proxy, endPoint.Port, $"https://localhost:{endPoint.Port}/", isExplicitProxy: false);
    }

    /// <summary>
    /// Client H1 TLS → H1→H3 bridge → origin QUIC/h3.
    /// </summary>
    public static TwpProxyHost StartReverseHttp1ToHttp3(int originQuicPort)
    {
        if (!QuicListener.IsSupported)
            throw new PlatformNotSupportedException("QuicListener is not supported on this platform.");

        var proxy = CreateBaseProxy(enableHttp2: true, enableHttp3: true);
        ConfigureSharedTestCa(proxy);

        var endPoint = new TransparentProxyEndPoint(IPAddress.Loopback, 0, decryptSsl: true)
        {
            ForwardHost = "localhost",
            ForwardPort = originQuicPort,
            GenericCertificateName = "localhost",
            MaxCachedConnections = 256
        };
        endPoint.BeforeSslAuthenticate += (_, args) =>
        {
            args.UpstreamHttpProtocol = UpstreamHttpProtocol.Http3;
            args.AllowHttpProtocolTranslation = true;
            return Task.CompletedTask;
        };
        proxy.AddEndPoint(endPoint);
        // Warm before Start so the fixed-cert H1 path sees CachedServerAuthOptions on first accept.
        WarmTlsTerminateCertificate(proxy, endPoint, "localhost");
        proxy.Start();
        return new TwpProxyHost(proxy, endPoint.Port, $"https://127.0.0.1:{endPoint.Port}/", isExplicitProxy: false);
    }

    private static ProxyServer CreateBaseProxy(bool enableHttp2, bool enableHttp3, int? maxCachedConnections = null)
    {
        var proxy = new ProxyServer(false, false, false);
        // Saturation runs must not format or enqueue diagnostics on session threads.
        proxy.Logging.Enabled = false;
        proxy.CertificateManager.SaveFakeCertificates = false;
        // Opt-in for compare-tls-cost handshake arms (child process via env).
        proxy.EnableRequestTimingCapture =
            string.Equals(Environment.GetEnvironmentVariable("TWP_RPS_CAPTURE_TLS"), "1",
                StringComparison.Ordinal);

        // Diagnostic-only stage decomposition (TWP_RPS_STAGE_TIMING=1): prints per-stage latency
        // percentiles to stderr every 20s. Subscribing AfterResponse disables no-interception fast
        // paths, so never enable this for publishable benchmark numbers.
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("TWP_RPS_STAGE_TIMING")))
        {
            proxy.EnableRequestTimingCapture = true;
            StageTimingCollector.Attach(proxy);
        }
        proxy.EnableConnectionPool = true;
        proxy.EnableHttp2 = enableHttp2;
        proxy.EnableHttp3 = enableHttp3;
        proxy.EnableHttpsSvcbDnsDiscovery = false;
        // Saturation probe: raise floor so 4-vCPU Linux hosts are not stuck at OS defaults.
        proxy.ThreadPoolWorkerThread = Math.Max(Environment.ProcessorCount * 8, 64);
        proxy.MaxCachedConnections = maxCachedConnections ?? 256;
        // New-connection TLS arms pay setsockopt keepalive on every accept; Kestrel does not.
        // Keep-alive reverse RPS is unaffected (connections already long-lived).
        proxy.EnableTcpKeepAlive = false;
        // New-connection TLS: skip ConcurrentDictionary session-CTS tracking (Stop still disposes).
        proxy.TrackSessionCancellations = false;

        return proxy;
    }

    private static void ConfigureSharedTestCa(ProxyServer proxy)
    {
        proxy.CertificateManager.RootCertificateName = LoopbackCertificateAuthority.RootCertificateName;
        proxy.CertificateManager.RootCertificate = LoopbackCertificateAuthority.RootCertificate;
        proxy.CertificateManager.LeafCertificateKeyAlgorithm = CertificateKeyAlgorithm.EcdsaP256;
        proxy.ServerCertificateValidationCallback += (_, args) =>
        {
            args.IsValid = LoopbackCertificateAuthority.Validate(args.Certificate);
            return Task.CompletedTask;
        };
    }

    /// <summary>
    /// Pin a leaf + warm <see cref="System.Net.Security.SslStreamCertificateContext"/> so the first
    /// handshake does not pay cert creation / chain build on the critical path.
    /// Prefer the shared loopback RSA leaf (same material YARP/Kestrel UseHttps uses) so
    /// compare-tls-cost new-connection is not an ECDSA-vs-RSA Schannel bake-off.
    /// </summary>
    private static void WarmTlsTerminateCertificate(ProxyServer proxy, TransparentProxyEndPoint endPoint,
        string certName)
    {
        var leaf = LoopbackCertificateAuthority.ServerCertificate;
        endPoint.GenericCertificate = leaf;
        var context = proxy.CertificateManager.CreateSslCertificateContext(leaf);
        endPoint.CachedServerAuthOptions = new System.Net.Security.SslServerAuthenticationOptions
        {
            ServerCertificateContext = context,
            ClientCertificateRequired = false,
            EnabledSslProtocols = proxy.SupportedSslProtocols,
            CertificateRevocationCheckMode = System.Security.Cryptography.X509Certificates.X509RevocationMode.NoCheck,
            ApplicationProtocols = [System.Net.Security.SslApplicationProtocol.Http11],
            // Match Kestrel HttpsConnectionMiddleware.ConfigureAlpn (H2 always; harmless for H1).
            AllowRenegotiation = false
        };
        _ = certName;
    }

    public void Dispose()
    {
        proxyServer.Stop();
        proxyServer.Dispose();
    }
}
