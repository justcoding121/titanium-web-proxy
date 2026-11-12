#pragma warning disable TWP001
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy;

namespace Titanium.Web.Proxy.UnitTests;

/// <summary>
///     Fat <see cref="SystemProxySettings" /> edges used by <c>ProxyServer.SystemProxy</c> when composing OS bypass lists.
/// </summary>
[TestClass]
public class ProxyServerSystemProxyBypassRulesFatGapTests
{
    /// <summary>
    ///     Bug class: <c>&lt;-loopback&gt;</c> is omitted when <see cref="SystemProxySettings.ManageLoopbackRule" /> is on.
    /// </summary>
    [TestMethod]
    public void BuildProxyOverride_LoopbackManaged_PrependsSubtractLoopback()
    {
        var settings = new SystemProxySettings
        {
            ProxyLoopback = true,
            ManageLoopbackRule = true,
            ProxyLoopbackPlacement = SystemProxyLoopbackPlacement.First,
        };
        settings.BypassRules.Add("*.local");
        var merged = settings.BuildProxyOverride("*.local");
        StringAssert.StartsWith(merged, "<-loopback>");
        StringAssert.Contains(merged, "*.local");
    }

    /// <summary>
    ///     Bug class: semicolon inside a single bypass rule is accepted and corrupts WinINET parsing.
    /// </summary>
    [TestMethod]
    public void Validate_SemicolonInRule_Throws()
    {
        var settings = new SystemProxySettings();
        settings.BypassRules.Add("bad;rule");
        try
        {
            settings.Validate();
            Assert.Fail("Expected ArgumentException for semicolon in bypass rule.");
        }
        catch (System.ArgumentException) { }
    }
}
#pragma warning restore TWP001
