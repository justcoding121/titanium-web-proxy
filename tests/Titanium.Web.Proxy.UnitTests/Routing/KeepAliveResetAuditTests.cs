using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Reflection;
using System.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Abstractions.Clusters;
using Titanium.Web.Proxy.Abstractions.Diagnostics;
using Titanium.Web.Proxy.Abstractions.Routing;
using Titanium.Web.Proxy.EventArguments;
using Titanium.Web.Proxy.Models;
using Titanium.Web.Proxy.UnitTests.Support;

namespace Titanium.Web.Proxy.UnitTests.Routing;

/// <summary>
/// Fails when a future per-request field is added and forgotten in
/// <see cref="SessionEventArgs.ResetForKeepAlive"/>. Keep-alive reuse without a reset leaks
/// route, retry, TLS and policy state into the next request on the same client connection.
/// </summary>
[TestClass]
public class KeepAliveResetAuditTests
{
    private static readonly string[] KeepAliveClearedFields =
    [
        nameof(SessionEventArgsBase.OperationCancellationToken),
        nameof(SessionEventArgsBase.Exception),
        nameof(SessionEventArgsBase.IsClientResponseCommitted),
        nameof(SessionEventArgs.IsFastPath),
        nameof(SessionEventArgs.CloseClientConnectionAfterResponse),
        nameof(SessionEventArgs.IsPromise),
        nameof(SessionEventArgs.Http3BufferedBodyReader),
        nameof(SessionEventArgs.Http3RequestBodyPump),
        nameof(SessionEventArgs.ResponseHeaderTimeout),
        nameof(SessionEventArgs.IdleReadTimeout),
        nameof(SessionEventArgs.IdleWriteTimeout),
        nameof(SessionEventArgs.RequestTimeout),
        nameof(SessionEventArgs.MaxBufferedBodyBytes),
        nameof(SessionEventArgs.NetworkFailureRetryAttempts),
        nameof(SessionEventArgs.MaxWebSocketFramePayloadBytes),
        nameof(SessionEventArgs.OriginHttpVersionPolicy),
        nameof(SessionEventArgs.UpstreamHttpProtocol),
        nameof(SessionEventArgsBase.ConnectTimeout),
        nameof(SessionEventArgsBase.UpstreamPinnedAddress),
        nameof(SessionEventArgsBase.UpstreamHttpClient),
        nameof(SessionEventArgsBase.ResponseTrailerPlan),
        nameof(SessionEventArgsBase.CustomUpStreamProxy),
        nameof(SessionEventArgsBase.CustomUpStreamProxyUsed),
        nameof(SessionEventArgsBase.UpstreamConnectHost),
        nameof(SessionEventArgsBase.UpstreamConnectPort),
        nameof(SessionEventArgsBase.UpstreamDestinationId),
        nameof(SessionEventArgsBase.ResponseHeaderTransformPlan),
        nameof(SessionEventArgsBase.DestinationRequestLease),
        nameof(SessionEventArgsBase.PassiveHealth),
        nameof(SessionEventArgsBase.PassiveDestinationCount),
        nameof(SessionEventArgsBase.MatchedRoute),
        nameof(SessionEventArgsBase.ClusterConnectionKey),
        nameof(SessionEventArgsBase.ForwarderError),
        nameof(SessionEventArgsBase.Extras),
        nameof(SessionEventArgsBase.UserData),
    ];

    [TestMethod]
    public void KeepAliveResetCoversAllSessionFields()
    {
        using var proxy = new ProxyServer(userTrustRootCertificate: false);
        using var dirty = TestSessions.Create(proxy);
        using var fresh = TestSessions.Create(proxy);

        Dirtify(dirty);
        dirty.ResetForKeepAlive(null, null);

        var mismatches = new List<string>();
        foreach (var name in KeepAliveClearedFields)
        {
            var prop = FindProperty(name);
            Assert.IsNotNull(prop, $"Missing property {name} — update KeepAliveClearedFields.");
            var left = prop!.GetValue(dirty);
            var right = prop.GetValue(fresh);
            if (!Equals(left, right))
            {
                mismatches.Add($"{name}: dirty={Describe(left)} fresh={Describe(right)}");
            }
        }

        Assert.AreEqual(0, mismatches.Count, string.Join("; ", mismatches));
    }

    [TestMethod]
    public void RoutingFeatureState_Reset_ClearsEveryMutableField()
    {
        var state = new RoutingFeatureState
        {
            Retry = new RetryConfig { Attempts = 3 },
            StatusRetries = 1,
            RequestStartTimestamp = 42,
            Activity = new Activity("test"),
            RequestId = "rid",
            PendingSetCookie = "a=b",
            HeldConnectionPolicy = "p",
        };
        state.Remember("d1");
        state.Remember("d2");
        Assert.AreEqual(2, state.TriedCount);

        state.Reset();

        Assert.IsNull(state.Retry);
        Assert.AreEqual(0, state.StatusRetries);
        Assert.AreEqual(0L, state.RequestStartTimestamp);
        Assert.IsNull(state.Activity);
        Assert.IsNull(state.RequestId);
        Assert.IsNull(state.PendingSetCookie);
        Assert.IsNull(state.HeldConnectionPolicy);
        Assert.AreEqual(0, state.TriedCount);
        Assert.IsFalse(state.AlreadyTried("d1"));
    }

    [TestMethod]
    public async System.Threading.Tasks.Task BoundedMapStress_NeverExceedsCap()
    {
        const int max = 64;
        var (finalCount, evictions) = await BoundedMapStress.RunAsync(max, threads: 8, operationsPerThread: 2_000);
        Assert.IsTrue(finalCount <= max, $"finalCount={finalCount} max={max}");
        Assert.IsTrue(evictions > 0);
    }

    [TestMethod]
    public void DisposableLiveCount_ReturnsToZeroAfterDispose()
    {
        var before = DisposableLiveCount.ProcessLive;
        var items = new DisposableLiveCount[50];
        for (var i = 0; i < items.Length; i++)
        {
            items[i] = new DisposableLiveCount();
        }

        Assert.AreEqual(before + items.Length, DisposableLiveCount.ProcessLive);
        foreach (var item in items)
        {
            item.Dispose();
        }

        Assert.AreEqual(before, DisposableLiveCount.ProcessLive);
    }

    private static void Dirtify(SessionEventArgs session)
    {
        session.OperationCancellationToken = CancellationToken.None;
        session.Exception = new InvalidOperationException("leak");
        session.IsClientResponseCommitted = true;
        session.IsFastPath = true;
        session.CloseClientConnectionAfterResponse = true;
        session.IsPromise = true;
        session.Http3BufferedBodyReader = _ => System.Threading.Tasks.Task.FromResult(Array.Empty<byte>());
        session.Http3RequestBodyPump = (_, _) => System.Threading.Tasks.Task.CompletedTask;
        session.ResponseHeaderTimeout = TimeSpan.FromSeconds(1);
        session.IdleReadTimeout = TimeSpan.FromSeconds(2);
        session.IdleWriteTimeout = TimeSpan.FromSeconds(3);
        session.RequestTimeout = TimeSpan.FromSeconds(4);
        session.MaxBufferedBodyBytes = 1;
        session.NetworkFailureRetryAttempts = 2;
        session.MaxWebSocketFramePayloadBytes = 3;
        session.OriginHttpVersionPolicy = OriginHttpVersionPolicy.NormalizeToHttp11;
        session.UpstreamHttpProtocol = UpstreamHttpProtocol.Http11;
        session.ConnectTimeout = TimeSpan.FromSeconds(5);
        session.UpstreamPinnedAddress = IPAddress.Parse("10.0.0.1");
        session.UpstreamHttpClient = new ClusterHttpClientConfig { MaxConnectionsPerServer = 1 };
        session.ResponseTrailerPlan = new Dictionary<string, string> { ["t"] = "1" };
        session.CustomUpStreamProxy = null;
        session.CustomUpStreamProxyUsed = null;
        session.UpstreamConnectHost = "origin.example";
        session.UpstreamConnectPort = 8443;
        session.UpstreamDestinationId = "dest-1";
        session.ResponseHeaderTransformPlan = new object();
        session.DestinationRequestLease = new DisposableLiveCount();
        session.PassiveHealth = new PassiveHealthConfig();
        session.PassiveDestinationCount = 7;
        session.MatchedRoute = new RouteConfig
        {
            Id = "r",
            ClusterId = "c",
            Match = new RouteMatch { Path = "/", PathKind = PathMatchKind.Prefix },
        };
        session.ClusterConnectionKey = "key";
        session.ForwarderError = ForwarderError.PolicyDenied;
        session.UserData = "userdata";
        var extras = new RoutingFeatureState
        {
            Retry = new RetryConfig { Attempts = 2 },
            StatusRetries = 1,
            RequestStartTimestamp = 99,
            RequestId = "id",
            PendingSetCookie = "c=1",
            HeldConnectionPolicy = "conn",
        };
        extras.Remember("tried");
        session.Extras = extras;
    }

    private static PropertyInfo? FindProperty(string name)
    {
        return typeof(SessionEventArgs).GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
               ?? typeof(SessionEventArgsBase).GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
    }

    private static string Describe(object? value) => value is null ? "null" : value.GetType().Name + ":" + value;
}
