#pragma warning disable TWP001 // Experimental HTTP/3 API — intentional Alt-Svc coverage
using System;
using System.IO;
using System.Net;
using System.Reflection;
using System.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.EventArguments;
using Titanium.Web.Proxy.Helpers;
using Titanium.Web.Proxy.Models;
using Titanium.Web.Proxy.Network.Tcp;

namespace Titanium.Web.Proxy.UnitTests;

/// <summary>
///     AfterResponse Alt-Svc → <see cref="Http3.Http3OriginCapabilityCache" /> bookkeeping must never
///     throw (including path-only origins with no Host/Authority).
/// </summary>
[TestClass]
public class Http3DiscoveryHandlerTests
{
    private static readonly BindingFlags PrivateInstance =
        BindingFlags.Instance | BindingFlags.NonPublic;

    private static SessionEventArgs MakeSession(ProxyServer proxy)
    {
        var endPoint = new ExplicitProxyEndPoint(IPAddress.Loopback, 0, false);
        var connection = new QuicClientConnection(
            proxy, new IPEndPoint(IPAddress.Loopback, 4433), new IPEndPoint(IPAddress.Loopback, 12345));
        var cts = new CancellationTokenSource();
        var clientStream = new HttpClientStream(proxy, connection, Stream.Null, proxy.BufferPool, cts.Token);
        return new SessionEventArgs(proxy, endPoint, clientStream, null, cts);
    }

    private static void InvokeTryUpdate(ProxyServer proxy, SessionEventArgs session)
    {
        var method = typeof(ProxyServer).GetMethod("TryUpdateHttp3CapabilityFromResponse", PrivateInstance);
        Assert.IsNotNull(method);
        method!.Invoke(proxy, [session]);
    }

    [TestMethod]
    public void AltSvc_ValidH3_CachesCapabilityForOrigin()
    {
        using var proxy = new ProxyServer(false, false, false) { EnableHttp3 = true };
        var session = MakeSession(proxy);
        session.HttpClient.Request.Method = "GET";
        session.HttpClient.Request.RequestUriString = "/";
        session.HttpClient.Request.Headers.AddHeader("Host", "altsvc.example.com");
        session.HttpClient.Response.Headers.AddHeader("Alt-Svc", "h3=\":443\"; ma=86400");

        InvokeTryUpdate(proxy, session);

        Assert.IsTrue(proxy.Http3OriginCapabilityCache.TryGet("altsvc.example.com:443", out _));
    }

    [TestMethod]
    public void AltSvc_Clear_EvictsCachedCapability()
    {
        using var proxy = new ProxyServer(false, false, false) { EnableHttp3 = true };
        proxy.Http3OriginCapabilityCache.Set("clear.example.com:443");
        Assert.IsTrue(proxy.Http3OriginCapabilityCache.TryGet("clear.example.com:443", out _));

        var session = MakeSession(proxy);
        session.HttpClient.Request.Method = "GET";
        session.HttpClient.Request.RequestUriString = "/";
        session.HttpClient.Request.Headers.AddHeader("Host", "clear.example.com");
        session.HttpClient.Response.Headers.AddHeader("Alt-Svc", "clear");

        InvokeTryUpdate(proxy, session);

        Assert.IsFalse(proxy.Http3OriginCapabilityCache.TryGet("clear.example.com:443", out _));
    }

    [TestMethod]
    public void AltSvc_PathOnlyUnparseableOrigin_DoesNotThrowOrWrite()
    {
        using var proxy = new ProxyServer(false, false, false) { EnableHttp3 = true };
        var session = MakeSession(proxy);
        session.HttpClient.Request.Method = "GET";
        session.HttpClient.Request.RequestUriString = "/";
        // No Host / Authority — GetOriginHostPort returns empty host.
        session.HttpClient.Response.Headers.AddHeader("Alt-Svc", "h3=\":443\"; ma=86400");

        InvokeTryUpdate(proxy, session);

        Assert.IsFalse(proxy.Http3OriginCapabilityCache.TryGet(":443", out _));
        Assert.IsFalse(proxy.Http3OriginCapabilityCache.TryGet(":80", out _));
    }

    [TestMethod]
    public void AltSvc_Clear_UnparseableOrigin_DoesNotThrow()
    {
        using var proxy = new ProxyServer(false, false, false) { EnableHttp3 = true };
        var session = MakeSession(proxy);
        session.HttpClient.Request.Method = "GET";
        session.HttpClient.Request.RequestUriString = "/";
        session.HttpClient.Response.Headers.AddHeader("Alt-Svc", "clear");

        InvokeTryUpdate(proxy, session);
    }

    [TestMethod]
    public void AltSvc_EnableHttp3False_IsNoOp()
    {
        using var proxy = new ProxyServer(false, false, false) { EnableHttp3 = false };
        var session = MakeSession(proxy);
        session.HttpClient.Request.Method = "GET";
        session.HttpClient.Request.RequestUriString = "/";
        session.HttpClient.Request.Headers.AddHeader("Host", "off.example.com");
        session.HttpClient.Response.Headers.AddHeader("Alt-Svc", "h3=\":443\"; ma=86400");

        InvokeTryUpdate(proxy, session);

        Assert.IsFalse(proxy.Http3OriginCapabilityCache.TryGet("off.example.com:443", out _));
    }

    [TestMethod]
    public void AltSvc_MissingHeader_IsNoOp()
    {
        using var proxy = new ProxyServer(false, false, false) { EnableHttp3 = true };
        var session = MakeSession(proxy);
        session.HttpClient.Request.Method = "GET";
        session.HttpClient.Request.RequestUriString = "/";
        session.HttpClient.Request.Headers.AddHeader("Host", "none.example.com");

        InvokeTryUpdate(proxy, session);

        Assert.IsFalse(proxy.Http3OriginCapabilityCache.TryGet("none.example.com:443", out _));
    }
}
