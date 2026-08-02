using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Network;

namespace Titanium.Web.Proxy.UnitTests;

[TestClass]
public class ConnectionIdTests
{
    [TestMethod]
    public void Next_NeverReturnsZero()
    {
        for (var i = 0; i < 100; i++)
            Assert.AreNotEqual(0, ConnectionId.Next());
    }

    [TestMethod]
    public async Task Next_ConcurrentCallers_ReceiveDistinctIds()
    {
        const int callers = 32;
        const int perCaller = 2_000;
        var seen = new ConcurrentDictionary<long, byte>();
        var duplicates = 0;

        var tasks = new Task[callers];
        for (var t = 0; t < callers; t++)
        {
            tasks[t] = Task.Run(() =>
            {
                for (var i = 0; i < perCaller; i++)
                {
                    var id = ConnectionId.Next();
                    if (!seen.TryAdd(id, 0))
                        Interlocked.Increment(ref duplicates);
                }
            });
        }

        await Task.WhenAll(tasks);

        Assert.AreEqual(0, duplicates, "two threads must never receive the same connection id");
        Assert.AreEqual(callers * perCaller, seen.Count);
    }
}
