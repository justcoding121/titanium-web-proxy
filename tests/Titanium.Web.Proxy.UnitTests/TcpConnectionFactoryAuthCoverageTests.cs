using System;
using System.Collections.Generic;
using System.Reflection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Exceptions;
using Titanium.Web.Proxy.Helpers;
using Titanium.Web.Proxy.Http;
using Titanium.Web.Proxy.Models;
using Titanium.Web.Proxy.Network.Tcp;

namespace Titanium.Web.Proxy.UnitTests;

[TestClass]
public class TcpConnectionFactoryAuthCoverageTests
{
    private static readonly BindingFlags PrivateStatic =
        BindingFlags.Static | BindingFlags.NonPublic;

    private static bool TryGetChallenge(HeaderCollection headers, out string? scheme, out string? challenge)
    {
        var method = typeof(TcpConnectionFactory).GetMethod("TryGetUpstreamProxyAuthenticationChallenge",
            PrivateStatic)!;
        var args = new object?[] { headers, null, null };
        var ok = (bool)method.Invoke(null, args)!;
        scheme = (string?)args[1];
        challenge = (string?)args[2];
        return ok;
    }

    [TestMethod]
    public void TryGetUpstreamProxyAuthenticationChallenge_PrefersNegotiate()
    {
        var headers = new HeaderCollection();
        headers.AddHeader(KnownHeaders.ProxyAuthenticate, "NTLM TlRMTVNT");
        headers.AddHeader(KnownHeaders.ProxyAuthenticate, "Negotiate YII");
        Assert.IsTrue(TryGetChallenge(headers, out var scheme, out var challenge));
        Assert.AreEqual("Negotiate", scheme);
        Assert.AreEqual("YII", challenge);
    }

    [TestMethod]
    public void TryGetUpstreamProxyAuthenticationChallenge_SchemeOnly_AndIgnoresBasic()
    {
        var headers = new HeaderCollection();
        headers.AddHeader(KnownHeaders.ProxyAuthenticate, "Basic realm=x");
        headers.AddHeader(KnownHeaders.ProxyAuthenticate, "NTLM");
        Assert.IsTrue(TryGetChallenge(headers, out var scheme, out var challenge));
        Assert.AreEqual("NTLM", scheme);
        Assert.IsNull(challenge);
    }

    [TestMethod]
    public void TryGetUpstreamProxyAuthenticationChallenge_NoHeader_ReturnsFalse()
    {
        Assert.IsFalse(TryGetChallenge(new HeaderCollection(), out _, out _));
    }

    [TestMethod]
    public void TryGetUpstreamProxyAuthenticationChallenge_RejectsSchemePrefixWithoutSpace()
    {
        var headers = new HeaderCollection();
        headers.AddHeader(KnownHeaders.ProxyAuthenticate, "NTLMxyz");
        Assert.IsFalse(TryGetChallenge(headers, out _, out _));
    }

    [TestMethod]
    public void CreateUpstreamProxyConnectException_CapturesSnapshotAndOverride()
    {
        var method = typeof(TcpConnectionFactory).GetMethod("CreateUpstreamProxyConnectException",
            PrivateStatic)!;
        var headers = new HeaderCollection();
        headers.AddHeader("Proxy-Authenticate", "NTLM");
        headers.AddHeader("Server", "squid");
        var status = new ResponseStatusInfo
        {
            StatusCode = 407,
            Description = "Proxy Auth Required",
            Version = HttpHeader.Version11
        };

        var ex = (UpstreamProxyConnectException)method.Invoke(null,
            [status, headers, "body-preview", "custom fail"])!;

        Assert.AreEqual(407, ex.StatusCode);
        Assert.AreEqual("Proxy Auth Required", ex.StatusDescription);
        Assert.AreEqual("custom fail", ex.Message);
        Assert.AreEqual("body-preview", ex.BodyPreview);
        Assert.AreEqual("NTLM", ex.Headers["Proxy-Authenticate"]);
        Assert.AreEqual("squid", ex.Headers["Server"]);

        var defaultEx = (UpstreamProxyConnectException)method.Invoke(null,
            [status, headers, null, null])!;
        StringAssert.Contains(defaultEx.Message, "407");
        Assert.IsNull(defaultEx.BodyPreview);
    }
}
