#pragma warning disable TWP001
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy;

namespace Titanium.Web.Proxy.UnitTests;

/// <summary>
///     Fat <see cref="ProxyServer" /> suite: EnableHttp3 round-trip.
/// </summary>
[TestClass]
public class ProxyServerEnableHttp3PropertyFatGapTests
{
    /// <summary>
    ///     Bug class: EnableHttp3 cannot be turned off after construction.
    /// </summary>
    [TestMethod]
    public void EnableHttp3_RoundTrip_AfterDisable()
    {
        using var proxy = new ProxyServer { EnableHttp3 = true };
        Assert.IsTrue(proxy.EnableHttp3);
        proxy.EnableHttp3 = false;
        Assert.IsFalse(proxy.EnableHttp3);
    }
}
#pragma warning restore TWP001
