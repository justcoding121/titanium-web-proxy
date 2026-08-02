#pragma warning disable TWP001 // Experimental H3 API — intentional in tests

using System;
using System.Net;
using System.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.EventArguments;
using Titanium.Web.Proxy.Helpers;
using Titanium.Web.Proxy.Http3;
using Titanium.Web.Proxy.Models;
using Titanium.Web.Proxy.Network.Tcp;

namespace Titanium.Web.Proxy.UnitTests;

/// <summary>
///     Unit coverage for small HTTP/3 model types and transparent QUIC endpoint defaults.
/// </summary>
[TestClass]
public class Http3ModelAndEndpointTests
{
    [TestMethod]
    public void Http3ConnectionException_StoresErrorCode()
    {
        var ex = new Http3ConnectionException(Http3ErrorCode.MissingSettings, "no settings");
        Assert.AreEqual(Http3ErrorCode.MissingSettings, ex.ErrorCode);
        Assert.AreEqual("no settings", ex.Message);
    }

    [TestMethod]
    public void Http3StreamException_StoresErrorCode()
    {
        var ex = new Http3StreamException(Http3ErrorCode.FrameUnexpected, "bad frame");
        Assert.AreEqual(Http3ErrorCode.FrameUnexpected, ex.ErrorCode);
        Assert.AreEqual("bad frame", ex.Message);
    }

    [TestMethod]
    public void Http3StreamState_IsClosed_RequiresBothHalves()
    {
        using var proxy = new ProxyServer(false, false, false);
        var endPoint = new TransparentQuicProxyEndPoint(IPAddress.Loopback, 0);
        using var connection = new QuicClientConnection(
            proxy, new IPEndPoint(IPAddress.Loopback, 4433), new IPEndPoint(IPAddress.Loopback, 12345));
        using var cts = new CancellationTokenSource();
        var clientStream = new HttpClientStream(proxy, connection, System.IO.Stream.Null, proxy.BufferPool, cts.Token);
        var session = new SessionEventArgs(proxy, endPoint, clientStream, null, cts);

        var state = new Http3StreamState(streamId: 0, session, cts);

        Assert.IsFalse(state.IsClosed);
        state.RequestClosed = true;
        Assert.IsFalse(state.IsClosed);
        state.ResponseClosed = true;
        Assert.IsTrue(state.IsClosed);
        Assert.AreEqual(0L, state.StreamId);
        Assert.AreSame(session, state.SessionArgs);
        Assert.AreSame(cts, state.Cancellation);
        Assert.AreEqual(0, state.FinalizedFlag);
    }

    [TestMethod]
    public void TransparentQuicProxyEndPoint_Defaults_AreSane()
    {
        var ep = new TransparentQuicProxyEndPoint(IPAddress.Loopback, 4433);

        Assert.AreEqual("localhost", ep.GenericCertificateName);
        Assert.AreEqual(100, ep.MaxInboundBidirectionalStreams);
        Assert.AreEqual(3, ep.MaxInboundUnidirectionalStreams);
        Assert.AreEqual(TimeSpan.FromSeconds(30), ep.HandshakeTimeout);
        Assert.AreEqual(TimeSpan.FromSeconds(60), ep.IdleTimeout);
        Assert.IsFalse(ep.AdvertiseToHttpClients);
        Assert.IsNull(ep.OriginalDestinationResolver);
    }

    [TestMethod]
    public void TransparentQuicProxyEndPoint_UnidirectionalStreams_ClampedToAtLeastThree()
    {
        var ep = new TransparentQuicProxyEndPoint(0);
        ep.MaxInboundUnidirectionalStreams = 1;
        Assert.AreEqual(3, ep.MaxInboundUnidirectionalStreams);

        ep.MaxInboundUnidirectionalStreams = 10;
        Assert.AreEqual(10, ep.MaxInboundUnidirectionalStreams);
    }

    [TestMethod]
    public void TransparentQuicProxyEndPoint_InvokeBeforeSslAuthenticate_IsNoOp()
    {
        using var proxy = new ProxyServer(false, false, false);
        var ep = new TransparentQuicProxyEndPoint(IPAddress.Loopback, 0);
        using var connection = new QuicClientConnection(
            proxy, new IPEndPoint(IPAddress.Loopback, 4433), new IPEndPoint(IPAddress.Loopback, 12345));
        using var cts = new CancellationTokenSource();
        var args = new BeforeSslAuthenticateEventArgs(proxy, connection, cts, "example.com");

        // Must complete immediately — QUIC uses BeforeQuicAuthenticate instead.
        Assert.IsTrue(ep.InvokeBeforeSslAuthenticate(proxy, args, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance)
            .IsCompletedSuccessfully);
    }

    [TestMethod]
    public void BeforeQuicAuthenticateEventArgs_DefaultsAndReject()
    {
        using var proxy = new ProxyServer(false, false, false);
        using var cts = new CancellationTokenSource();
        var remote = new IPEndPoint(IPAddress.Loopback, 12345);
        var local = new IPEndPoint(IPAddress.Loopback, 4433);

        var args = new BeforeQuicAuthenticateEventArgs(
            proxy, cts, "sni.example", "origin.example", 443, remote, local);

        Assert.AreEqual("sni.example", args.SniHostName);
        Assert.AreEqual("origin.example", args.OriginalDestinationHost);
        Assert.AreEqual(443, args.OriginalDestinationPort);
        Assert.AreEqual("origin.example", args.ForwardHost);
        Assert.AreEqual(443, args.ForwardPort);
        Assert.AreEqual(UpstreamHttpProtocol.Auto, args.UpstreamHttpProtocol);
        Assert.IsTrue(args.AllowHttpProtocolTranslation);
        Assert.IsNull(args.CustomUpStreamProxy);

        args.UpstreamHttpProtocol = UpstreamHttpProtocol.Http3;
        Assert.AreEqual(UpstreamHttpProtocol.Http3, args.UpstreamHttpProtocol);

        Assert.ThrowsException<ArgumentOutOfRangeException>(
            () => args.UpstreamHttpProtocol = (UpstreamHttpProtocol)999);

        args.Reject();
        Assert.IsTrue(cts.IsCancellationRequested);
    }
}
