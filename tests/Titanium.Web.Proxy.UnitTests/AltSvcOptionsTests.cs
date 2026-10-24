#pragma warning disable TWP001

using System;
using System.IO;
using System.Net;
using System.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.EventArguments;
using Titanium.Web.Proxy.Helpers;
using Titanium.Web.Proxy.Http;
using Titanium.Web.Proxy.Models;
using Titanium.Web.Proxy.Network.Tcp;

namespace Titanium.Web.Proxy.UnitTests;

[TestClass]
public class AltSvcOptionsTests
{
    [TestMethod]
    public void Default_IsByteIdentical_AndExplicitEndpointsStaySilent()
    {
        using var proxy = new ProxyServer();
        var transparent = new TransparentProxyEndPoint(IPAddress.Loopback, 8443, true) { EnableHttp3 = true };
        var session = Session(proxy, transparent);
        ProxyServer.MaybeInjectClientAltSvc(session);
        Assert.AreEqual("h3=\":8443\"; ma=86400", session.HttpClient.Response.Headers.GetFirstHeader("Alt-Svc")?.Value);

        var explicitEp = new ExplicitProxyEndPoint(IPAddress.Loopback, 8443, true);
        var explicitSession = Session(proxy, explicitEp);
        ProxyServer.MaybeInjectClientAltSvc(explicitSession);
        Assert.IsFalse(explicitSession.HttpClient.Response.Headers.HeaderExists("Alt-Svc"));
    }

    [TestMethod]
    public void OriginSuppliedAltSvc_IsNotOverwritten()
    {
        using var proxy = new ProxyServer();
        var ep = new TransparentProxyEndPoint(IPAddress.Loopback, 443, true) { EnableHttp3 = true };
        var session = Session(proxy, ep);
        session.HttpClient.Response.Headers.AddHeader(KnownHeaders.AltSvc, "h3=\":443\"; ma=60");
        ProxyServer.MaybeInjectClientAltSvc(session);
        Assert.AreEqual(1, session.HttpClient.Response.Headers.GetHeaders("Alt-Svc")!.Count);
        Assert.AreEqual("h3=\":443\"; ma=60", session.HttpClient.Response.Headers.GetFirstHeader("Alt-Svc")!.Value);
    }

    [TestMethod]
    public void Disabled_InjectsNothing_AndOptionsArePerEndpoint()
    {
        using var proxy = new ProxyServer();
        var off = new TransparentProxyEndPoint(IPAddress.Loopback, 8443, true)
        {
            EnableHttp3 = true,
            AltSvc = new AltSvcOptions { Enabled = false },
        };
        var custom = new TransparentProxyEndPoint(IPAddress.Loopback, 8443, true)
        {
            EnableHttp3 = true,
            AltSvc = new AltSvcOptions { MaxAgeSeconds = 30, Port = 443 },
        };

        var offSession = Session(proxy, off);
        ProxyServer.MaybeInjectClientAltSvc(offSession);
        Assert.IsFalse(offSession.HttpClient.Response.Headers.HeaderExists("Alt-Svc"));

        var customSession = Session(proxy, custom);
        ProxyServer.MaybeInjectClientAltSvc(customSession);
        Assert.AreEqual("h3=\":443\"; ma=30", customSession.HttpClient.Response.Headers.GetFirstHeader("Alt-Svc")?.Value);
        Assert.AreEqual("h3=\":8443\"; ma=86400", new TransparentProxyEndPoint(IPAddress.Loopback, 8443, true) { EnableHttp3 = true }.ClientAltSvcValue);
    }

    [TestMethod]
    public void ReassigningOptions_ClearsTheCachedValue()
    {
        var ep = new TransparentProxyEndPoint(IPAddress.Loopback, 8443, true)
        {
            EnableHttp3 = true,
            AltSvc = new AltSvcOptions { MaxAgeSeconds = 10 },
        };
        Assert.AreEqual("h3=\":8443\"; ma=10", ep.ClientAltSvcValue);
        ep.AltSvc = new AltSvcOptions { MaxAgeSeconds = 20 };
        Assert.AreEqual("h3=\":8443\"; ma=20", ep.ClientAltSvcValue);
    }

    [TestMethod]
    public void CachedValue_AllocatesNothingAfterFirstRead()
    {
#if DEBUG
        Assert.Inconclusive("Allocation ceilings are enforced in Release.");
#else
        var ep = new TransparentProxyEndPoint(IPAddress.Loopback, 8443, true) { EnableHttp3 = true };
        _ = ep.ClientAltSvcValue;
        const int n = 10_000;
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < n; i++)
        {
            _ = ep.ClientAltSvcValue;
        }

        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.AreEqual(0, allocated / n, $"cached Alt-Svc read allocated {allocated / n} bytes");
#endif
    }

    [TestMethod]
    public void Http3Off_DoesNotInject()
    {
        using var proxy = new ProxyServer();
        var ep = new TransparentProxyEndPoint(IPAddress.Loopback, 8443, true);
        var session = Session(proxy, ep);
        ProxyServer.MaybeInjectClientAltSvc(session);
        Assert.IsFalse(session.HttpClient.Response.Headers.HeaderExists("Alt-Svc"));
    }

    private static SessionEventArgs Session(ProxyServer proxy, ProxyEndPoint endPoint)
    {
        var connection = new QuicClientConnection(
            proxy, new IPEndPoint(IPAddress.Loopback, 4433), new IPEndPoint(IPAddress.Loopback, 12345));
        var cts = new CancellationTokenSource();
        var stream = new HttpClientStream(proxy, connection, Stream.Null, proxy.BufferPool, cts.Token);
        return new SessionEventArgs(proxy, endPoint, stream, null, cts);
    }
}
