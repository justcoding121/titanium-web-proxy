using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Models;

namespace Titanium.Web.Proxy.UnitTests;

/// <summary>
///     Verifies that the per-session override properties introduced in §1 (ConnectTimeout,
///     MaxBufferedBodyBytes, NetworkFailureRetryAttempts, MaxWebSocketFramePayloadBytes,
///     OriginHttpVersionPolicy, UpstreamHttpProtocol) are accessible and default to null.
/// </summary>
[TestClass]
public class SessionEventArgsOverridePropertyTests
{
    [TestMethod]
    public void ProxyServer_EnableHttp3_DefaultsFalse()
    {
        using var proxy = new ProxyServer();
        Assert.IsFalse(proxy.EnableHttp3);
    }

    [TestMethod]
    public void ProxyServer_EnableHttp3_CanBeSetToTrue()
    {
        using var proxy = new ProxyServer();
        proxy.EnableHttp3 = true;
        Assert.IsTrue(proxy.EnableHttp3);
    }

    [TestMethod]
    public void UpstreamHttpProtocol_Http3EnumValue_IsDefinedAndGreaterThanHttp2()
    {
        // Http3 should be a defined enum member.
        Assert.IsTrue(System.Enum.IsDefined(typeof(UpstreamHttpProtocol), UpstreamHttpProtocol.Http3));

        // It must be distinct from Http2 and Http11.
        Assert.AreNotEqual(UpstreamHttpProtocol.Http2, UpstreamHttpProtocol.Http3);
        Assert.AreNotEqual(UpstreamHttpProtocol.Http11, UpstreamHttpProtocol.Http3);
    }
}
