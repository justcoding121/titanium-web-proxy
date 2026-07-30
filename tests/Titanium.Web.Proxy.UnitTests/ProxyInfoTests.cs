using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Helpers;
using Titanium.Web.Proxy.Models;

namespace Titanium.Web.Proxy.UnitTests;

/// <summary>
///     Phase E.15: <c>ProxyInfo</c> parses the WinINet/registry <c>ProxyServer</c> value, which can mix
///     per-protocol <c>protocol=host:port</c> entries with a bare global <c>host:port</c> entry, and
///     which - being an IP-literal authority - needs bracketed-IPv6 support rather than a naive
///     <c>Split(':')</c>.
/// </summary>
[TestClass]
public class ProxyInfoTests
{
    [TestMethod]
    public void GetSystemProxyValues_PerProtocolEntries_ParsedIndependently()
    {
        var values = ProxyInfo.GetSystemProxyValues("http=127.0.0.1:8888;https=127.0.0.1:8443");

        Assert.AreEqual(2, values.Count);
        AssertContains(values, ProxyProtocolType.Http, "127.0.0.1", 8888);
        AssertContains(values, ProxyProtocolType.Https, "127.0.0.1", 8443);
    }

    [TestMethod]
    public void GetSystemProxyValues_BareGlobalHostPort_AppliesToHttpAndHttps()
    {
        // The common case: a user sets one proxy via the basic Settings UI without expanding
        // "Advanced"/per-protocol configuration. Windows stores this as a single bare entry.
        var values = ProxyInfo.GetSystemProxyValues("127.0.0.1:8888");

        Assert.AreEqual(2, values.Count);
        AssertContains(values, ProxyProtocolType.Http, "127.0.0.1", 8888);
        AssertContains(values, ProxyProtocolType.Https, "127.0.0.1", 8888);
    }

    [TestMethod]
    public void GetSystemProxyValues_BracketedIPv6PerProtocolEntry_ParsesHostWithoutBrackets()
    {
        var values = ProxyInfo.GetSystemProxyValues("http=[::1]:8888");

        Assert.AreEqual(1, values.Count);
        AssertContains(values, ProxyProtocolType.Http, "::1", 8888);
    }

    [TestMethod]
    public void GetSystemProxyValues_BracketedIPv6BareGlobalEntry_ParsesHostWithoutBrackets()
    {
        var values = ProxyInfo.GetSystemProxyValues("[2001:db8::1]:8080");

        Assert.AreEqual(2, values.Count);
        AssertContains(values, ProxyProtocolType.Http, "2001:db8::1", 8080);
        AssertContains(values, ProxyProtocolType.Https, "2001:db8::1", 8080);
    }

    [TestMethod]
    public void GetSystemProxyValues_MixedPerProtocolAndGlobalEntries_BothResolve()
    {
        var values = ProxyInfo.GetSystemProxyValues("https=127.0.0.1:9443;10.0.0.1:9080");

        Assert.AreEqual(3, values.Count);
        AssertContains(values, ProxyProtocolType.Https, "127.0.0.1", 9443);
        AssertContains(values, ProxyProtocolType.Http, "10.0.0.1", 9080);
        AssertContains(values, ProxyProtocolType.Https, "10.0.0.1", 9080);
    }

    [TestMethod]
    public void GetSystemProxyValues_EntryMissingPort_IsSkippedRatherThanThrowing()
    {
        // A bare hostname/port pair with no port is not a usable proxy entry and previously would
        // have thrown (IndexOutOfRangeException reading endPointParts[1], or FormatException from
        // int.Parse). A malformed entry must not take down resolution of the rest of the list.
        var values = ProxyInfo.GetSystemProxyValues("http=onlyhost;https=127.0.0.1:8443");

        Assert.AreEqual(1, values.Count);
        AssertContains(values, ProxyProtocolType.Https, "127.0.0.1", 8443);
    }

    [TestMethod]
    public void GetSystemProxyValues_UnbracketedAmbiguousIPv6Entry_IsSkippedRatherThanMisparsed()
    {
        var values = ProxyInfo.GetSystemProxyValues("http=::1:8888");

        Assert.AreEqual(0, values.Count);
    }

    [TestMethod]
    public void GetSystemProxyValues_UnrecognizedProtocolPrefix_IsSkipped()
    {
        var values = ProxyInfo.GetSystemProxyValues("ftp=127.0.0.1:2121;http=127.0.0.1:8888");

        Assert.AreEqual(1, values.Count);
        AssertContains(values, ProxyProtocolType.Http, "127.0.0.1", 8888);
    }

    [TestMethod]
    public void GetSystemProxyValues_EmptyOrWhitespace_ReturnsEmpty()
    {
        Assert.AreEqual(0, ProxyInfo.GetSystemProxyValues(null).Count);
        Assert.AreEqual(0, ProxyInfo.GetSystemProxyValues("").Count);
        Assert.AreEqual(0, ProxyInfo.GetSystemProxyValues("   ").Count);
    }

    private static void AssertContains(System.Collections.Generic.List<HttpSystemProxyValue> values,
        ProxyProtocolType protocolType, string host, int port)
    {
        Assert.IsTrue(
            values.Any(v => v.ProtocolType == protocolType && v.HostName == host && v.Port == port),
            $"Expected an entry for {protocolType} {host}:{port} in [{string.Join(", ", values.Select(v => v.ToString()))}]");
    }
}
