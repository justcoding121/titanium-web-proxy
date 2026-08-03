using System;
using System.Net;
using System.Threading.Tasks;
using Titanium.Web.Proxy.Models;
using Titanium.Web.Proxy.Network;

namespace Titanium.Web.Proxy.IntegrationTests.Setup;

public class TestProxyServer : IDisposable
{
    public TestProxyServer(bool isReverseProxy, ProxyServer? upStreamProxy = null)
    {
        ProxyServer = new ProxyServer(false, false, false);
        // Keep the manager's configured name aligned with the shared test root so any code path
        // that falls back to CreateRootCertificate cannot mint a product-default-CN root that
        // would collide with an example-trusted "Titanium Root Certificate Authority" in the
        // current-user Windows stores (Basic example uses new ProxyServer() which trusts on Start).
        ProxyServer.CertificateManager.RootCertificateName = TestCertificateAuthority.RootCertificateName;
        ProxyServer.CertificateManager.RootCertificate = TestCertificateAuthority.RootCertificate;
        ProxyServer.CertificateManager.SaveFakeCertificates = false;
        ProxyServer.ServerCertificateValidationCallback += (_, args) =>
        {
            args.IsValid = TestCertificateAuthority.Validate(args.Certificate, args.SslPolicyErrors);
            return Task.CompletedTask;
        };

        var explicitEndPoint = isReverseProxy
            ? (ProxyEndPoint)new TransparentProxyEndPoint(IPAddress.Any, 0)
            : new ExplicitProxyEndPoint(IPAddress.Any, 0);

        ProxyServer.AddEndPoint(explicitEndPoint);

        if (upStreamProxy != null)
        {
            ProxyServer.UpStreamHttpProxy = new ExternalProxy("localhost", upStreamProxy.ProxyEndPoints[0].Port);
            ProxyServer.UpStreamHttpsProxy = new ExternalProxy("localhost", upStreamProxy.ProxyEndPoints[0].Port);
        }

        ProxyServer.Start();
    }

    public ProxyServer ProxyServer { get; }

    public int ListeningPort => ProxyServer.ProxyEndPoints[0].Port;

    public CertificateManager CertificateManager => ProxyServer.CertificateManager;

    public void Dispose()
    {
        ProxyServer.Stop();
        ProxyServer.Dispose();
    }
}
