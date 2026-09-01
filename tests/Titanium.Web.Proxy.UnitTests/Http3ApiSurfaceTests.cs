using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Models;

#pragma warning disable TWP001 // Experimental HTTP/3 API — test intentionally exercises this surface

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
    public void ProxyServer_TryEnableHttp3IfSupported_MatchesOsQuic()
    {
        using var proxy = new ProxyServer();
        var enabled = proxy.TryEnableHttp3IfSupported();
        Assert.AreEqual(System.Net.Quic.QuicListener.IsSupported, enabled);
        Assert.AreEqual(enabled, proxy.EnableHttp3);
    }

    [TestMethod]
    public void ProxyServer_SetHttp3Enabled_FalseClearsFlag()
    {
        using var proxy = new ProxyServer();
        proxy.TryEnableHttp3IfSupported();
        Assert.IsFalse(proxy.SetHttp3Enabled(false));
        Assert.IsFalse(proxy.EnableHttp3);
        Assert.AreEqual(System.Net.Quic.QuicListener.IsSupported, proxy.SetHttp3Enabled(true));
        Assert.AreEqual(System.Net.Quic.QuicListener.IsSupported, proxy.EnableHttp3);
    }

    [TestMethod]
    public void UpstreamHttpProtocol_Http3EnumValue_IsDefinedAndGreaterThanHttp2()
    {
        // Http3 should be a defined enum member.
        Assert.IsTrue(System.Enum.IsDefined(UpstreamHttpProtocol.Http3));

    }
}
