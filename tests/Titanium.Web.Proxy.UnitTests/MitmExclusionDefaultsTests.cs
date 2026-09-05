using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy;

namespace Titanium.Web.Proxy.UnitTests;

[TestClass]
public class MitmExclusionDefaultsTests
{
    [TestMethod]
    public void CreateSystemProxySettings_Merge_KeepsIdentityHost()
    {
        var settings = MitmExclusionDefaults.CreateSystemProxySettings(
            proxyLoopback: true,
            ["*.corp.example.com"],
            MitmExclusionMode.Merge);
        Assert.IsTrue(settings.BypassRules.Contains("login.live.com"));
        Assert.IsTrue(settings.BypassRules.Contains("*.corp.example.com"));
    }

    [TestMethod]
    public void SystemProxyBypassRules_IncludeGitForgesSoHttpsRemotesStayDirect()
    {
        CollectionAssert.Contains(MitmExclusionDefaults.SystemProxyBypassRules, "github.com");
        CollectionAssert.Contains(MitmExclusionDefaults.SystemProxyBypassRules, "*.github.com");
        CollectionAssert.Contains(MitmExclusionDefaults.SystemProxyBypassRules, "*.githubusercontent.com");
        CollectionAssert.Contains(MitmExclusionDefaults.SystemProxyBypassRules, "gitlab.com");
        CollectionAssert.Contains(MitmExclusionDefaults.SystemProxyBypassRules, "bitbucket.org");
        CollectionAssert.Contains(MitmExclusionDefaults.SystemProxyBypassRules, "dev.azure.com");

        Assert.IsTrue(MitmExclusionDefaults.IsBuiltInSslBypass("github.com"));
        Assert.IsTrue(MitmExclusionDefaults.IsBuiltInSslBypass("api.github.com"));
        Assert.IsTrue(MitmExclusionDefaults.IsBuiltInSslBypass("codeload.github.com"));
        Assert.IsTrue(MitmExclusionDefaults.IsBuiltInSslBypass("raw.githubusercontent.com"));
        Assert.IsTrue(MitmExclusionDefaults.ShouldDisableSslDecrypt("github.com"));
    }

    [TestMethod]
    public void CreateSystemProxySettings_Replace_OmitsIdentityHostWhenRemoved()
    {
        var withoutIdentity = MitmExclusionDefaults.SystemProxyBypassRules
            .Where(r => r != "login.live.com")
            .ToList();
        var settings = MitmExclusionDefaults.CreateSystemProxySettings(
            proxyLoopback: true,
            withoutIdentity,
            MitmExclusionMode.Replace);
        Assert.IsFalse(settings.BypassRules.Contains("login.live.com"));
        Assert.IsTrue(settings.BypassRules.Contains("*.microsoftonline.com"));
        Assert.AreEqual(withoutIdentity.Count, settings.BypassRules.Count);
    }

    [TestMethod]
    public void ShouldDisableSslDecrypt_Merge_ForcesBuiltIn()
    {
        Assert.IsTrue(MitmExclusionDefaults.ShouldDisableSslDecrypt(
            "login.live.com", null, ["login.live.com"], MitmExclusionMode.Merge));
        Assert.IsTrue(MitmExclusionDefaults.ShouldDisableSslDecrypt(
            "content.dropbox.com", [], null, MitmExclusionMode.Merge));
    }

    [TestMethod]
    public void ShouldDisableSslDecrypt_Replace_HonorsCallerListOnly()
    {
        Assert.IsFalse(MitmExclusionDefaults.ShouldDisableSslDecrypt(
            "login.live.com", [], null, MitmExclusionMode.Replace));
        Assert.IsTrue(MitmExclusionDefaults.ShouldDisableSslDecrypt(
            "login.live.com", ["login.live.com"], null, MitmExclusionMode.Replace));
        Assert.IsFalse(MitmExclusionDefaults.ShouldDisableSslDecrypt(
            "content.dropbox.com", ["webex.com"], null, MitmExclusionMode.Replace));
    }
}
