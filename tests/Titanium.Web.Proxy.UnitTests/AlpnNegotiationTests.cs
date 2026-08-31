using System;
using System.ComponentModel;
using System.Security.Authentication;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Network.Tcp;

namespace Titanium.Web.Proxy.UnitTests;

[TestClass]
public class AlpnNegotiationTests
{
    [TestMethod]
    public void IsAlpnNegotiationFailure_Win32SecENoApplicationProtocol_True()
    {
        var inner = new Win32Exception(AlpnNegotiation.SecENoApplicationProtocol);
        var ex = new AuthenticationException("Authentication failed, see inner exception.", inner);
        Assert.IsTrue(AlpnNegotiation.IsAlpnNegotiationFailure(ex));
        Assert.IsFalse(AlpnNegotiation.ShouldAttemptTlsVersionDowngrade(ex));
    }

    [TestMethod]
    public void IsAlpnNegotiationFailure_MessageOnly_True()
    {
        var ex = new AuthenticationException(
            "No common application protocol exists between the client and the server. Application protocol negotiation failed.");
        Assert.IsTrue(AlpnNegotiation.IsAlpnNegotiationFailure(ex));
    }

    [TestMethod]
    public void IsAlpnNegotiationFailure_PlainAuthException_False()
    {
        var ex = new AuthenticationException("The remote certificate is invalid according to the validation procedure.");
        Assert.IsFalse(AlpnNegotiation.IsAlpnNegotiationFailure(ex));
        Assert.IsTrue(AlpnNegotiation.ShouldAttemptTlsVersionDowngrade(ex));
    }

    [TestMethod]
    public void IsAlpnNegotiationFailure_IoException_False()
    {
        var ex = new System.IO.IOException("Unable to read data from the transport connection.");
        Assert.IsFalse(AlpnNegotiation.IsAlpnNegotiationFailure(ex));
    }

    [TestMethod]
    public void IsAlpnNegotiationFailure_AggregateWrappingWin32_True()
    {
        var inner = new Win32Exception(AlpnNegotiation.SecENoApplicationProtocol);
        var agg = new AggregateException(new AuthenticationException("fail", inner));
        Assert.IsTrue(AlpnNegotiation.IsAlpnNegotiationFailure(agg));
    }

    [TestMethod]
    public void IsAlpnNegotiationFailure_Null_False()
    {
        Assert.IsFalse(AlpnNegotiation.IsAlpnNegotiationFailure(null));
    }
}
