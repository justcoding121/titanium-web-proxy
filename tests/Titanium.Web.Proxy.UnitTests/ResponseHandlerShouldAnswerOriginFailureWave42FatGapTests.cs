#pragma warning disable TWP001
using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.EventArguments;
using Titanium.Web.Proxy.Exceptions;
using Titanium.Web.Proxy.Helpers;
using Titanium.Web.Proxy.Http;
using Titanium.Web.Proxy.Models;
using Titanium.Web.Proxy.Network.Tcp;

namespace Titanium.Web.Proxy.UnitTests;

/// <summary>
///     Fat <see cref="ProxyServer.ShouldAnswerOriginFailure" /> gate before synthetic 502/504.
/// </summary>
[TestClass]
public class ResponseHandlerShouldAnswerOriginFailureWave42FatGapTests
{
    [TestMethod]
    public void ShouldAnswerOriginFailure_FalseWhenCommittedOrLocked()
    {
        using var proxy = new ProxyServer(false, false, false);
        using var session = Wave42FatGapTestSupport.MakeSession(proxy);
        session.IsClientResponseCommitted = true;
        Assert.IsFalse(ProxyServer.ShouldAnswerOriginFailure(session,
            new IOException("io"), session.CancellationTokenSource));
        session.IsClientResponseCommitted = false;
        session.HttpClient.Response.Locked = true;
        Assert.IsFalse(ProxyServer.ShouldAnswerOriginFailure(session,
            new SocketException(), session.CancellationTokenSource));
    }

    [TestMethod]
    public void ShouldAnswerOriginFailure_FalseWhenCancelled()
    {
        using var proxy = new ProxyServer(false, false, false);
        using var session = Wave42FatGapTestSupport.MakeSession(proxy);
        session.CancellationTokenSource.Cancel();
        Assert.IsFalse(ProxyServer.ShouldAnswerOriginFailure(session,
            new IOException("io"), session.CancellationTokenSource));
    }

    [TestMethod]
    public void ShouldAnswerOriginFailure_TrueForTransportFailures()
    {
        using var proxy = new ProxyServer(false, false, false);
        using var session = Wave42FatGapTestSupport.MakeSession(proxy);
        Assert.IsTrue(ProxyServer.ShouldAnswerOriginFailure(session,
            new SocketException((int)SocketError.ConnectionRefused), session.CancellationTokenSource));
        Assert.IsTrue(ProxyServer.ShouldAnswerOriginFailure(session,
            new RetryableServerConnectionException("retry"), session.CancellationTokenSource));
        Assert.IsTrue(ProxyServer.ShouldAnswerOriginFailure(session,
            new UpstreamCapacityException("cap", 8), session.CancellationTokenSource));
        Assert.IsTrue(ProxyServer.ShouldAnswerOriginFailure(session,
            new AuthenticationException("tls"), session.CancellationTokenSource));
        Assert.IsTrue(ProxyServer.ShouldAnswerOriginFailure(session,
            new IOException("reset"), session.CancellationTokenSource));
        Assert.IsFalse(ProxyServer.ShouldAnswerOriginFailure(session,
            new InvalidOperationException("logic"), session.CancellationTokenSource));
    }

}
#pragma warning restore TWP001
