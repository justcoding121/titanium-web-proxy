using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Inspector.Services;
using Titanium.Web.Proxy.Network;

namespace Titanium.Inspector.Tests;

[TestClass]
public class MacSslTrustWaitUxTests
{
    [TestMethod]
    public void OsTrustUxCopy_MacWaitBody_MentionsAlwaysTrustAndAutoContinue()
    {
        StringAssert.Contains(OsTrustUxCopy.MacSslTrustWaitBody, "Always Trust");
        StringAssert.Contains(OsTrustUxCopy.MacSslTrustWaitBody, "Use System Defaults");
        StringAssert.Contains(OsTrustUxCopy.MacSslTrustWaitBody, "password");
        StringAssert.Contains(OsTrustUxCopy.MacSslTrustNotSavedYet, "toggle");
        StringAssert.Contains(OsTrustUxCopy.MacSslTrustWaitConfirmSaved, "saved");
        Assert.IsFalse(OsTrustUxCopy.MacSslTrustWaitBody.Contains("Linux", StringComparison.Ordinal));
        Assert.IsFalse(OsTrustUxCopy.MacSslTrustWaitBody.Contains("Windows", StringComparison.Ordinal));
        Assert.IsFalse(OsTrustUxCopy.MacSslTrustWaitBody.Contains("NSS", StringComparison.Ordinal));
        Assert.IsFalse(OsTrustUxCopy.MacSslTrustWaitStatusInKeychain.Contains("Linux", StringComparison.Ordinal));
    }

    [TestMethod]
    public void OsTrustUxCopy_ConfirmInstall_CurrentOsOnly_NoCrossOsSlash()
    {
        var body = OsTrustUxCopy.ConfirmInstallRootCaBody();
        Assert.IsFalse(body.Contains("macOS/Linux", StringComparison.Ordinal));
        Assert.IsFalse(body.Contains("Keychain/NSS", StringComparison.Ordinal));
        if (OperatingSystem.IsMacOS())
        {
            StringAssert.Contains(body, "Keychain");
            Assert.IsFalse(body.Contains("Linux", StringComparison.Ordinal));
            Assert.IsFalse(body.Contains("NSS", StringComparison.Ordinal));
        }
    }

    [TestMethod]
    public void OsTrustUxCopy_ExcludedHosts_CurrentOsOnly()
    {
        var intro = OsTrustUxCopy.ExcludedHostsIntro();
        Assert.IsFalse(intro.Contains("Windows, macOS, and Linux", StringComparison.Ordinal));
        var loop = OsTrustUxCopy.ExcludedHostsLoopbackHint();
        Assert.IsFalse(loop.Contains("macOS/Linux", StringComparison.Ordinal));
    }

    [TestMethod]
    public void FormatDecryptTrustFailed_Mac_UsesContinueNotIveConfirmed()
    {
        var result = CertificateOsTrustResult.Fail(
            CertificateOsTrustKind.MacNeedsManualTrustConfirm,
            "needs Always Trust");
        var (_, _, primary, secondary, _) = OsTrustUxCopy.FormatDecryptTrustFailed(result);
        Assert.AreEqual("Continue in Keychain Access", primary);
        Assert.AreEqual("Export CA", secondary);
        Assert.IsFalse(primary.Contains("I've confirmed", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task ScriptedDialogs_ShowMacSslTrustWait_ReturnsConfiguredResult()
    {
        var dialogs = new ScriptedInspectorDialogs
        {
            MacSslTrustWaitResult = MacSslTrustWaitResult.Cancelled,
        };
        var opened = false;
        var result = await dialogs.ShowMacSslTrustWaitAsync(
            owner: null,
            verifySslTrust: () => false,
            openKeychain: () => opened = true,
            isInLoginKeychain: () => true);
        Assert.AreEqual(MacSslTrustWaitResult.Cancelled, result);
        Assert.AreEqual(1, dialogs.MacSslTrustWaitCalls);
        Assert.IsTrue(opened);

        dialogs.MacSslTrustWaitResult = MacSslTrustWaitResult.Trusted;
        var verified = false;
        result = await dialogs.ShowMacSslTrustWaitAsync(
            owner: null,
            verifySslTrust: () =>
            {
                verified = true;
                return true;
            },
            openKeychain: () => { });
        Assert.AreEqual(MacSslTrustWaitResult.Trusted, result);
        Assert.IsTrue(verified);
        Assert.AreEqual(2, dialogs.MacSslTrustWaitCalls);

        dialogs.MacSslTrustWaitResult = MacSslTrustWaitResult.NotSavedYet;
        result = await dialogs.ShowMacSslTrustWaitAsync(
            owner: null,
            verifySslTrust: () => false,
            openKeychain: () => { });
        Assert.AreEqual(MacSslTrustWaitResult.NotSavedYet, result);
    }
}
