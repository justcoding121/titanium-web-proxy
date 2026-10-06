using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Abstractions;
using Titanium.Web.Proxy.Abstractions.Clusters;
using Titanium.Web.Proxy.Abstractions.Routing;
using Titanium.Web.Proxy.Clusters;
using Titanium.Web.Proxy.EventArguments;
using Titanium.Web.Proxy.Helpers;
using Titanium.Web.Proxy.Models;
using Titanium.Web.Proxy.Network.Tcp;
using Titanium.Web.Proxy.Routing;

namespace Titanium.Web.Proxy.UnitTests;

[TestClass]
public class RouteLimitsDispatchTests
{
    [TestMethod]
    public async Task TryApply_SetsSessionOverrides_OnlyWhenTheRouteDeclaresLimits()
    {
        using var proxy = new ProxyServer(userTrustRootCertificate: false);
        var clusters = new List<ClusterConfig>
        {
            new()
            {
                Id = "c1",
                Destinations = [new DestinationConfig { Id = "d1", Address = "10.0.0.5", Port = 8080 }],
            },
        };
        var manager = new ClusterManager();
        await manager.ApplyAsync(clusters);
        proxy.ReverseProxy = new ReverseProxyOptions
        {
            ClusterManager = manager,
            Routes =
            [
                new RouteConfig
                {
                    Id = "limited",
                    ClusterId = "c1",
                    Match = new RouteMatch { Host = "limited.test", Path = "/", PathKind = PathMatchKind.Prefix },
                    Limits = new RouteLimits
                    {
                        RequestTimeoutSeconds = 15,
                        IdleTimeoutSeconds = 7,
                        MaxBufferedBodyBytes = 2048,
                        MaxWebSocketFramePayloadBytes = 4096,
                    },
                },
                new RouteConfig
                {
                    Id = "plain",
                    ClusterId = "c1",
                    Order = 1,
                    Match = new RouteMatch { Host = "plain.test", Path = "/", PathKind = PathMatchKind.Prefix },
                },
            ],
        };

        var limited = MakeSession(proxy, "http://limited.test/a");
        Assert.IsTrue(ReverseProxySessionDispatch.TryApply(proxy, limited));
        Assert.AreEqual(TimeSpan.FromSeconds(15), limited.RequestTimeout);
        Assert.AreEqual(TimeSpan.FromSeconds(7), limited.IdleReadTimeout);
        Assert.AreEqual(2048, limited.MaxBufferedBodyBytes);
        Assert.AreEqual(4096, limited.MaxWebSocketFramePayloadBytes);

        var plain = MakeSession(proxy, "http://plain.test/a");
        Assert.IsTrue(ReverseProxySessionDispatch.TryApply(proxy, plain));
        Assert.IsNull(plain.RequestTimeout);
        Assert.IsNull(plain.IdleReadTimeout);
        Assert.IsNull(plain.MaxBufferedBodyBytes);
        Assert.IsNull(plain.MaxWebSocketFramePayloadBytes);
    }

    private static SessionEventArgs MakeSession(ProxyServer proxy, string url)
    {
        var endPoint = new ExplicitProxyEndPoint(IPAddress.Loopback, 0, false);
        var connection = new QuicClientConnection(
            proxy, new IPEndPoint(IPAddress.Loopback, 4433), new IPEndPoint(IPAddress.Loopback, 12345));
        var cts = new CancellationTokenSource();
        var clientStream = new HttpClientStream(proxy, connection, Stream.Null, proxy.BufferPool, cts.Token);
        var session = new SessionEventArgs(proxy, endPoint, clientStream, null, cts);
        session.HttpClient.Request.Method = "GET";
        session.HttpClient.Request.RequestUriString = url;
        return session;
    }
}
