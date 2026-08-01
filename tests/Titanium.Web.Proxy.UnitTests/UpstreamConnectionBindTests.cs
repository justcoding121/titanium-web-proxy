using System;
using System.Net;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Diagnostics;
using Titanium.Web.Proxy.Http;

namespace Titanium.Web.Proxy.UnitTests;

/// <summary>
///     Covers Bind-only upstream connection metadata used by multiplexed H2/H3 (including QUIC) so
///     <c>ServerRemoteEndPoint</c> and <c>UpstreamConnectionTiming</c> work without <c>HasConnection</c>.
/// </summary>
[TestClass]
public class UpstreamConnectionBindTests
{
    [TestMethod]
    public void BindUpstreamConnection_SetsIdentityEndPointAndTiming()
    {
        var client = new HttpWebClient(null, new Request(), new Lazy<int>(() => 0));
        const long id = 42;
        var endpoint = new IPEndPoint(IPAddress.Parse("203.0.113.10"), 443);
        var timing = new UpstreamConnectionTiming(DateTime.UtcNow);

        client.BindUpstreamConnection(id, endpoint, timing);

        Assert.AreEqual(id, client.UpstreamConnectionId);
        Assert.AreEqual(endpoint, client.UpstreamRemoteEndPoint);
        Assert.AreSame(timing, client.UpstreamConnectionTiming);
        Assert.IsFalse(client.HasConnection);
    }

    [TestMethod]
    public void BindUpstreamConnection_AllowsNullEndPointAndTiming()
    {
        var client = new HttpWebClient(null, new Request(), new Lazy<int>(() => 0));

        client.BindUpstreamConnection(7, null, null);

        Assert.IsNotNull(client.UpstreamConnectionId);
        Assert.IsNull(client.UpstreamRemoteEndPoint);
        Assert.IsNull(client.UpstreamConnectionTiming);
        Assert.IsFalse(client.HasConnection);
    }

    [TestMethod]
    public void BindUpstreamConnection_Rebind_OverwritesStaleMetadata()
    {
        // Mirrors the H3 path falling back to TCP after a QUIC attempt already bound its own metadata:
        // nothing from the abandoned connection may survive.
        var client = new HttpWebClient(null, new Request(), new Lazy<int>(() => 0));
        client.BindUpstreamConnection(11, new IPEndPoint(IPAddress.Parse("203.0.113.10"), 443),
            new UpstreamConnectionTiming(DateTime.UtcNow));

        const long tcpId = 22;
        var tcpEndPoint = new IPEndPoint(IPAddress.Parse("198.51.100.7"), 8080);
        client.BindUpstreamConnection(tcpId, tcpEndPoint, null);

        Assert.AreEqual(tcpId, client.UpstreamConnectionId);
        Assert.AreEqual(tcpEndPoint, client.UpstreamRemoteEndPoint);
        Assert.IsNull(client.UpstreamConnectionTiming, "stale QUIC timing must not survive TCP fallback");
    }

    [TestMethod]
    public void FinishSession_ClearsBoundUpstreamMetadata()
    {
        var client = new HttpWebClient(null, new Request(), new Lazy<int>(() => 0));
        client.BindUpstreamConnection(33, new IPEndPoint(IPAddress.Loopback, 8443),
            new UpstreamConnectionTiming(DateTime.UtcNow));

        client.FinishSession();

        Assert.IsNull(client.UpstreamConnectionId);
        Assert.IsNull(client.UpstreamRemoteEndPoint);
        Assert.IsNull(client.UpstreamConnectionTiming);
        Assert.IsFalse(client.HasConnection);
    }
}
