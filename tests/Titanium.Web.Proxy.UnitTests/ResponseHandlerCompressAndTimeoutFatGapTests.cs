#pragma warning disable TWP001
using System;
using System.IO;
using System.Net;
using System.Net.Security;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Abstractions.Routing;
using Titanium.Web.Proxy.Compression;
using Titanium.Web.Proxy.EventArguments;
using Titanium.Web.Proxy.Helpers;
using Titanium.Web.Proxy.Http;
using Titanium.Web.Proxy.Exceptions;
using Titanium.Web.Proxy.Logging;
using Titanium.Web.Proxy.Models;
using Titanium.Web.Proxy.Network;
using Titanium.Web.Proxy.Network.Quic;
using Titanium.Web.Proxy.Network.Tcp;

namespace Titanium.Web.Proxy.UnitTests;

/// <summary>
///     Fat <see cref="ResponseHandler" /> helpers: buffered gzip compression, client HTTP/2 detection,
///     and timeout limit-id mapping.
/// </summary>
[TestClass]
public class ResponseHandlerCompressAndTimeoutFatGapTests
{
    private const BindingFlags PrivateStatic = BindingFlags.NonPublic | BindingFlags.Static;
    private static readonly BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;

    private static SessionEventArgs MakeSession(ProxyServer proxy, bool http2Client = false)
    {
        var ep = new ExplicitProxyEndPoint(IPAddress.Loopback, 0, false);
        var connection = new QuicClientConnection(
            proxy, new IPEndPoint(IPAddress.Loopback, 4433), new IPEndPoint(IPAddress.Loopback, 12345));
        if (http2Client)
            connection.NegotiatedApplicationProtocol = SslApplicationProtocol.Http2;
        var cts = new CancellationTokenSource();
        var clientStream = new HttpClientStream(proxy, connection, Stream.Null, proxy.BufferPool, cts.Token);
        var session = new SessionEventArgs(proxy, ep, clientStream, null, cts);
        session.HttpClient.Request.HttpVersion = HttpHeader.Version11;
        session.HttpClient.Request.Method = "GET";
        session.HttpClient.Request.Headers.AddHeader("Accept-Encoding", "gzip");
        return session;
    }

    [TestMethod]
    public void ResponseCompressionPolicy_TrySelect_EligiblePlainText200()
    {
        var policy = ResponseCompressionPolicy.Create(new ResponseCompressionConfig
        {
            Enabled = true,
            MinBytes = 64,
            MaxBytes = 64 * 1024,
            MimeTypes = ["text/plain"],
        })!;
        using var proxy = new ProxyServer(false, false, false);
        using var session = MakeSession(proxy);
        var body = Encoding.UTF8.GetBytes(new string('x', 2048));
        session.HttpClient.Response.HttpVersion = HttpHeader.Version11;
        session.HttpClient.Response.StatusCode = 200;
        session.HttpClient.Response.Headers.AddHeader("Content-Type", "text/plain");
        session.HttpClient.Response.ContentLength = body.Length;
        session.HttpClient.Response.Body = body;
        session.HttpClient.Response.IsBodyRead = true;
        Assert.IsTrue(policy.TrySelect(session.HttpClient.Request, session.HttpClient.Response, out var algorithm));
        Assert.AreNotEqual(HttpCompression.Unsupported, algorithm);
    }

    [TestMethod]
    public async Task TryCompressResponseAsync_Http2Client_SkipsCompression()
    {
        using var proxy = new ProxyServer(false, false, false);
        var policy = ResponseCompressionPolicy.Create(new ResponseCompressionConfig { Enabled = true })!;
        using var session = MakeSession(proxy, http2Client: true);
        var body = Encoding.UTF8.GetBytes(new string('y', 512));
        session.HttpClient.Response.HttpVersion = HttpHeader.Version11;
        session.HttpClient.Response.StatusCode = 200;
        session.HttpClient.Response.Headers.AddHeader("Content-Type", "text/plain");
        session.HttpClient.Response.ContentLength = body.Length;
        session.HttpClient.Response.Body = body;
        session.HttpClient.Response.IsBodyRead = true;

        var method = typeof(ProxyServer).GetMethod("TryCompressResponseAsync", PrivateStatic)!;
        await (Task)method.Invoke(null, [session, policy])!;

        Assert.IsNull(session.HttpClient.Response.ContentEncoding);
        Assert.AreEqual(body.Length, session.HttpClient.Response.ContentLength);
    }

    [TestMethod]
    public void ClientSpokeHttp2_NegotiatedHttp2_IsTrue()
    {
        using var proxy = new ProxyServer(false, false, false);
        using var session = MakeSession(proxy, http2Client: true);
        var method = typeof(ProxyServer).GetMethod("ClientSpokeHttp2", PrivateStatic)!;
        Assert.IsTrue((bool)method.Invoke(null, [session])!);
    }

    [TestMethod]
    public void ClientSpokeHttp2_H2CleartextUpgrade_IsTrue()
    {
        using var proxy = new ProxyServer(false, false, false);
        using var session = MakeSession(proxy);
        session.ClientConnection.Http2CleartextClient = true;
        var method = typeof(ProxyServer).GetMethod("ClientSpokeHttp2", PrivateStatic)!;
        Assert.IsTrue((bool)method.Invoke(null, [session])!);
    }

    [TestMethod]
    public void TimeoutLimitId_MapsEveryKind()
    {
        var method = typeof(ProxyServer).GetMethod("TimeoutLimitId", PrivateStatic)!;
        Assert.AreEqual(LimitId.TimeoutConnect,
            (LimitId)method.Invoke(null, [ProxyTimeoutKind.Connect])!);
        Assert.AreEqual(LimitId.TimeoutResponseHeader,
            (LimitId)method.Invoke(null, [ProxyTimeoutKind.ResponseHeader])!);
        Assert.AreEqual(LimitId.TimeoutIdleRead,
            (LimitId)method.Invoke(null, [ProxyTimeoutKind.IdleRead])!);
        Assert.AreEqual(LimitId.TimeoutIdleWrite,
            (LimitId)method.Invoke(null, [ProxyTimeoutKind.IdleWrite])!);
        Assert.AreEqual(LimitId.TimeoutRequest,
            (LimitId)method.Invoke(null, [ProxyTimeoutKind.Request])!);
        Assert.AreEqual(LimitId.TimeoutClientHeader,
            (LimitId)method.Invoke(null, [ProxyTimeoutKind.ClientHeader])!);
        Assert.AreEqual(LimitId.TimeoutRequest,
            (LimitId)method.Invoke(null, [(ProxyTimeoutKind)999])!);
    }

    [TestMethod]
    public void FillLegacyHttpResponseCacheAsync_NoFiller_IsNoOp()
    {
        using var proxy = new ProxyServer(false, false, false);
        Assert.IsNull(proxy.HttpResponseCacheFiller);
        using var session = MakeSession(proxy);
        var fill = typeof(ProxyServer).GetMethod("FillLegacyHttpResponseCacheAsync", PrivateInstance)!;
        var task = (Task)fill.Invoke(proxy, [session, CancellationToken.None])!;
        task.GetAwaiter().GetResult();
        Assert.IsNull(session.ResponseCache);
    }
}
#pragma warning restore TWP001
