using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Inspector.Services;

namespace Titanium.Inspector.Tests;

[TestClass]
public class MitmBypassTests
{
    [TestMethod]
    public void CreateSystemProxySettings_AddsBypassRulesAndLoopback()
    {
        var withLoopback = MitmBypass.CreateSystemProxySettings(includeLoopback: true);
        Assert.IsTrue(withLoopback.ProxyLoopback);
        CollectionAssert.IsSubsetOf(MitmBypass.SystemProxyBypassRules, withLoopback.BypassRules.ToList());

        var without = MitmBypass.CreateSystemProxySettings(includeLoopback: false);
        Assert.IsFalse(without.ProxyLoopback);
        Assert.IsTrue(without.BypassRules.Contains("login.live.com"));
    }

    [TestMethod]
    public void ShouldDisableSslDecrypt_BypassHostsAndPinning()
    {
        Assert.IsFalse(MitmBypass.ShouldDisableSslDecrypt(null));
        Assert.IsFalse(MitmBypass.ShouldDisableSslDecrypt(""));
        Assert.IsFalse(MitmBypass.ShouldDisableSslDecrypt("example.com"));

        Assert.IsTrue(MitmBypass.ShouldDisableSslDecrypt("login.live.com"));
        Assert.IsTrue(MitmBypass.ShouldDisableSslDecrypt("LOGIN.WINDOWS.NET"));
        Assert.IsTrue(MitmBypass.ShouldDisableSslDecrypt("login.microsoftonline.com"));
        Assert.IsTrue(MitmBypass.ShouldDisableSslDecrypt("microsoftonline.com"));
        Assert.IsTrue(MitmBypass.ShouldDisableSslDecrypt("content.dropbox.com"));
        Assert.IsTrue(MitmBypass.ShouldDisableSslDecrypt("meet.webex.com"));
    }

    [TestMethod]
    public void HostnameMatches_WildcardAndExact()
    {
        Assert.IsTrue(MitmBypass.HostnameMatches("a.example.com", "*.example.com"));
        Assert.IsTrue(MitmBypass.HostnameMatches("example.com", "*.example.com"));
        Assert.IsTrue(MitmBypass.HostnameMatches("login.live.com", "login.live.com"));
        Assert.IsFalse(MitmBypass.HostnameMatches("other.com", "*.example.com"));
    }
}
