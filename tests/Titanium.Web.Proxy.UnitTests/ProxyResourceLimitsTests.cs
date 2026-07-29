using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Options;

namespace Titanium.Web.Proxy.UnitTests;

/// <summary>
///     Construction-time validation for <see cref="ProxyResourceLimits" />: every bound must reject
///     zero/negative values, nullable bounds must accept <see langword="null" /> as an explicit
///     "disabled" state, and <see cref="ProxyResourceLimits.Default" /> itself must be a valid,
///     constructible snapshot.
/// </summary>
[TestClass]
public class ProxyResourceLimitsTests
{
    [TestMethod]
    public void Default_IsValid()
    {
        var limits = ProxyResourceLimits.Default;

        Assert.IsTrue(limits.MaxHeaderLineBytes > 0);
        Assert.IsTrue(limits.MaxConcurrentStreamsPerConnection > 0);
        Assert.IsTrue(limits.ConnectionPoolingEnabled);
        Assert.IsTrue(limits.MaxCachedConnectionsPerHost > 0);
        Assert.IsNull(limits.MaxConcurrentClients, "Admission cap is opt-in under the Balanced default.");
        Assert.IsNull(limits.MaxEncodedBodyBytes, "Body budget is opt-in under the Balanced default.");
        Assert.IsTrue(limits.MaxOpenHeaderBlockFrames > 0);
        Assert.IsTrue(limits.MaxOpenHeaderBlockDuration > TimeSpan.Zero);
    }

    [TestMethod]
    public void Create_ZeroMaxOpenHeaderBlockDuration_Throws()
    {
        // Like MaxOpenHeaderBlockFrames, this is always-enforced: it is the wall-clock half of the
        // CONTINUATION-flood guard, so it must never be representable as "disabled".
        Assert.ThrowsException<ArgumentOutOfRangeException>(() => ProxyResourceLimits.Create(
            maxHeaderLineBytes: 8192,
            maxHeaderCount: 64,
            maxHeaderAggregateBytes: 32768,
            maxEncodedBodyBytes: null,
            maxDecodedBodyBytes: null,
            maxDecompressionRatio: null,
            maxConcurrentClients: null,
            maxConcurrentStreamsPerConnection: 50,
            maxPeerInitiatedIncompleteStreamResets: null,
            maxOpenHeaderBlockFrames: 16,
            maxOpenHeaderBlockDuration: TimeSpan.Zero,
            connectionPoolingEnabled: true,
            maxCachedConnectionsPerHost: 1,
            maxCertificateCacheEntries: null));
    }

    [TestMethod]
    public void Create_AllValid_RoundTripsValues()
    {
        var limits = ProxyResourceLimits.Create(
            maxHeaderLineBytes: 8192,
            maxHeaderCount: 64,
            maxHeaderAggregateBytes: 32768,
            maxEncodedBodyBytes: 1024,
            maxDecodedBodyBytes: 2048,
            maxDecompressionRatio: 10,
            maxConcurrentClients: 500,
            maxConcurrentStreamsPerConnection: 50,
            maxPeerInitiatedIncompleteStreamResets: 20,
            maxOpenHeaderBlockFrames: 16,
            maxOpenHeaderBlockDuration: TimeSpan.FromSeconds(5),
            connectionPoolingEnabled: true,
            maxCachedConnectionsPerHost: 2,
            maxCertificateCacheEntries: 100);

        Assert.AreEqual(8192, limits.MaxHeaderLineBytes);
        Assert.AreEqual(64, limits.MaxHeaderCount);
        Assert.AreEqual(1024L, limits.MaxEncodedBodyBytes);
        Assert.AreEqual(10d, limits.MaxDecompressionRatio);
        Assert.AreEqual(500, limits.MaxConcurrentClients);
        Assert.AreEqual(20, limits.MaxPeerInitiatedIncompleteStreamResets);
        Assert.AreEqual(100, limits.MaxCertificateCacheEntries);
    }

    [TestMethod]
    public void Create_NullableBoundsAcceptNull_MeaningDisabled()
    {
        var limits = ProxyResourceLimits.Create(
            maxHeaderLineBytes: 8192,
            maxHeaderCount: 64,
            maxHeaderAggregateBytes: 32768,
            maxEncodedBodyBytes: null,
            maxDecodedBodyBytes: null,
            maxDecompressionRatio: null,
            maxConcurrentClients: null,
            maxConcurrentStreamsPerConnection: 50,
            maxPeerInitiatedIncompleteStreamResets: null,
            maxOpenHeaderBlockFrames: 16,
            maxOpenHeaderBlockDuration: TimeSpan.FromSeconds(5),
            connectionPoolingEnabled: false,
            maxCachedConnectionsPerHost: 1,
            maxCertificateCacheEntries: null);

        Assert.IsNull(limits.MaxEncodedBodyBytes);
        Assert.IsNull(limits.MaxDecodedBodyBytes);
        Assert.IsNull(limits.MaxDecompressionRatio);
        Assert.IsNull(limits.MaxConcurrentClients);
        Assert.IsNull(limits.MaxPeerInitiatedIncompleteStreamResets);
        Assert.IsNull(limits.MaxCertificateCacheEntries);
        Assert.IsFalse(limits.ConnectionPoolingEnabled);
    }

    [TestMethod]
    [DataRow(0L)]
    [DataRow(-1L)]
    public void Create_NonPositiveMaxHeaderLineBytes_Throws(long value)
    {
        Assert.ThrowsException<ArgumentOutOfRangeException>(() => ProxyResourceLimits.Create(
            maxHeaderLineBytes: value,
            maxHeaderCount: 64,
            maxHeaderAggregateBytes: 32768,
            maxEncodedBodyBytes: null,
            maxDecodedBodyBytes: null,
            maxDecompressionRatio: null,
            maxConcurrentClients: null,
            maxConcurrentStreamsPerConnection: 50,
            maxPeerInitiatedIncompleteStreamResets: null,
            maxOpenHeaderBlockFrames: 16,
            maxOpenHeaderBlockDuration: TimeSpan.FromSeconds(5),
            connectionPoolingEnabled: true,
            maxCachedConnectionsPerHost: 1,
            maxCertificateCacheEntries: null));
    }

    [TestMethod]
    public void Create_ZeroMaxConcurrentStreamsPerConnection_Throws()
    {
        // This is the always-enforced HTTP/2 concurrency cap: unlike the nullable budgets, it must
        // never be representable as "disabled", since that would let a peer open unlimited streams.
        Assert.ThrowsException<ArgumentOutOfRangeException>(() => ProxyResourceLimits.Create(
            maxHeaderLineBytes: 8192,
            maxHeaderCount: 64,
            maxHeaderAggregateBytes: 32768,
            maxEncodedBodyBytes: null,
            maxDecodedBodyBytes: null,
            maxDecompressionRatio: null,
            maxConcurrentClients: null,
            maxConcurrentStreamsPerConnection: 0,
            maxPeerInitiatedIncompleteStreamResets: null,
            maxOpenHeaderBlockFrames: 16,
            maxOpenHeaderBlockDuration: TimeSpan.FromSeconds(5),
            connectionPoolingEnabled: true,
            maxCachedConnectionsPerHost: 1,
            maxCertificateCacheEntries: null));
    }

    [TestMethod]
    public void Create_ZeroMaxCachedConnectionsPerHost_ThrowsEvenWhenPoolingDisabled()
    {
        // This is exactly the "0 spins forever holding the pool lock" defect the plan requires to
        // become unrepresentable: disabling pooling is ConnectionPoolingEnabled=false, never a
        // zero-valued cache size.
        Assert.ThrowsException<ArgumentOutOfRangeException>(() => ProxyResourceLimits.Create(
            maxHeaderLineBytes: 8192,
            maxHeaderCount: 64,
            maxHeaderAggregateBytes: 32768,
            maxEncodedBodyBytes: null,
            maxDecodedBodyBytes: null,
            maxDecompressionRatio: null,
            maxConcurrentClients: null,
            maxConcurrentStreamsPerConnection: 50,
            maxPeerInitiatedIncompleteStreamResets: null,
            maxOpenHeaderBlockFrames: 16,
            maxOpenHeaderBlockDuration: TimeSpan.FromSeconds(5),
            connectionPoolingEnabled: false,
            maxCachedConnectionsPerHost: 0,
            maxCertificateCacheEntries: null));
    }

    [TestMethod]
    public void Create_NegativeMaxDecompressionRatio_Throws()
    {
        Assert.ThrowsException<ArgumentOutOfRangeException>(() => ProxyResourceLimits.Create(
            maxHeaderLineBytes: 8192,
            maxHeaderCount: 64,
            maxHeaderAggregateBytes: 32768,
            maxEncodedBodyBytes: null,
            maxDecodedBodyBytes: null,
            maxDecompressionRatio: -5,
            maxConcurrentClients: null,
            maxConcurrentStreamsPerConnection: 50,
            maxPeerInitiatedIncompleteStreamResets: null,
            maxOpenHeaderBlockFrames: 16,
            maxOpenHeaderBlockDuration: TimeSpan.FromSeconds(5),
            connectionPoolingEnabled: true,
            maxCachedConnectionsPerHost: 1,
            maxCertificateCacheEntries: null));
    }
}
