using System;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Abstractions;
using Titanium.Web.Proxy.Abstractions.Clusters;
using Titanium.Web.Proxy.Abstractions.Routing;
using Titanium.Web.Proxy.Clusters;
using Titanium.Web.Proxy.Http2;
using Titanium.Web.Proxy.IntegrationTests.Helpers;
using Titanium.Web.Proxy.IntegrationTests.Setup;
using Titanium.Web.Proxy.Models;
using Titanium.Web.Proxy.Routing;
using Titanium.Web.Proxy.Transforms;

namespace Titanium.Web.Proxy.IntegrationTests;

/// <summary>
///     Fat <c>Http2ToHttp2DispatchBridgeHandler</c> suite: multiple Cookie fields coalesce
///     to a single lowercase <c>cookie</c> field on the origin leg.
/// </summary>
[DoNotParallelize]
[TestClass]
public class Http2DispatchCookieCoalesceFatGapTests
{
    private static async Task<(ProxyServer Proxy, TransparentProxyEndPoint Tls)> StartDispatchProxyAsync(int originPort)
    {
        var manager = new ClusterManager();
        await manager.ApplyAsync(
        [
            new ClusterConfig
            {
                Id = "ca",
                Destinations = [new DestinationConfig { Id = "ca-d0", Address = "127.0.0.1", Port = originPort }],
            },
            new ClusterConfig
            {
                Id = "cb",
                Destinations = [new DestinationConfig { Id = "cb-d0", Address = "127.0.0.1", Port = originPort }],
            },
        ]);

        RouteConfig[] routes =
        [
            new()
            {
                Id = "ra",
                ClusterId = "ca",
                Match = new RouteMatch { Path = "/a", PathKind = PathMatchKind.Prefix },
            },
            new()
            {
                Id = "rb",
                ClusterId = "cb",
                Match = new RouteMatch { Path = "/", PathKind = PathMatchKind.Prefix },
            },
        ];

        var proxy = new ProxyServer(false, false, false) { EnableHttp2 = true };
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
            Clusters =
            [
                new ClusterConfig { Id = "ca", Destinations = [new DestinationConfig { Id = "ca-d0", Address = "127.0.0.1", Port = originPort }] },
                new ClusterConfig { Id = "cb", Destinations = [new DestinationConfig { Id = "cb-d0", Address = "127.0.0.1", Port = originPort }] },
            ],
        };

        var tls = new TransparentProxyEndPoint(IPAddress.Loopback, 0, decryptSsl: true)
        {
            GenericCertificate = TestCertificateAuthority.ServerCertificate,
            ForwardHost = "127.0.0.1",
            ForwardPort = originPort,
            ForwardCleartext = false,
        };
        proxy.AddEndPoint(tls);
        proxy.Start();
        return (proxy, tls);
    }

    /// <summary>
    ///     Bug class: duplicate Cookie fields stay uppercase or only the first value reaches the origin.
    /// </summary>
    [TestMethod]
    [Timeout(30_000)]
    public async Task DispatchBridge_MultipleCookieHeaders_CoalescedOnOrigin()
    {
        var captured = new TaskCompletionSource<(string Name, string Value)>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var origin = new Http2RawOriginServer(TestCertificateAuthority.ServerCertificate);
        origin.HandleConnection(async conn =>
        {
            await conn.SendInitialSettingsAsync();
            while (true)
            {
                var (streamId, headers, _) = await conn.ReadRequestAsync();
                var cookie = headers.FirstOrDefault(h =>
                    string.Equals(h.Name, "cookie", StringComparison.OrdinalIgnoreCase));
                captured.TrySetResult((cookie.Name ?? string.Empty, cookie.Value ?? string.Empty));
                var payload = System.Text.Encoding.UTF8.GetBytes("ok");
                var resp = conn.EncodeHeaders(
                    [(":status", "200"), ("content-length", payload.Length.ToString())], []);
                await conn.WriteHeaderBlockAsync(streamId, resp, endStream: false);
                await conn.WriteFrameAsync(Http2FrameType.Data, streamId, Http2FrameFlag.EndStream, payload);
            }
        });

        var (proxy, ep) = await StartDispatchProxyAsync(origin.Port);
        try
        {
            using var raw = await Http2RawClient.ConnectDirectAsync(ep.Port, "localhost");
            var req = raw.Connection.EncodeHeaders(
                [(":method", "GET"), (":scheme", "https"), (":path", "/"), (":authority", "localhost")],
                [("cookie", "a=1"), ("cookie", "b=2")]);
            await raw.Connection.WriteHeaderBlockAsync(1, req, endStream: true);

            var seen = await captured.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.AreEqual("cookie", seen.Name, "Origin wire must use lowercase cookie.");
            Assert.IsTrue(seen.Value.Contains("a=1"), seen.Value);
            Assert.IsTrue(seen.Value.Contains("b=2"), seen.Value);
        }
        finally
        {
            proxy.Stop();
            proxy.Dispose();
        }
    }
}
