using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Abstractions.Routing;
using Titanium.Web.Proxy.Http;
using Titanium.Web.Proxy.Models;
using Titanium.Web.Proxy.Routing;
using Titanium.Web.Proxy.UnitTests.Support;

namespace Titanium.Web.Proxy.UnitTests.Routing;

/// <summary>An authorization service that is slow, down, or asked the same thing many times at once.</summary>
[TestClass]
[DoNotParallelize]
public class ExtAuthStampedeTests
{
    [TestInitialize]
    public void Reset() => RoutePolicies.ResetForTests();

    [TestCleanup]
    public void Restore() => RoutePolicies.ResetForTests();

    private static Request Req(string path = "/x", string token = "t")
    {
        var request = new Request { Method = "GET", RequestUriString8 = (ByteString)path, Host = "app" };
        request.Headers.AddHeader("Authorization", token);
        return request;
    }

    private static RoutePolicyConfig Policy(int port, bool failOpen, int timeoutMilliseconds, int cacheSeconds = 0) => new()
    {
        Name = "auth",
        Kind = "extAuth",
        Url = "http://127.0.0.1:" + port + "/check",
        FailOpen = failOpen,
        TimeoutMilliseconds = timeoutMilliseconds,
        CacheSeconds = cacheSeconds,
        CopyResponseHeaders = ["X-User"],
    };

    /// <summary>A bare TCP "service". It either never answers or answers 200 after a delay, and counts what it sees.</summary>
    private sealed class FakeService : IDisposable
    {
        private readonly TcpListener listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource stop = new();
        private readonly TimeSpan delay;
        private readonly bool answer;
        private int accepted;
        private int concurrent;
        private int peak;

        public FakeService(bool answer, TimeSpan delay)
        {
            this.answer = answer;
            this.delay = delay;
            listener.Start();
            _ = Task.Run(AcceptLoop);
        }

        public int Port => ((IPEndPoint)listener.LocalEndpoint).Port;
        public int Accepted => Volatile.Read(ref accepted);
        public int Peak => Volatile.Read(ref peak);

        private async Task AcceptLoop()
        {
            try
            {
                while (!stop.IsCancellationRequested)
                {
                    var client = await listener.AcceptTcpClientAsync(stop.Token).ConfigureAwait(false);
                    Interlocked.Increment(ref accepted);
                    _ = Task.Run(() => Serve(client));
                }
            }
            catch (Exception)
            {
                // Stopped.
            }
        }

        private async Task Serve(TcpClient client)
        {
            var now = Interlocked.Increment(ref concurrent);
            int seen;
            while (now > (seen = Volatile.Read(ref peak)) && Interlocked.CompareExchange(ref peak, now, seen) != seen)
            {
            }

            try
            {
                using (client)
                {
                    var stream = client.GetStream();
                    var buffer = new byte[4096];
                    var text = new StringBuilder();
                    while (!text.ToString().Contains("\r\n\r\n", StringComparison.Ordinal))
                    {
                        var read = await stream.ReadAsync(buffer, stop.Token).ConfigureAwait(false);
                        if (read == 0)
                        {
                            return;
                        }

                        text.Append(Encoding.ASCII.GetString(buffer, 0, read));
                    }

                    if (!answer)
                    {
                        await Task.Delay(Timeout.Infinite, stop.Token).ConfigureAwait(false);
                        return;
                    }

                    await Task.Delay(delay, stop.Token).ConfigureAwait(false);
                    var response = Encoding.ASCII.GetBytes(
                        "HTTP/1.1 200 OK\r\nContent-Length: 0\r\nX-User: alice\r\nConnection: close\r\n\r\n");
                    await stream.WriteAsync(response, stop.Token).ConfigureAwait(false);
                }
            }
            catch (Exception)
            {
                // The client gave up (a timeout) or the test ended.
            }
            finally
            {
                Interlocked.Decrement(ref concurrent);
            }
        }

        public void Dispose()
        {
            stop.Cancel();
            listener.Stop();
            stop.Dispose();
        }
    }

    [TestMethod]
    public async Task AlwaysTimingOut_FailClosed_DoesNotPileUpAndRefusesEveryRequest()
    {
        using var service = new FakeService(answer: false, delay: TimeSpan.Zero);
        var limits = new ExternalAuthorization.ExtAuthLimits(MaxInFlight: 4, FailureThreshold: 3, Cooldown: TimeSpan.FromSeconds(30));
        var client = new ExternalAuthorization.ExtAuthClient(Policy(service.Port, failOpen: false, timeoutMilliseconds: 150), limits);

        // Distinct credentials, so nothing can be answered from a cache or shared with another request.
        var tasks = Enumerable.Range(0, 300)
            .Select(i => Task.Run(async () => await client.CheckAsync(Req(token: "t" + i), IPAddress.Loopback)))
            .ToArray();
        var verdicts = await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(30));

        Assert.IsTrue(verdicts.All(v => !v.Allowed && v.Status == 503), "an unreachable service must never let a request through");
        Assert.IsTrue(service.Peak <= 4, "never more subrequests at once than the bound, saw " + service.Peak);
        Assert.IsTrue(service.Accepted <= 4 + 3, "the cooldown stops new calls once the service has failed repeatedly, saw " + service.Accepted);
        Assert.AreEqual(0, client.InFlight, "every slot is given back");
    }

    [TestMethod]
    public async Task Saturation_FollowsFailOpen()
    {
        using var service = new FakeService(answer: false, delay: TimeSpan.Zero);
        var limits = new ExternalAuthorization.ExtAuthLimits(MaxInFlight: 1, FailureThreshold: 1000, Cooldown: TimeSpan.FromSeconds(30));

        var closed = new ExternalAuthorization.ExtAuthClient(Policy(service.Port, failOpen: false, timeoutMilliseconds: 500), limits);
        var holder = closed.CheckAsync(Req(token: "a"), IPAddress.Loopback).AsTask();
        await WaitUntil(() => closed.InFlight == 1);
        var refused = await closed.CheckAsync(Req(token: "b"), IPAddress.Loopback);
        Assert.IsFalse(refused.Allowed, "saturated and failOpen=false: refused at once, not queued");
        Assert.AreEqual(503, refused.Status);
        await holder;

        var open = new ExternalAuthorization.ExtAuthClient(Policy(service.Port, failOpen: true, timeoutMilliseconds: 500), limits);
        var holderOpen = open.CheckAsync(Req(token: "a"), IPAddress.Loopback).AsTask();
        await WaitUntil(() => open.InFlight == 1);
        var admitted = await open.CheckAsync(Req(token: "b"), IPAddress.Loopback);
        Assert.IsTrue(admitted.Allowed, "saturated and failOpen=true: allowed at once");
        await holderOpen;
    }

    [TestMethod]
    public async Task Cooldown_StopsCallingTheService_ThenLetsOneProbeThrough()
    {
        var clock = new ManualTimeProvider();
        RoutePolicies.Clock = clock;
        using var service = new FakeService(answer: false, delay: TimeSpan.Zero);
        var limits = new ExternalAuthorization.ExtAuthLimits(MaxInFlight: 8, FailureThreshold: 2, Cooldown: TimeSpan.FromSeconds(10));
        var client = new ExternalAuthorization.ExtAuthClient(Policy(service.Port, failOpen: false, timeoutMilliseconds: 100), limits);

        for (var i = 0; i < 2; i++)
        {
            Assert.AreEqual(503, (await client.CheckAsync(Req(token: "f" + i), IPAddress.Loopback)).Status);
        }

        var before = service.Accepted;
        for (var i = 0; i < 20; i++)
        {
            Assert.AreEqual(503, (await client.CheckAsync(Req(token: "c" + i), IPAddress.Loopback)).Status);
        }

        Assert.AreEqual(before, service.Accepted, "no subrequest is sent during the cooldown");

        clock.Advance(TimeSpan.FromSeconds(11));
        Assert.AreEqual(503, (await client.CheckAsync(Req(token: "probe"), IPAddress.Loopback)).Status);
        Assert.AreEqual(before + 1, service.Accepted, "one probe goes out once the cooldown has run");
        Assert.AreEqual(503, (await client.CheckAsync(Req(token: "after"), IPAddress.Loopback)).Status);
        Assert.AreEqual(before + 1, service.Accepted, "a failed probe starts another cooldown");
    }

    [TestMethod]
    public async Task IdenticalConcurrentLookups_ShareOneSubrequest()
    {
        using var service = new FakeService(answer: true, delay: TimeSpan.FromMilliseconds(300));
        var client = new ExternalAuthorization.ExtAuthClient(Policy(service.Port, failOpen: false, timeoutMilliseconds: 5000, cacheSeconds: 60));

        var verdicts = await Task.WhenAll(Enumerable.Range(0, 50).Select(_ =>
            Task.Run(async () => await client.CheckAsync(Req(), IPAddress.Loopback))));

        Assert.IsTrue(verdicts.All(v => v.Allowed));
        Assert.AreEqual(1, service.Accepted, "fifty identical lookups cost the service one call");
        Assert.IsTrue(verdicts.All(v => v.Copy!.Any(h => h.Key == "X-User" && h.Value == "alice")));

        var other = await client.CheckAsync(Req(token: "different"), IPAddress.Loopback);
        Assert.IsTrue(other.Allowed);
        Assert.AreEqual(2, service.Accepted, "a different credential is a different lookup");
    }

    [TestMethod]
    public async Task CachedHeaders_AreReadOnly_AndNotSharedAsAMutableList()
    {
        using var service = new FakeService(answer: true, delay: TimeSpan.Zero);
        var client = new ExternalAuthorization.ExtAuthClient(Policy(service.Port, failOpen: false, timeoutMilliseconds: 5000, cacheSeconds: 60));

        var first = await client.CheckAsync(Req(), IPAddress.Loopback);
        var second = await client.CheckAsync(Req(), IPAddress.Loopback);

        Assert.AreEqual(1, service.Accepted, "the second answer came from the cache");
        Assert.IsNotNull(second.Copy);
        Assert.IsNotInstanceOfType<List<KeyValuePair<string, string>>>(second.Copy);
        Assert.IsNotInstanceOfType<KeyValuePair<string, string>[]>(second.Copy);
        var writable = (ICollection<KeyValuePair<string, string>>)second.Copy!;
        Assert.IsTrue(writable.IsReadOnly);
        Assert.ThrowsExactly<NotSupportedException>(() => writable.Add(new KeyValuePair<string, string>("X-Evil", "1")));
        Assert.AreEqual(first.Copy!.Count, second.Copy!.Count);
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            Assert.IsTrue(DateTime.UtcNow < deadline, "timed out waiting for the condition");
            await Task.Delay(5);
        }
    }
}
