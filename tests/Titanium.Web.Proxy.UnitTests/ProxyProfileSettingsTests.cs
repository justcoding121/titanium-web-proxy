using System.Security.Authentication;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Options;

namespace Titanium.Web.Proxy.UnitTests;

[TestClass]
public class ProxyProfileSettingsTests
{
    [TestMethod]
    public void For_Balanced_MatchesTheStaticBalancedInstance()
    {
        Assert.AreSame(ProxyProfileSettings.Balanced, ProxyProfileSettings.For(ProxyProfile.Balanced));
    }

    [TestMethod]
    public void For_LegacyCompatible_MatchesTheStaticLegacyCompatibleInstance()
    {
        Assert.AreSame(ProxyProfileSettings.LegacyCompatible, ProxyProfileSettings.For(ProxyProfile.LegacyCompatible));
    }

    [TestMethod]
    public void For_PublicFacing_MatchesTheStaticPublicFacingInstance()
    {
        Assert.AreSame(ProxyProfileSettings.PublicFacing, ProxyProfileSettings.For(ProxyProfile.PublicFacing));
    }

    [TestMethod]
    public void For_UnknownProfile_Throws()
    {
        Assert.ThrowsExactly<System.ArgumentOutOfRangeException>(
            () => ProxyProfileSettings.For((ProxyProfile)999));
    }

    [TestMethod]
    public void Balanced_IsAllEnforceWithModernTlsAndNoOutboundBlocking()
    {
        var settings = ProxyProfileSettings.Balanced;

        Assert.AreEqual(SslProtocols.Tls12 | SslProtocols.Tls13, settings.SupportedSslProtocols);
        Assert.IsFalse(settings.BlockPrivateNetworkDestinations);
        Assert.IsNull(settings.MaxConcurrentClientConnections);
        Assert.AreEqual(PolicyMode.Enforce, settings.PolicyModes[PolicyFamily.BodyBudget]);
        Assert.AreEqual(PolicyMode.Enforce, settings.PolicyModes[PolicyFamily.AdmissionControl]);
        Assert.IsFalse(settings.PolicyModes.AllowAmbiguousFraming);
    }

    [TestMethod]
    public void LegacyCompatible_ObservesAdmissionAndHeaderLimits_ButStillEnforcesBodyAndDecompressionBudgets()
    {
        var settings = ProxyProfileSettings.LegacyCompatible;

        Assert.AreEqual(PolicyMode.Enforce, settings.PolicyModes[PolicyFamily.BodyBudget]);
        Assert.AreEqual(PolicyMode.Enforce, settings.PolicyModes[PolicyFamily.DecompressionRatio]);
        Assert.AreEqual(PolicyMode.Observe, settings.PolicyModes[PolicyFamily.HeaderLimits]);
        Assert.AreEqual(PolicyMode.Observe, settings.PolicyModes[PolicyFamily.AdmissionControl]);
        Assert.AreEqual(PolicyMode.Observe, settings.PolicyModes[PolicyFamily.Http2AbuseBudget]);
    }

    [TestMethod]
    public void LegacyCompatible_OptsIntoLegacyTlsVersions()
    {
        var settings = ProxyProfileSettings.LegacyCompatible;

#pragma warning disable SYSLIB0039 // Asserting the deliberate legacy-TLS opt-in itself.
        Assert.IsTrue(settings.SupportedSslProtocols.HasFlag(SslProtocols.Tls));
        Assert.IsTrue(settings.SupportedSslProtocols.HasFlag(SslProtocols.Tls11));
#pragma warning restore SYSLIB0039
        Assert.IsTrue(settings.SupportedSslProtocols.HasFlag(SslProtocols.Tls12));
        Assert.IsTrue(settings.SupportedSslProtocols.HasFlag(SslProtocols.Tls13));
    }

    [TestMethod]
    public void PublicFacing_IsAllEnforceWithOutboundBlockingAndTighterTimeouts()
    {
        var settings = ProxyProfileSettings.PublicFacing;

        Assert.AreEqual(SslProtocols.Tls12 | SslProtocols.Tls13, settings.SupportedSslProtocols);
        Assert.IsTrue(settings.BlockPrivateNetworkDestinations);
        Assert.AreEqual(10_000, settings.MaxConcurrentClientConnections);
        Assert.AreEqual(PolicyMode.Enforce, settings.PolicyModes[PolicyFamily.BodyBudget]);
        Assert.AreEqual(PolicyMode.Enforce, settings.PolicyModes[PolicyFamily.AdmissionControl]);
        Assert.IsTrue(settings.ClientHeaderTimeoutSeconds > 0);
        Assert.IsTrue(settings.ResponseHeaderTimeoutSeconds > 0);
        Assert.IsTrue(settings.IdleReadTimeoutSeconds > 0);
        Assert.IsTrue(settings.IdleWriteTimeoutSeconds > 0);
        Assert.IsTrue(settings.RequestTimeoutSeconds > 0);
    }

    [TestMethod]
    public void NoProfile_EnablesTheAmbiguousFramingEscapeHatch()
    {
        // The escape hatch is deliberately unreachable through profile selection - see
        // ProxyPolicyModes' type-level remarks. Every shipped profile's snapshot must reflect that.
        Assert.IsFalse(ProxyProfileSettings.Balanced.PolicyModes.AllowAmbiguousFraming);
        Assert.IsFalse(ProxyProfileSettings.LegacyCompatible.PolicyModes.AllowAmbiguousFraming);
        Assert.IsFalse(ProxyProfileSettings.PublicFacing.PolicyModes.AllowAmbiguousFraming);
    }
}
