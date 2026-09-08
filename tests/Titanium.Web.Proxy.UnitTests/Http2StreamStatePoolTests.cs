using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Http2;

namespace Titanium.Web.Proxy.UnitTests;

[TestClass]
public class Http2StreamStatePoolTests
{
    [TestMethod]
    public void Return_DoesNotExceedMaxRetained_UnderConcurrentReturns()
    {
        const int cap = 8;
        var pool = new Http2StreamStatePool(cap);
        var extra = new List<Http2StreamState>(64);
        for (var i = 0; i < 64; i++)
            extra.Add(new Http2StreamState(i + 1));

        Parallel.ForEach(extra, state => pool.Return(state));

        Assert.IsTrue(pool.Retained <= cap, $"retained {pool.Retained} exceeded cap {cap}");

        var rented = 0;
        while (true)
        {
            var state = pool.RentCompressed(100 + rented);
            rented++;
            if (rented > cap + 8)
                break;
            if (pool.Retained == 0 && rented > cap)
                break;
        }

        Assert.IsTrue(pool.Retained <= cap);
    }

    [TestMethod]
    public void RentCompressed_ReusesReturnedShell()
    {
        var pool = new Http2StreamStatePool(4);
        var first = pool.RentCompressed(1);
        pool.Return(first);
        var second = pool.RentCompressed(2);
        Assert.AreSame(first, second);
        Assert.AreEqual(2, second.StreamId);
    }
}
