using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Options;

namespace Titanium.Web.Proxy.UnitTests;

/// <summary>
///     <see cref="ResolvedSessionPolicy" /> is a thin, validated composite of
///     <see cref="ProxyResourceLimits" /> and <see cref="ProxyTimeoutOptions" />; these tests cover its
///     own construction contract (null rejection, round-tripping, the immutable "with" replacement
///     shape) rather than re-testing the two halves' own validation, which is already covered by
///     <see cref="ProxyResourceLimitsTests" /> and <see cref="ProxyTimeoutOptionsTests" />.
/// </summary>
[TestClass]
public class ResolvedSessionPolicyTests
{
    [TestMethod]
    public void Default_CombinesBothDefaults()
    {
        var policy = ResolvedSessionPolicy.Default;

        Assert.AreSame(ProxyResourceLimits.Default, policy.ResourceLimits);
        Assert.AreSame(ProxyTimeoutOptions.Default, policy.Timeouts);
    }

    [TestMethod]
    public void Create_RoundTripsBothHalves()
    {
        var limits = ProxyResourceLimits.Default;
        var timeouts = ProxyTimeoutOptions.Default;

        var policy = ResolvedSessionPolicy.Create(limits, timeouts);

        Assert.AreSame(limits, policy.ResourceLimits);
        Assert.AreSame(timeouts, policy.Timeouts);
    }

    [TestMethod]
    public void Create_NullResourceLimits_Throws()
    {
        Assert.ThrowsException<ArgumentNullException>(() =>
            ResolvedSessionPolicy.Create(null!, ProxyTimeoutOptions.Default));
    }

    [TestMethod]
    public void Create_NullTimeouts_Throws()
    {
        Assert.ThrowsException<ArgumentNullException>(() =>
            ResolvedSessionPolicy.Create(ProxyResourceLimits.Default, null!));
    }

    [TestMethod]
    public void WithResourceLimits_ReplacesOnlyResourceLimits_LeavingOriginalUnchanged()
    {
        var original = ResolvedSessionPolicy.Default;
        var lowered = ProxyResourceLimits.Create(
            maxHeaderLineBytes: 4096,
            maxHeaderCount: 32,
            maxHeaderAggregateBytes: 16384,
            maxEncodedBodyBytes: 1024,
            maxDecodedBodyBytes: 2048,
            maxDecompressionRatio: 10,
            maxConcurrentClients: null,
            maxConcurrentStreamsPerConnection: 50,
            maxPeerInitiatedIncompleteStreamResets: null,
            maxOpenHeaderBlockFrames: 64,
            maxOpenHeaderBlockDuration: TimeSpan.FromSeconds(5),
            connectionPoolingEnabled: true,
            maxCachedConnectionsPerHost: 2,
            maxCertificateCacheEntries: null);

        var overridden = original.WithResourceLimits(lowered);

        Assert.AreSame(lowered, overridden.ResourceLimits);
        Assert.AreSame(original.Timeouts, overridden.Timeouts,
            "Only the resource-limits half should change; timeouts are shared, not re-resolved.");
        Assert.AreSame(ProxyResourceLimits.Default, original.ResourceLimits,
            "The original snapshot must not be mutated in place.");
    }

    [TestMethod]
    public void WithResourceLimits_Null_Throws()
    {
        Assert.ThrowsException<ArgumentNullException>(() =>
            ResolvedSessionPolicy.Default.WithResourceLimits(null!));
    }
}
