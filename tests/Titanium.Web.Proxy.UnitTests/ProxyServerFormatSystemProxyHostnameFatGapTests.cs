#pragma warning disable TWP001
using System.Net;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy;

namespace Titanium.Web.Proxy.UnitTests;

/// <summary>
///     Fat <see cref="ProxyServer.FormatSystemProxyHostname" /> suite: loopback addresses written into
///     OS proxy settings must not resolve unpredictably on dual-stack hosts.
/// </summary>
[TestClass]
public class ProxyServerFormatSystemProxyHostnameFatGapTests
{
    /// <summary>
    ///     Bug class: IPv4 loopback listeners are published as <c>localhost</c> and clients prefer <c>::1</c>.
    /// </summary>
    [TestMethod]
    public void FormatSystemProxyHostname_Ipv4Loopback_Is127()
    {
        Assert.AreEqual("127.0.0.1", ProxyServer.FormatSystemProxyHostname(IPAddress.Loopback));
        Assert.AreEqual("127.0.0.1", ProxyServer.FormatSystemProxyHostname(IPAddress.Any));
    }

    /// <summary>
    ///     Bug class: IPv6 loopback listeners are published as <c>localhost</c> instead of <c>::1</c>.
    /// </summary>
    [TestMethod]
    public void FormatSystemProxyHostname_Ipv6Loopback_IsBracketlessV6()
    {
        Assert.AreEqual("::1", ProxyServer.FormatSystemProxyHostname(IPAddress.IPv6Loopback));
        Assert.AreEqual("::1", ProxyServer.FormatSystemProxyHostname(IPAddress.IPv6Any));
    }
}
#pragma warning restore TWP001
