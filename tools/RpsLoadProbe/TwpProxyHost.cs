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
    /// TLS-terminating reverse HTTP/2 matching nginx: client h2 TLS → H2→H1 bridge → cleartext HTTP/1 origin.
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
        // matches nginx's topology but the per-stream H2→H1 bridge still errors under saturation;
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

        var endPoint = new TransparentQuicProxyEndPoint(IPAddress.Loopback, 0)
        {
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
        return new TwpProxyHost(proxy, endPoint.Port, $"https://localhost:{endPoint.Port}/", isExplicitProxy: false);
    }

    /// <summary>
    /// QUIC/h3 TLS terminate → cleartext HTTP/1 origin (ForwardCleartext + UpstreamHttpProtocol.Http11).
    /// </summary>
    public static TwpProxyHost StartReverseHttp3Cleartext(int originHttpPort)
    {
        if (!QuicListener.IsSupported)
            throw new PlatformNotSupportedException("QuicListener is not supported on this platform.");

        var proxy = CreateBaseProxy(enableHttp2: true, enableHttp3: true);
        ConfigureSharedTestCa(proxy);

        var endPoint = new TransparentQuicProxyEndPoint(IPAddress.Loopback, 0)
        {
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
        proxy.AddEndPoint(endPoint);
        proxy.Start();
        return new TwpProxyHost(proxy, endPoint.Port, $"https://localhost:{endPoint.Port}/", isExplicitProxy: false);
    }

    /// <summary>
    /// Client H2 TLS → H2→H1 bridge → origin HTTPS HTTP/1 (MITM: decrypt client, TLS to origin).
    /// </summary>
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
    /// Client H3 QUIC → bridge → origin HTTPS HTTP/1 (MITM: decrypt client, TLS to origin).
    /// </summary>
    public static TwpProxyHost StartMitmHttp3ToHttp1(int originHttpsPort)
    {
        if (!QuicListener.IsSupported)
            throw new PlatformNotSupportedException("QuicListener is not supported on this platform.");

        var proxy = CreateBaseProxy(enableHttp2: true, enableHttp3: true);
        ConfigureSharedTestCa(proxy);

        var endPoint = new TransparentQuicProxyEndPoint(IPAddress.Loopback, 0)
        {
            ForwardHost = "127.0.0.1",
            ForwardPort = originHttpsPort,
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
        proxy.AddEndPoint(endPoint);
        proxy.Start();
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
    /// Client H3 QUIC → H3→H2 bridge → origin HTTPS with ALPN h2.
    /// </summary>
    public static TwpProxyHost StartReverseHttp3ToHttp2(int originHttpsPort)
    {
        if (!QuicListener.IsSupported)
            throw new PlatformNotSupportedException("QuicListener is not supported on this platform.");

        var proxy = CreateBaseProxy(enableHttp2: true, enableHttp3: true);
        ConfigureSharedTestCa(proxy);

        var endPoint = new TransparentQuicProxyEndPoint(IPAddress.Loopback, 0)
        {
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
        proxy.AddEndPoint(endPoint);
        proxy.Start();
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
        proxy.EnableConnectionPool = true;
        proxy.EnableHttp2 = enableHttp2;
        proxy.EnableHttp3 = enableHttp3;
        proxy.EnableHttpsSvcbDnsDiscovery = false;
        // Saturation probe: raise floor so 4-vCPU Linux hosts are not stuck at OS defaults.
        proxy.ThreadPoolWorkerThread = Math.Max(Environment.ProcessorCount * 4, 32);
        proxy.MaxCachedConnections = maxCachedConnections ?? 256;

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
    /// </summary>
    private static void WarmTlsTerminateCertificate(ProxyServer proxy, TransparentProxyEndPoint endPoint,
        string certName)
    {
        var leaf = proxy.CertificateManager.CreateServerCertificate(certName).GetAwaiter().GetResult();
        if (leaf == null)
            return;
        endPoint.GenericCertificate = leaf;
        _ = proxy.CertificateManager.CreateSslCertificateContext(leaf);
    }

    public void Dispose()
    {
        proxyServer.Stop();
        proxyServer.Dispose();
    }
}
