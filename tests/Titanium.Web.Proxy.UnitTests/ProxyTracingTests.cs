using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Abstractions.Diagnostics;
using Titanium.Web.Proxy.Diagnostics;
using Titanium.Web.Proxy.EventArguments;
using Titanium.Web.Proxy.Helpers;
using Titanium.Web.Proxy.Http;
using Titanium.Web.Proxy.Models;
using Titanium.Web.Proxy.Network.Tcp;
using Titanium.Web.Proxy.Routing;

namespace Titanium.Web.Proxy.UnitTests;

/// <summary>
///     The span helpers cost nothing while no listener is attached, and never record secret-bearing headers.
///     Tests that attach a listener live in the CLI test project, where the real exporter is exercised.
/// </summary>
[TestClass]
public class ProxyTracingTests
{
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
    public void NoListener_StartServerReturnsNull_WithoutAllocating()
    {
        if (ProxyMetrics.ActivitySource.HasListeners())
        {
            Assert.Inconclusive("Another test in this process holds a listener on the proxy ActivitySource.");
        }

        using var proxy = new ProxyServer { TracingEnabled = true, PropagateTraceContext = true };
        using var session = MakeSession(proxy);
        session.HttpClient.Request.Url = "http://origin.test/path?token=secret";

        // Warm up so JIT and one-time static initialisation are not counted.
        for (var i = 0; i < 100; i++)
        {
            Assert.IsNull(ProxyTracing.StartServer(proxy, session));
        }

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 10_000; i++)
        {
            ProxyTracing.StartServer(proxy, session);
            ProxyTracing.BeginUpstream(proxy, session, "dest");
            ProxyTracing.RecordRoute(null, "r", "c");
        }

        Assert.AreEqual(0, GC.GetAllocatedBytesForCurrentThread() - before);
        Assert.IsFalse(session.HttpClient.Request.Headers.HeaderExists("traceparent"));
    }

    [TestMethod]
    public void NoListener_DispatchWithTracingOn_AllocatesNoMoreThanWithTracingOff()
    {
        if (ProxyMetrics.ActivitySource.HasListeners())
        {
            Assert.Inconclusive("Another test in this process holds a listener on the proxy ActivitySource.");
        }

        using var off = new ProxyServer { TracingEnabled = false };
        using var on = new ProxyServer { TracingEnabled = true, PropagateTraceContext = true };
        using var offSession = MakeSession(off);
        using var onSession = MakeSession(on);

        for (var i = 0; i < 100; i++)
        {
            ReverseProxySessionDispatch.Apply(off, offSession);
            ReverseProxySessionDispatch.Apply(on, onSession);
        }

        var offBytes = Measure(off, offSession);
        var onBytes = Measure(on, onSession);

        Assert.AreEqual(offBytes, onBytes, "Tracing with no listener must add nothing to the request path.");
        Assert.IsNull(onSession.Extras?.Activity);

        static long Measure(ProxyServer server, SessionEventArgs session)
        {
            var before = GC.GetAllocatedBytesForCurrentThread();
            for (var i = 0; i < 5_000; i++)
            {
                ReverseProxySessionDispatch.Apply(server, session);
            }

            return GC.GetAllocatedBytesForCurrentThread() - before;
        }
    }

    [TestMethod]
    [DataRow("authorization", false)]
    [DataRow("Authorization", false)]
    [DataRow("COOKIE", false)]
    [DataRow("set-cookie", false)]
    [DataRow("proxy-authorization", false)]
    [DataRow("x-api-key", false)]
    [DataRow(" x-auth-token ", false)]
    [DataRow("", false)]
    [DataRow("x-request-id", true)]
    [DataRow("user-agent", true)]
    public void SecretBearingHeaders_AreNeverRecordable(string name, bool recordable)
    {
        Assert.AreEqual(recordable, ProxyTracing.IsRecordableHeader(name));
    }
}
