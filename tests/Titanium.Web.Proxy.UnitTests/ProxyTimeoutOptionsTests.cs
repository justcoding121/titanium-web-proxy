using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Options;

namespace Titanium.Web.Proxy.UnitTests;

/// <summary>
///     Construction-time validation for <see cref="ProxyTimeoutOptions" />: every deadline must
///     reject zero/negative durations, nullable deadlines must accept <see langword="null" /> as an
///     explicit "no deadline" state, and <see cref="ProxyTimeoutOptions.Default" /> itself must be a
///     valid, constructible snapshot carrying forward today's shipped values.
/// </summary>
[TestClass]
public class ProxyTimeoutOptionsTests
{
    [TestMethod]
    public void Default_IsValid_AndMatchesTodaysShippedValues()
    {
        var timeouts = ProxyTimeoutOptions.Default;

        Assert.AreEqual(TimeSpan.FromSeconds(60), timeouts.IdleReadTimeout,
            "60-second connection timeout is today's ProxyServer.ConnectionTimeOutSeconds default.");
        Assert.AreEqual(TimeSpan.FromSeconds(20), timeouts.ConnectTimeout,
            "20-second connect timeout is today's ProxyServer.ConnectTimeOutSeconds default.");
        Assert.IsNull(timeouts.TotalRequestTimeout, "No end-to-end deadline under the Balanced default.");
    }

    [TestMethod]
    public void Create_AllValid_RoundTripsValues()
    {
        var timeouts = ProxyTimeoutOptions.Create(
            clientHeaderTimeout: TimeSpan.FromSeconds(5),
            connectTimeout: TimeSpan.FromSeconds(10),
            responseHeaderTimeout: TimeSpan.FromSeconds(15),
            idleReadTimeout: TimeSpan.FromSeconds(20),
            idleWriteTimeout: TimeSpan.FromSeconds(25),
            callbackTimeout: TimeSpan.FromSeconds(30),
            drainTimeout: TimeSpan.FromSeconds(2),
            streamTimeout: TimeSpan.FromSeconds(45),
            totalRequestTimeout: TimeSpan.FromMinutes(5));

        Assert.AreEqual(TimeSpan.FromSeconds(5), timeouts.ClientHeaderTimeout);
        Assert.AreEqual(TimeSpan.FromSeconds(30), timeouts.CallbackTimeout);
        Assert.AreEqual(TimeSpan.FromMinutes(5), timeouts.TotalRequestTimeout);
    }

    [TestMethod]
    public void Create_NullableDeadlinesAcceptNull_MeaningNoDeadline()
    {
        var timeouts = ProxyTimeoutOptions.Create(
            clientHeaderTimeout: TimeSpan.FromSeconds(5),
            connectTimeout: TimeSpan.FromSeconds(10),
            responseHeaderTimeout: TimeSpan.FromSeconds(15),
            idleReadTimeout: TimeSpan.FromSeconds(20),
            idleWriteTimeout: TimeSpan.FromSeconds(25),
            callbackTimeout: null,
            drainTimeout: TimeSpan.FromSeconds(2),
            streamTimeout: null,
            totalRequestTimeout: null);

        Assert.IsNull(timeouts.CallbackTimeout);
        Assert.IsNull(timeouts.StreamTimeout);
        Assert.IsNull(timeouts.TotalRequestTimeout);
    }

    [TestMethod]
    public void Create_ZeroClientHeaderTimeout_Throws()
    {
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => ProxyTimeoutOptions.Create(
            clientHeaderTimeout: TimeSpan.Zero,
            connectTimeout: TimeSpan.FromSeconds(10),
            responseHeaderTimeout: TimeSpan.FromSeconds(15),
            idleReadTimeout: TimeSpan.FromSeconds(20),
            idleWriteTimeout: TimeSpan.FromSeconds(25),
            callbackTimeout: null,
            drainTimeout: TimeSpan.FromSeconds(2),
            streamTimeout: null,
            totalRequestTimeout: null));
    }

    [TestMethod]
    public void Create_NegativeConnectTimeout_Throws()
    {
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => ProxyTimeoutOptions.Create(
            clientHeaderTimeout: TimeSpan.FromSeconds(5),
            connectTimeout: TimeSpan.FromSeconds(-1),
            responseHeaderTimeout: TimeSpan.FromSeconds(15),
            idleReadTimeout: TimeSpan.FromSeconds(20),
            idleWriteTimeout: TimeSpan.FromSeconds(25),
            callbackTimeout: null,
            drainTimeout: TimeSpan.FromSeconds(2),
            streamTimeout: null,
            totalRequestTimeout: null));
    }

    [TestMethod]
    public void Create_ZeroCallbackTimeout_WhenPresent_Throws()
    {
        // Present-but-zero must still be rejected: null is the only spelling of "no deadline",
        // so a caller cannot accidentally construct a callback timeout of zero duration.
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => ProxyTimeoutOptions.Create(
            clientHeaderTimeout: TimeSpan.FromSeconds(5),
            connectTimeout: TimeSpan.FromSeconds(10),
            responseHeaderTimeout: TimeSpan.FromSeconds(15),
            idleReadTimeout: TimeSpan.FromSeconds(20),
            idleWriteTimeout: TimeSpan.FromSeconds(25),
            callbackTimeout: TimeSpan.Zero,
            drainTimeout: TimeSpan.FromSeconds(2),
            streamTimeout: null,
            totalRequestTimeout: null));
    }
}
