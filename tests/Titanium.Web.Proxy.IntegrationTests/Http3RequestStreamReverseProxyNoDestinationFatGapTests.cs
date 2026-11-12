#pragma warning disable CA1416
#pragma warning disable TWP001

using System;
using System.Net;
using System.Net.Quic;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Abstractions;
using Titanium.Web.Proxy.Abstractions.Clusters;
using Titanium.Web.Proxy.Abstractions.Routing;
using Titanium.Web.Proxy.Clusters;
using Titanium.Web.Proxy.IntegrationTests.Helpers;
using Titanium.Web.Proxy.IntegrationTests.Setup;
using Titanium.Web.Proxy.Models;
using Titanium.Web.Proxy.Routing;
using Titanium.Web.Proxy.Transforms;

namespace Titanium.Web.Proxy.IntegrationTests;

/// <summary>
///     Fat <c>Http3RequestStream</c> + <c>ReverseProxySessionDispatch</c> suite: NoDestination
///     must answer the QUIC client instead of hanging.
/// </summary>
[DoNotParallelize]
[TestClass]
public class Http3RequestStreamReverseProxyNoDestinationFatGapTests
{
    private static void RequireQuic()
    {
        if (!QuicListener.IsSupported || !QuicConnection.IsSupported)
            Assert.Inconclusive("MsQuic / System.Net.Quic is not supported on this platform.");
    }

    /// <summary>
    ///     Bug class: maintenance destination leaves the H3 stream open with no response.
    /// </summary>
    [TestMethod]
    [Timeout(60_000)]
    public async Task H3_ReverseProxy_NoDestination_Returns503()
    {
        RequireQuic();

        var manager = new ClusterManager();
        await manager.ApplyAsync(
        [
            new ClusterConfig
            {
                Id = "c",
                Destinations =
                [
                    new DestinationConfig { Id = "d", Address = "127.0.0.1", Port = 9, UseHttps = false },
                ],
            },
        ]);
        manager.SetDestinationState("d", DestinationState.Maintenance);

        var routes = new[]
        {
            new RouteConfig
            {
                Id = "r",
                ClusterId = "c",
                Match = new RouteMatch { Path = "/", PathKind = PathMatchKind.Prefix },
            },
        };

        var ep = new TransparentQuicProxyEndPoint(IPAddress.Loopback, 0)
        {
            ForwardHost = "127.0.0.1",
            ForwardPort = 9,
        };
        using var proxy = CreateH3DispatchProxy(ep, manager, routes);
        try
        {
            await using var client = await QuicHttp3Client.ConnectAsync(
                new IPEndPoint(IPAddress.Loopback, ep.Port), "localhost");
            var response = await client.SendAsync("GET", "localhost", "/any");
            Assert.AreEqual(503, response.StatusCode, response.TextBody);
        }
        finally
        {
            proxy.Stop();
            proxy.Dispose();
        }
    }

    private static ProxyServer CreateH3DispatchProxy(
        TransparentQuicProxyEndPoint ep,
        ClusterManager manager,
        RouteConfig[] routes)
    {
        var proxy = new ProxyServer(false, false, false)
        {
            EnableHttp3 = true,
            EnableHttpInterception = true,
            EnableHttpsSvcbDnsDiscovery = false,
        };
        proxy.CertificateManager.RootCertificateName = TestCertificateAuthority.RootCertificateName;
        proxy.CertificateManager.RootCertificate = TestCertificateAuthority.RootCertificate;
        proxy.CertificateManager.SaveFakeCertificates = false;
        proxy.ServerCertificateValidationCallback += (_, args) =>
        {
            args.IsValid = TestCertificateAuthority.Validate(args.Certificate, args.SslPolicyErrors);
            return Task.CompletedTask;
        };
        proxy.ReverseProxy = new ReverseProxyOptions
        {
            Routes = routes,
            ClusterManager = manager,
            RouteMatcher = new RouteMatcher(),
            LoadBalancer = new LoadBalancer(),
            TransformEngine = new TransformEngine(),
        };
        ep.BeforeQuicAuthenticate += async (_, args) =>
        {
            args.UpstreamHttpProtocol = UpstreamHttpProtocol.Http11;
            await Task.CompletedTask;
        };
        proxy.AddEndPoint(ep);
        proxy.Start();
        return proxy;
    }
}
