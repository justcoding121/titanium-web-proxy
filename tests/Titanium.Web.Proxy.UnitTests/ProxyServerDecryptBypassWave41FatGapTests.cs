#pragma warning disable TWP001
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Titanium.Web.Proxy.UnitTests;

/// <summary>
///     Fat <see cref="ProxyServer" /> suite: decrypt-failure bypass learn/evict APIs.
/// </summary>
[TestClass]
public class ProxyServerDecryptBypassWave41FatGapTests
{
    [TestMethod]
    public void ForceDecryptBypass_LearnedHost_IsHonoredUntilRemoved()
    {
        using var proxy = new ProxyServer(false, false, false)
        {
            EnableDecryptFailureBypass = true,
            DecryptFailureBypassThreshold = 1,
        };
        const string host = "learned.wave41.test";
        Assert.IsFalse(proxy.ShouldBypassDecryptForLearnedHost(host));
        proxy.ForceDecryptFailureBypass(host);
        Assert.IsTrue(proxy.ShouldBypassDecryptForLearnedHost(host));
        Assert.IsTrue(proxy.RemoveDecryptFailureBypass(host));
        Assert.IsFalse(proxy.ShouldBypassDecryptForLearnedHost(host));
    }

    [TestMethod]
    public void DecryptBypass_Disabled_IgnoresForce()
    {
        using var proxy = new ProxyServer(false, false, false)
        {
            EnableDecryptFailureBypass = false,
        };
        proxy.ForceDecryptFailureBypass("ignored.wave41.test");
        Assert.IsFalse(proxy.ShouldBypassDecryptForLearnedHost("ignored.wave41.test"));
    }
}
#pragma warning restore TWP001
