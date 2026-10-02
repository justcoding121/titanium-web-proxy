using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Concurrent;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Extensions;
using Titanium.Web.Proxy.Http;
using Titanium.Web.Proxy.Models;
using Titanium.Web.Proxy.Network;

namespace Titanium.Web.Proxy.UnitTests;

[TestClass]
public class H1TerminateLiteClientPoolTests
{
    private static Request NewRequest()
    {
        var request = new Request { Method = "GET", HttpVersion = HttpHeader.Version11 };
        request.RequestUriString8 = "/".GetByteString();
        return request;
    }

    private static void Drain()
    {
        for (var i = 0; i < ProxyServer.H1TerminateLiteClientPoolCap * 4; i++)
            ProxyServer.RentH1TerminateLiteClient(NewRequest());
    }

    [TestMethod]
    public void Rent_AfterRelease_ReusesShellAndRebindsRequest()
    {
        Drain();
        var first = NewRequest();
        var client = ProxyServer.RentH1TerminateLiteClient(first);
        Assert.AreSame(first, client.Request);
        Assert.IsNotNull(client.Response);

        ProxyServer.ReleaseH1TerminateLiteClient(client);
        var second = NewRequest();
        var again = ProxyServer.RentH1TerminateLiteClient(second);

        Assert.AreSame(client, again);
        Assert.AreSame(second, again.Request);
        Assert.IsFalse(again.HasConnection);
    }

    [TestMethod]
    public void Release_BeyondCap_DropsExtraShells()
    {
        Drain();
        var extra = 50;
        var shells = new List<HttpWebClient>();
        for (var i = 0; i < ProxyServer.H1TerminateLiteClientPoolCap + extra; i++)
            shells.Add(ProxyServer.RentH1TerminateLiteClient(NewRequest()));
        foreach (var shell in shells)
            ProxyServer.ReleaseH1TerminateLiteClient(shell);

        var known = new HashSet<HttpWebClient>(shells, ReferenceEqualityComparer.Instance);
        var reused = 0;
        for (var i = 0; i < ProxyServer.H1TerminateLiteClientPoolCap + extra; i++)
        {
            if (known.Contains(ProxyServer.RentH1TerminateLiteClient(NewRequest())))
                reused++;
        }

        Assert.AreEqual(ProxyServer.H1TerminateLiteClientPoolCap, reused);
    }

    [TestMethod]
    public void ConcurrentRentRelease_NeverHandsOneShellToTwoOwners()
    {
        Drain();
        var inUse = new ConcurrentDictionary<HttpWebClient, int>(ReferenceEqualityComparer.Instance);
        var failures = 0;
        const int workers = 16;
        const int iterations = 20_000;

        Parallel.For(0, workers, new ParallelOptions { MaxDegreeOfParallelism = workers }, _ =>
        {
            for (var i = 0; i < iterations; i++)
            {
                var request = NewRequest();
                var client = ProxyServer.RentH1TerminateLiteClient(request);
                if (!ReferenceEquals(client.Request, request) || !inUse.TryAdd(client, 1))
                    Interlocked.Increment(ref failures);
                if ((i & 7) == 0)
                    Thread.Yield();
                if (!ReferenceEquals(client.Request, request))
                    Interlocked.Increment(ref failures);
                inUse.TryRemove(client, out _);
                ProxyServer.ReleaseH1TerminateLiteClient(client);
            }
        });

        Assert.AreEqual(0, failures);
    }
}
