#pragma warning disable TWP001
using System.IO;
using System.Net;
using System.Reflection;
using System.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Abstractions.Clusters;
using Titanium.Web.Proxy.Abstractions.Routing;
using Titanium.Web.Proxy.EventArguments;
using Titanium.Web.Proxy.Helpers;
using Titanium.Web.Proxy.Http3;
using Titanium.Web.Proxy.Models;
using Titanium.Web.Proxy.Network.Tcp;

namespace Titanium.Web.Proxy.UnitTests;

/// <summary>
///     Fat <c>Http3RequestStream</c> MITM unchanged-lite gates: armed gateway retry must
///     block the fast path even when <see cref="SessionEventArgs.Extras" /> is not populated yet.
/// </summary>
[TestClass]
public class Http3RequestStreamGatewayRetryMitmGateFatGapTests
{
    private const BindingFlags PrivateStatic = BindingFlags.Static | BindingFlags.NonPublic;

    private static SessionEventArgs MakeSession(ProxyServer proxy)
    {
        var endPoint = new ExplicitProxyEndPoint(IPAddress.Loopback, 0, false);
        var connection = new QuicClientConnection(
            proxy, new IPEndPoint(IPAddress.Loopback, 4433), new IPEndPoint(IPAddress.Loopback, 12345));
        var cts = new CancellationTokenSource();
        var clientStream = new HttpClientStream(proxy, connection, Stream.Null, proxy.BufferPool, cts.Token);
        return new SessionEventArgs(proxy, endPoint, clientStream, null, cts);
    }

    [TestMethod]
    public void TryMitmUnchanged_BlocksWhenMatchedRouteHasRetry()
    {
        using var proxy = new ProxyServer(userTrustRootCertificate: false);
        using var session = MakeSession(proxy);
        using var cts = new CancellationTokenSource();
        var auth = new BeforeQuicAuthenticateEventArgs(
            proxy, cts, "sni.test", "origin.test", 443,
            new IPEndPoint(IPAddress.Loopback, 1), new IPEndPoint(IPAddress.Loopback, 2));
        auth.UpstreamHttpProtocol = UpstreamHttpProtocol.Http11;
        var request = session.HttpClient.Request;
        request.Method = "GET";
        request.RequestUriString = "/";
        var baseline = MitmCompressedRelayHelper.HeaderRelayBaseline.Capture(request.Headers);
        session.MatchedRoute = new RouteConfig
        {
            Id = "r",
            ClusterId = "c1",
            Match = new RouteMatch(),
            Retry = new RetryConfig { Attempts = 2 },
        };

        var h1 = typeof(Http3RequestStream).GetMethod("TryMitmUnchangedH3ToH1Lite", PrivateStatic)!;
        var result = (bool)h1.Invoke(
            null,
            [session, auth, request, baseline, "GET", request.RequestUriString8, request.Authority, "GET"])!;
        Assert.IsFalse(result, "Armed route retry must disable MITM unchanged H3→H1 lite.");
    }
}
#pragma warning restore TWP001
