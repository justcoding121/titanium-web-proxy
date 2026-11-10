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
///     Fat <c>Http2ToHttp2DispatchBridgeHandler</c> suite: Accept-Patch and Issue reach the origin.
/// </summary>
[DoNotParallelize]
[TestClass]
public class Http2DispatchAcceptPatchAndIssueFatGapTests
{
    private static async Task<(ProxyServer Proxy, TransparentProxyEndPoint Tls)> StartDispatchProxyAsync(int originPort)
    {
        ClusterConfig[] clusters =
        [
            new()
            {
                Id = "c-ap",
                Destinations = [new DestinationConfig { Id = "d0", Address = "127.0.0.1", Port = originPort }],
            },
        ];

        var manager = new ClusterManager();
        await manager.ApplyAsync(clusters);

        RouteConfig[] routes =
        [
            new()
            {
                Id = "r-root",
                ClusterId = "c-ap",
                Match = new RouteMatch { Path = "/", PathKind = PathMatchKind.Prefix },
            },
        ];

        var proxy = new ProxyServer(false, false, false) { EnableHttp2 = true, EnableHttpInterception = true };
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
            Clusters = clusters,
            ClusterManager = manager,
            RouteMatcher = new RouteMatcher(),
            LoadBalancer = new LoadBalancer(),
            TransformEngine = new TransformEngine(),
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

    [TestMethod]
    [Timeout(30_000)]
    public async Task Dispatch_AcceptPatch_ReachesOrigin()
    {
        var captured = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var origin = new Http2RawOriginServer(TestCertificateAuthority.ServerCertificate);
        origin.HandleConnection(async conn =>
        {
            await conn.SendInitialSettingsAsync();
            while (true)
            {
                var (streamId, headers, _) = await conn.ReadRequestAsync();
                captured.TrySetResult(headers.FirstOrDefault(h => h.Name == "accept-patch").Value);
                var resp = conn.EncodeHeaders([(":status", "204")], []);
                await conn.WriteHeaderBlockAsync(streamId, resp, endStream: true);
            }
        });

        var (proxy, ep) = await StartDispatchProxyAsync(origin.Port);
        try
        {
            using var raw = await Http2RawClient.ConnectDirectAsync(ep.Port, "localhost");
            var req = raw.Connection.EncodeHeaders(
                [(":method", "OPTIONS"), (":scheme", "https"), (":path", "/"), (":authority", "localhost")],
                [("accept-patch", "text/example")]);
            await raw.Connection.WriteHeaderBlockAsync(1, req, endStream: true);
            var seen = await captured.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.AreEqual("text/example", seen);
        }
        finally
        {
            proxy.Stop();
            proxy.Dispose();
        }
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task Dispatch_Issue_ReachesOrigin()
    {
        var captured = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var origin = new Http2RawOriginServer(TestCertificateAuthority.ServerCertificate);
        origin.HandleConnection(async conn =>
        {
            await conn.SendInitialSettingsAsync();
            while (true)
            {
                var (streamId, headers, _) = await conn.ReadRequestAsync();
                captured.TrySetResult(headers.FirstOrDefault(h => h.Name == "issue").Value);
                var resp = conn.EncodeHeaders([(":status", "204")], []);
                await conn.WriteHeaderBlockAsync(streamId, resp, endStream: true);
            }
        });

        var (proxy, ep) = await StartDispatchProxyAsync(origin.Port);
        try
        {
            using var raw = await Http2RawClient.ConnectDirectAsync(ep.Port, "localhost");
            var req = raw.Connection.EncodeHeaders(
                [(":method", "GET"), (":scheme", "https"), (":path", "/"), (":authority", "localhost")],
                [("issue", "https://gap.test/terms")]);
            await raw.Connection.WriteHeaderBlockAsync(1, req, endStream: true);
            var seen = await captured.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.IsTrue(seen?.Contains("gap.test", StringComparison.Ordinal), seen);
        }
        finally
        {
            proxy.Stop();
            proxy.Dispose();
        }
    }
}
