using System.Net;
using System.Reflection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Plus.Resilience;
using Titanium.Web.Proxy;
using Titanium.Web.Proxy.Abstractions.Clusters;
using Titanium.Web.Proxy.Abstractions.Routing;
using Titanium.Web.Proxy.Clusters;
using Titanium.Web.Proxy.EventArguments;
using Titanium.Web.Proxy.Helpers;
using Titanium.Web.Proxy.Http;
using Titanium.Web.Proxy.Models;
using Titanium.Web.Proxy.Network.Tcp;

namespace Titanium.Plus.Tests;

/// <summary>
///     Circuit-breaker destination resolution and cooldown recovery.
///     Bug classes: Host/[ipv6] never match; cooldown never recovers ejected destinations.
/// </summary>
[TestClass]
public class CircuitBreakerDestinationResolutionBehaviorTests
{
    [TestMethod]
    public async Task ResolveDestinationId_MatchesUpstreamId_Host_AndIpv6BracketHost()
    {
        var manager = new ClusterManager();
        await manager.ApplyAsync(
        [
            new ClusterConfig
            {
                Id = "c1",
                Destinations =
                [
                    new DestinationConfig { Id = "v4", Address = "127.0.0.1", Port = 9 },
                    new DestinationConfig { Id = "v6", Address = "::1", Port = 8443 },
                ],
            },
        ]);

        using var proxy = new ProxyServer(userTrustRootCertificate: false);
        var clock = new ManualTimeProvider();
        using var circuit = new CircuitBreakerController(proxy, manager, threshold: 2, cooldown: TimeSpan.FromSeconds(30), clock);
        using var session = MakeSession(proxy);

        session.UpstreamDestinationId = "v4";
        Assert.AreEqual("v4", Resolve(circuit, session));

        session.UpstreamDestinationId = null;
        session.HttpClient.Request.Host = "127.0.0.1:9";
        Assert.AreEqual("v4", Resolve(circuit, session));

        session.HttpClient.Request.Host = "[::1]:8443";
        Assert.AreEqual("v6", Resolve(circuit, session));

        session.HttpClient.Request.Host = "missing.example:9";
        Assert.IsNull(Resolve(circuit, session));
    }

    [TestMethod]
    public async Task RecoverExpiredForTests_RestoresHealthyAfterCooldown()
    {
        var manager = new ClusterManager();
        await manager.ApplyAsync(
        [
            new ClusterConfig
            {
                Id = "c1",
                Destinations = [new DestinationConfig { Id = "d1", Address = "127.0.0.1", Port = 9 }],
            },
        ]);

        using var proxy = new ProxyServer(userTrustRootCertificate: false);
        var clock = new ManualTimeProvider();
        using var circuit = new CircuitBreakerController(proxy, manager, threshold: 2, cooldown: TimeSpan.FromSeconds(10), clock);

        circuit.RecordStatusForTests("d1", 500);
        circuit.RecordStatusForTests("d1", 503);
        Assert.AreEqual(DestinationState.Unhealthy, manager.GetDestinationState("d1"));

        circuit.RecoverExpiredForTests();
        Assert.AreEqual(DestinationState.Unhealthy, manager.GetDestinationState("d1"), "cooldown must still hold");

        clock.Advance(TimeSpan.FromSeconds(11));
        circuit.RecoverExpiredForTests();
        Assert.AreEqual(DestinationState.Healthy, manager.GetDestinationState("d1"));
    }

    private static string? Resolve(CircuitBreakerController circuit, SessionEventArgs session) =>
        (string?)typeof(CircuitBreakerController)
            .GetMethod("ResolveDestinationId", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(circuit, [session]);

    private static SessionEventArgs MakeSession(ProxyServer proxy)
    {
        var endPoint = new ExplicitProxyEndPoint(IPAddress.Loopback, 0, false);
        var connection = new QuicClientConnection(
            proxy, new IPEndPoint(IPAddress.Loopback, 4433), new IPEndPoint(IPAddress.Loopback, 12345));
        var cts = new CancellationTokenSource();
        var clientStream = new HttpClientStream(proxy, connection, Stream.Null, proxy.BufferPool, cts.Token);
        var session = new SessionEventArgs(proxy, endPoint, clientStream, null, cts);
        session.HttpClient.Request.HttpVersion = HttpHeader.Version11;
        session.HttpClient.Request.Method = "GET";
        session.HttpClient.Request.RequestUriString = "http://127.0.0.1/";
        session.HttpClient.Response.StatusCode = 500;
        return session;
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 3, 4, 5, 6, 7, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now += by;
    }
}
