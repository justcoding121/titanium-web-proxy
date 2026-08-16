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
            ForwardPort = originHttpPort
        };
        proxy.AddEndPoint(endPoint);
        proxy.Start();
        return new TwpProxyHost(proxy, endPoint.Port, $"http://127.0.0.1:{endPoint.Port}/", isExplicitProxy: false);
    }

    public static TwpProxyHost StartReverseHttp2(int originHttpsPort)
    {
        var proxy = CreateBaseProxy(enableHttp2: true, enableHttp3: false);
        ConfigureSharedTestCa(proxy);

        var endPoint = new TransparentProxyEndPoint(IPAddress.Loopback, 0, decryptSsl: true)
        {
            ForwardHost = "127.0.0.1",
            ForwardPort = originHttpsPort,
            GenericCertificateName = "localhost"
        };
        proxy.AddEndPoint(endPoint);
        proxy.Start();
        // #region agent log
        DebugSessionLog.Write("B", "TwpProxyHost.StartReverseHttp2", "started",
            new { listenPort = endPoint.Port, originHttpsPort, maxCached = proxy.MaxCachedConnections });
        // #endregion
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
            MaxInboundBidirectionalStreams = 256
        };
        endPoint.BeforeQuicAuthenticate += (_, args) =>
        {
            args.UpstreamHttpProtocol = UpstreamHttpProtocol.Http3;
            return Task.CompletedTask;
        };
        proxy.AddEndPoint(endPoint);
        proxy.Start();
        // #region agent log
        DebugSessionLog.Write("C", "TwpProxyHost.StartReverseHttp3", "started",
            new { listenPort = endPoint.Port, originHttpsPort, maxCached = proxy.MaxCachedConnections });
        // #endregion
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

    private static ProxyServer CreateBaseProxy(bool enableHttp2, bool enableHttp3, int? maxCachedConnections = null)
    {
        var proxy = new ProxyServer(false, false, false);
        proxy.CertificateManager.SaveFakeCertificates = false;
        proxy.EnableRequestTimingCapture = false;
        proxy.EnableConnectionPool = true;
        proxy.EnableHttp2 = enableHttp2;
        proxy.EnableHttp3 = enableHttp3;
        proxy.EnableHttpsSvcbDnsDiscovery = false;
        if (maxCachedConnections is { } n)
            proxy.MaxCachedConnections = n;
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

    public void Dispose()
    {
        proxyServer.Stop();
        proxyServer.Dispose();
    }
}
