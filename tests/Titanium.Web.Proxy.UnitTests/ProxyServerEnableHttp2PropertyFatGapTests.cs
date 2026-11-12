#pragma warning disable TWP001
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy;

namespace Titanium.Web.Proxy.UnitTests;

/// <summary>
///     Fat <see cref="ProxyServer" /> suite: EnableHttp2 round-trip.
/// </summary>
[TestClass]
public class ProxyServerEnableHttp2PropertyFatGapTests
{
    /// <summary>
    ///     Bug class: EnableHttp2 cannot be cleared after construction (stuck on).
    /// </summary>
    [TestMethod]
    public void EnableHttp2_CanDisableAfterDefault()
    {
        using var proxy = new ProxyServer();
        Assert.IsTrue(proxy.EnableHttp2);
        proxy.EnableHttp2 = false;
        Assert.IsFalse(proxy.EnableHttp2);
        proxy.EnableHttp2 = true;
        Assert.IsTrue(proxy.EnableHttp2);
    }
}
#pragma warning restore TWP001
