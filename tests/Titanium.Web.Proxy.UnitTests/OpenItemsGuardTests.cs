#pragma warning disable TWP001

using System.IO;
using System.Net;
using System.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Abstractions.Clusters;
using Titanium.Web.Proxy.Abstractions.Routing;
using Titanium.Web.Proxy.EventArguments;
using Titanium.Web.Proxy.Helpers;
using Titanium.Web.Proxy.Http;
using Titanium.Web.Proxy.Models;
using Titanium.Web.Proxy.Network.Tcp;
using Titanium.Web.Proxy.Routing;

namespace Titanium.Web.Proxy.UnitTests;

/// <summary>
/// Behavior that must stay identical when the follow-up options are left unset.
/// </summary>
[TestClass]
public class OpenItemsGuardTests
{
    [TestMethod]
    public void PlainRoute_DoesNotRequirePerRequestDispatch()
    {
        Assert.IsFalse(ReverseProxyFastPath.RequiresPerRequestDispatch(null, new ExplicitProxyEndPoint(IPAddress.Loopback, 0, false)));
    }

    [TestMethod]
    public void DefaultAltSvcValue_MatchesTheHistoricalHeader()
    {
        var ep = new TransparentProxyEndPoint(IPAddress.Loopback, 8443, true) { EnableHttp3 = true };
        Assert.AreEqual("h3=\":8443\"; ma=86400", ep.ClientAltSvcValue);
        Assert.AreEqual("h3=\":8443\"; ma=86400", ep.ClientAltSvcValue);
    }

    [TestMethod]
    public void ResetForKeepAlive_ClearsRoutingResidue()
    {
        using var proxy = new ProxyServer();
        var endPoint = new ExplicitProxyEndPoint(IPAddress.Loopback, 0, false);
        var connection = new QuicClientConnection(
            proxy, new IPEndPoint(IPAddress.Loopback, 4433), new IPEndPoint(IPAddress.Loopback, 12345));
        using var cts = new CancellationTokenSource();
        var stream = new HttpClientStream(proxy, connection, Stream.Null, proxy.BufferPool, cts.Token);
        var session = new SessionEventArgs(proxy, endPoint, stream, null, cts);
        session.Extras = new RoutingFeatureState { StatusRetried = true };
        session.UpstreamPinnedAddress = IPAddress.Loopback;
        session.UpstreamHttpClient = new ClusterHttpClientConfig { MaxConnectionsPerServer = 2 };
        session.UpstreamConnectHost = "origin.example";
        session.UpstreamConnectPort = 8080;
        session.UpstreamDestinationId = "d1";
        session.MatchedRoute = new RouteConfig
        {
            Id = "r",
            ClusterId = "c",
            Match = new RouteMatch { Path = "/", PathKind = PathMatchKind.Prefix },
        };
        session.ResponseTrailerPlan = new System.Collections.Generic.Dictionary<string, string> { ["x"] = "y" };
        session.ResponseHeaderTransformPlan = "plan";

        session.ResetForKeepAlive(null, null);

        Assert.IsNull(session.Extras);
        Assert.IsNull(session.UpstreamPinnedAddress);
        Assert.IsNull(session.UpstreamHttpClient);
        Assert.IsNull(session.UpstreamConnectHost);
        Assert.IsNull(session.UpstreamConnectPort);
        Assert.IsNull(session.UpstreamDestinationId);
        Assert.IsNull(session.MatchedRoute);
        Assert.IsNull(session.ResponseTrailerPlan);
        Assert.IsNull(session.ResponseHeaderTransformPlan);
    }
}
