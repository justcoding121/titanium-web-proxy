using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Inspector.Services;
using Titanium.Web.Proxy.Network;

namespace Titanium.Inspector.Tests;

/// <summary>
///     Remaining Avalonia-safe Inspector leftovers: OsTrustUxCopy OS copy arms / FormatStatus,
///     and SessionSearch keyed-token edit helpers still thin on Sonar develop.
/// </summary>
[TestClass]
public class OsTrustAndSessionSearchTokenCoverageBehaviorTests
{
    [TestMethod]
    public void OsTrustUxCopy_ConfirmBodies_AndFormatStatusKinds()
    {
        Assert.IsFalse(string.IsNullOrWhiteSpace(OsTrustUxCopy.ConfirmInstallRootCaBody()));
        Assert.IsFalse(string.IsNullOrWhiteSpace(OsTrustUxCopy.ConfirmRemoveRootCaBody()));
        Assert.IsFalse(string.IsNullOrWhiteSpace(OsTrustUxCopy.ConfirmElevateRootCaBody()));
        Assert.IsFalse(string.IsNullOrWhiteSpace(OsTrustUxCopy.ExcludedHostsIntro()));
        Assert.IsFalse(string.IsNullOrWhiteSpace(OsTrustUxCopy.ProxyLocalhostTip()));
        StringAssert.Contains(OsTrustUxCopy.TrustRecoveryAdminBody("base"), "base");
        Assert.IsFalse(string.IsNullOrWhiteSpace(OsTrustUxCopy.MacSslTrustWaitBody));
        Assert.IsFalse(string.IsNullOrWhiteSpace(OsTrustUxCopy.MacSslTrustWaitStatusWaiting));
        Assert.IsFalse(string.IsNullOrWhiteSpace(OsTrustUxCopy.MacSslTrustWaitStatusInKeychain));
        Assert.IsFalse(string.IsNullOrWhiteSpace(OsTrustUxCopy.MacSslTrustNotSavedYet));
        Assert.IsFalse(string.IsNullOrWhiteSpace(OsTrustUxCopy.MacSslTrustWaitConfirmSaved));

        Assert.IsFalse(string.IsNullOrWhiteSpace(OsTrustUxCopy.FormatStatus(null)));
        Assert.IsFalse(string.IsNullOrWhiteSpace(OsTrustUxCopy.FormatStatus(
            CertificateOsTrustResult.Fail(CertificateOsTrustKind.Cancelled, "cancelled"))));
        Assert.IsFalse(string.IsNullOrWhiteSpace(OsTrustUxCopy.FormatStatus(
            CertificateOsTrustResult.Fail(CertificateOsTrustKind.CertutilMissing, "certutil"))));
        Assert.IsFalse(string.IsNullOrWhiteSpace(OsTrustUxCopy.FormatStatus(
            CertificateOsTrustResult.Fail(CertificateOsTrustKind.HomebrewMissing, ""))));
        StringAssert.Contains(
            OsTrustUxCopy.FormatStatus(CertificateOsTrustResult.Fail(CertificateOsTrustKind.HomebrewMissing, "brew missing")),
            "brew missing");
        Assert.IsFalse(string.IsNullOrWhiteSpace(OsTrustUxCopy.FormatStatus(
            CertificateOsTrustResult.Fail(CertificateOsTrustKind.MacNeedsManualTrustConfirm, "confirm"))));
        Assert.IsFalse(string.IsNullOrWhiteSpace(OsTrustUxCopy.FormatStatus(
            CertificateOsTrustResult.Fail(CertificateOsTrustKind.MacKeychainFailed, "keychain"))));
        StringAssert.Contains(
            OsTrustUxCopy.FormatStatus(CertificateOsTrustResult.Fail(CertificateOsTrustKind.Failed, "detail")),
            "detail");
        Assert.IsFalse(string.IsNullOrWhiteSpace(OsTrustUxCopy.FormatStatus(
            CertificateOsTrustResult.Fail(CertificateOsTrustKind.Failed, ""))));
    }

    [TestMethod]
    public void SessionSearch_SetAddRemoveKeyedTokens_CoverWhitespaceAndBareParts()
    {
        Assert.AreEqual("", SessionSearch.RemoveKeyedTokens(null, "mark"));
        Assert.AreEqual("host:a", SessionSearch.RemoveKeyedTokens("mark:red host:a mark:blue", "mark"));
        Assert.AreEqual("login", SessionSearch.RemoveKeyedTokens("login mark:red", "mark"));

        Assert.AreEqual("host:a", SessionSearch.SetKeyedToken("host:a", "", "x"));
        Assert.AreEqual("", SessionSearch.SetKeyedToken(null, "mark", " "));
        Assert.AreEqual("mark:red", SessionSearch.SetKeyedToken(null, "Mark", "red"));
        Assert.AreEqual("host:a mark:first", SessionSearch.SetKeyedToken("mark:old host:a", "mark", "first second"));
        Assert.AreEqual("mark:only", SessionSearch.SetKeyedToken("mark:old", "mark", "only\tmore"));

        Assert.AreEqual("q", SessionSearch.AddToken("q", "", "x"));
        Assert.AreEqual("", SessionSearch.AddToken(null, "mark", " "));
        Assert.AreEqual("mark:red", SessionSearch.AddToken(null, "Mark", "red"));
        Assert.AreEqual("mark:red", SessionSearch.AddToken("mark:red", "mark", "red"));
        Assert.AreEqual("mark:red mark:blue", SessionSearch.AddToken("mark:red", "mark", "blue"));
        Assert.AreEqual("host:a mark:first", SessionSearch.AddToken("host:a", "mark", "first second"));
    }
}
