using System.Net;
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

    private TwpProxyHost(ProxyServer proxyServer, int port, string listenUrl, bool isExplicitProxy)
    {
        this.proxyServer = proxyServer;
        Port = port;
        ListenUrl = listenUrl;
        IsExplicitProxy = isExplicitProxy;
    }

    public static TwpProxyHost StartReverseHttp1(int originHttpPort)
    {
        var proxy = CreateBaseProxy();
        // ForwardHost alone redirects the TCP connection; Host header stays as the client sent it.
        // Avoiding BeforeRequest URL rewrite removes per-request async event + string/Uri alloc.
        var endPoint = new TransparentProxyEndPoint(IPAddress.Loopback, 0, decryptSsl: false)
        {
            ForwardHost = "127.0.0.1",
            ForwardPort = originHttpPort
        };
        proxy.AddEndPoint(endPoint);
        proxy.Start();
        return new TwpProxyHost(proxy, endPoint.Port, $"http://127.0.0.1:{endPoint.Port}/", isExplicitProxy: false);
    }

    public static TwpProxyHost StartHttpsMitm()
    {
        var proxy = CreateBaseProxy();
        proxy.CertificateManager.RootCertificateName = LoopbackCertificateAuthority.RootCertificateName;
        proxy.CertificateManager.RootCertificate = LoopbackCertificateAuthority.RootCertificate;
        proxy.CertificateManager.LeafCertificateKeyAlgorithm = CertificateKeyAlgorithm.EcdsaP256;
        proxy.ServerCertificateValidationCallback += (_, args) =>
        {
            args.IsValid = LoopbackCertificateAuthority.Validate(args.Certificate);
            return Task.CompletedTask;
        };

        var endPoint = new ExplicitProxyEndPoint(IPAddress.Loopback, 0, decryptSsl: true);
        proxy.AddEndPoint(endPoint);
        proxy.Start();
        return new TwpProxyHost(proxy, endPoint.Port, $"http://127.0.0.1:{endPoint.Port}/", isExplicitProxy: true);
    }

    private static ProxyServer CreateBaseProxy()
    {
        var proxy = new ProxyServer(false, false, false);
        proxy.CertificateManager.SaveFakeCertificates = false;
        proxy.EnableRequestTimingCapture = false;
        proxy.EnableConnectionPool = true;
        proxy.EnableHttp2 = false; // keep HTTP/1 saturation path isolated
        // Library defaults already include MaxCachedConnections=128 and ThreadPoolWorkerThread=2x cores.
        return proxy;
    }

    public void Dispose()
    {
        proxyServer.Stop();
        proxyServer.Dispose();
    }
}
