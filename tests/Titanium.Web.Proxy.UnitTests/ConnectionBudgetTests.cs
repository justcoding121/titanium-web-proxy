using System;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Clusters;
using Titanium.Web.Proxy.Exceptions;
using Titanium.Web.Proxy.Helpers;
using Titanium.Web.Proxy.Network.Tcp;
using Titanium.Web.Proxy.Routing;

namespace Titanium.Web.Proxy.UnitTests;

[TestClass]
public class ConnectionBudgetTests
{
    [TestMethod]
    public void Reserve_StopsAtTheCap_AndReleaseLetsTheNextCallerIn()
    {
        var budget = new ConnectionBudget(2);
        Assert.IsTrue(budget.TryReserve());
        Assert.IsTrue(budget.TryReserve());
        Assert.IsFalse(budget.TryReserve());
        budget.ReleaseSlot();
        Assert.IsTrue(budget.TryReserve());
        Assert.AreEqual(2, budget.Open);
    }

    [TestMethod]
    public async Task Waiter_WakesWhenASlotIsReleased()
    {
        var budget = new ConnectionBudget(1);
        Assert.IsTrue(budget.TryReserve());
        budget.EnterWait();
        var waiting = budget.WaitAsync(TimeSpan.FromSeconds(2), CancellationToken.None);
        budget.ReleaseSlot();
        Assert.IsTrue(await waiting);
        budget.ExitWait();
        Assert.IsTrue(budget.TryReserve());
    }

    [TestMethod]
    [Timeout(3_000)]
    public async Task CapReload_UsesNewMax()
    {
        var budget = new ConnectionBudget(1);
        Assert.IsTrue(budget.TryReserve());
        Assert.IsFalse(budget.TryReserve());
        budget.EnterWait();
        var waiting = budget.WaitAsync(TimeSpan.FromSeconds(2), CancellationToken.None);
        budget.SetMax(2);
        Assert.IsTrue(await waiting);
        budget.ExitWait();
        Assert.IsTrue(budget.TryReserve());
        Assert.AreEqual(2, budget.Open);
        Assert.ThrowsException<ArgumentOutOfRangeException>(() => budget.SetMax(0));
    }

    [TestMethod]
    public void CapacityTimeout_IsNotReportedAsADestinationFailure()
    {
        using var proxy = new ProxyServer();
        var manager = new ClusterManager();
        proxy.ReverseProxy = new Abstractions.ReverseProxyOptions
        {
            ClusterManager = manager,
            LoadBalancer = new LoadBalancer(),
        };
        var endPoint = new Models.ExplicitProxyEndPoint(IPAddress.Loopback, 0, false);
        var connection = new QuicClientConnection(
            proxy, new IPEndPoint(IPAddress.Loopback, 1), new IPEndPoint(IPAddress.Loopback, 2));
        using var cts = new CancellationTokenSource();
        var stream = new HttpClientStream(proxy, connection, System.IO.Stream.Null, proxy.BufferPool, cts.Token);
        var session = new EventArguments.SessionEventArgs(proxy, endPoint, stream, null, cts);
        session.UpstreamDestinationId = "dest-a";
        session.Exception = new UpstreamCapacityException("key", 2);
        session.HttpClient.Response.StatusCode = 503;

        ReverseProxySessionDispatch.ReportUpstreamResult(proxy, session, success: false);

        Assert.IsNull(session.DestinationRequestLease);
        Assert.AreEqual(0, manager.Snapshot.DestinationStates.Count);
    }
}
