using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.EventArguments;
using Titanium.Web.Proxy.Helpers;
using Titanium.Web.Proxy.Http;
using Titanium.Web.Proxy.Models;
using Titanium.Web.Proxy.Network.Tcp;
using Titanium.Web.Proxy.StreamExtended.BufferPool;

namespace Titanium.Web.Proxy.UnitTests;

/// <summary>
///     Coverage for ProxyServer WinAuth response rewrite / round-cap paths without SSPI prompts.
/// </summary>
[TestClass]
public class WinAuthHandlerFlowTests
{
    private static readonly BindingFlags PrivateInstance =
        BindingFlags.Instance | BindingFlags.NonPublic;

    private static readonly BindingFlags PrivateStatic =
        BindingFlags.Static | BindingFlags.NonPublic;

    private static SessionEventArgs MakeSession(ProxyServer proxy)
    {
        var endPoint = new ExplicitProxyEndPoint(IPAddress.Loopback, 0, false);
        var connection = new QuicClientConnection(
            proxy, new IPEndPoint(IPAddress.Loopback, 4433), new IPEndPoint(IPAddress.Loopback, 12345));
        var cts = new CancellationTokenSource();
        var clientStream = new HttpClientStream(proxy, connection, Stream.Null, proxy.BufferPool, cts.Token);
        return new SessionEventArgs(proxy, endPoint, clientStream, null, cts);
    }

    private static async Task InvokeRewriteUnauthorizedResponse(SessionEventArgs args)
    {
        var method = typeof(ProxyServer).GetMethod("RewriteUnauthorizedResponse", PrivateStatic)!;
        await (Task)method.Invoke(null, [args])!;
    }

    private static async Task InvokeHandle401(ProxyServer proxy, SessionEventArgs args)
    {
        var method = typeof(ProxyServer).GetMethod("Handle401UnAuthorized", PrivateInstance)!;
        await (Task)method.Invoke(proxy, [args])!;
    }

    private static async Task InvokeHandle407(ProxyServer proxy, SessionEventArgs args)
    {
        var method = typeof(ProxyServer).GetMethod("Handle407ProxyAuthorization", PrivateInstance)!;
        await (Task)method.Invoke(proxy, [args])!;
    }

    private static void PrepareResponseBody(SessionEventArgs session, string bodyHtml)
    {
        session.HttpClient.Request.Locked = true;
        session.HttpClient.Response.StatusCode = 401;
        session.HttpClient.Response.ContentType = "text/html; charset=utf-8";
        session.SetResponseBodyString(bodyHtml);
        session.HttpClient.Response.IsBodyRead = true;
    }

    [TestMethod]
    public async Task RewriteUnauthorizedResponse_InsertsIntoBodyTag()
    {
        using var proxy = new ProxyServer(false, false, false);
        using var session = MakeSession(proxy);
        PrepareResponseBody(session, "<html><body>orig</body></html>");
        session.HttpClient.Response.Headers.AddHeader("WWW-Authenticate", "NTLM");
        session.HttpClient.Response.Headers.AddHeader("Proxy-Authenticate", "Negotiate");

        await InvokeRewriteUnauthorizedResponse(session);

        var body = Encoding.UTF8.GetString(session.HttpClient.Response.Body);
        StringAssert.Contains(body, "inserted-by-proxy");
        StringAssert.Contains(body, "orig");
        StringAssert.Contains(body, "<body>");
        Assert.IsNull(session.HttpClient.Response.Headers.GetFirstHeader("WWW-Authenticate"));
        Assert.IsNull(session.HttpClient.Response.Headers.GetFirstHeader("Proxy-Authenticate"));
    }

    [TestMethod]
    public async Task RewriteUnauthorizedResponse_WithoutBodyTag_UsesFallbackDocument()
    {
        using var proxy = new ProxyServer(false, false, false);
        using var session = MakeSession(proxy);
        PrepareResponseBody(session, "plain failure");

        await InvokeRewriteUnauthorizedResponse(session);

        var body = Encoding.UTF8.GetString(session.HttpClient.Response.Body);
        StringAssert.Contains(body, "<!DOCTYPE html");
        StringAssert.Contains(body, "NTLM authentication through Titanium.Web.Proxy");
        Assert.IsFalse(body.Contains("plain failure", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task Handle401_RoundCap_RewritesAndDoesNotReRequest()
    {
        using var proxy = new ProxyServer(false, false, false);
        using var session = MakeSession(proxy);
        PrepareResponseBody(session, "<html><body>denied</body></html>");
        session.HttpClient.Response.Headers.AddHeader("WWW-Authenticate", "NTLM");
        session.HttpClient.Data["WinAuthRoundCount"] = 3;

        await InvokeHandle401(proxy, session);

        Assert.IsFalse(session.ReRequest);
        var body = Encoding.UTF8.GetString(session.HttpClient.Response.Body);
        StringAssert.Contains(body, "inserted-by-proxy");
        Assert.IsNull(session.HttpClient.Response.Headers.GetFirstHeader("WWW-Authenticate"));
    }

    [TestMethod]
    public async Task Handle401_NoAuthHeader_IsNoOp()
    {
        using var proxy = new ProxyServer(false, false, false);
        using var session = MakeSession(proxy);
        PrepareResponseBody(session, "<html><body>x</body></html>");

        await InvokeHandle401(proxy, session);

        Assert.IsFalse(session.ReRequest);
        Assert.AreEqual("<html><body>x</body></html>",
            Encoding.UTF8.GetString(session.HttpClient.Response.Body));
    }

    [TestMethod]
    public async Task Handle401_IisMisspelledWwwAuthenticate_IsRecognized()
    {
        using var proxy = new ProxyServer(false, false, false);
        using var session = MakeSession(proxy);
        PrepareResponseBody(session, "<html><body>x</body></html>");
        // IIS historically misspells WWW-Authenticate; authHeaderNames includes it.
        session.HttpClient.Response.Headers.AddHeader("WWWAuthenticate", "NTLM");
        session.HttpClient.Data["WinAuthRoundCount"] = 3;

        await InvokeHandle401(proxy, session);

        Assert.IsFalse(session.ReRequest);
        StringAssert.Contains(Encoding.UTF8.GetString(session.HttpClient.Response.Body), "inserted-by-proxy");
    }

    [TestMethod]
    public async Task Handle407_WithoutConnection_IsNoOp()
    {
        using var proxy = new ProxyServer(false, false, false);
        using var session = MakeSession(proxy);
        session.HttpClient.Response.StatusCode = 407;
        session.HttpClient.Response.Headers.AddHeader("Proxy-Authenticate", "NTLM");

        await InvokeHandle407(proxy, session);
        Assert.IsFalse(session.ReRequest);
    }

    [TestMethod]
    public async Task Handle407_EmptyToken_RewritesResponse()
    {
        using var proxy = new ProxyServer(false, false, false);
        proxy.UpstreamProxyWinAuthTokenGenerator = (_, _, _, _) => "";

        using var session = MakeSession(proxy);
        using var serverConn = await CreateServerConnectionAsync(proxy,
            new ExternalProxy { HostName = "up.proxy", Port = 8080, UseDefaultCredentials = true });
        session.HttpClient.SetConnection(serverConn);

        PrepareResponseBody(session, "<html><body>proxy auth</body></html>");
        session.HttpClient.Response.StatusCode = 407;
        session.HttpClient.Response.Headers.AddHeader("Proxy-Authenticate", "NTLM");

        await InvokeHandle407(proxy, session);

        Assert.IsFalse(session.ReRequest);
        StringAssert.Contains(Encoding.UTF8.GetString(session.HttpClient.Response.Body), "inserted-by-proxy");
    }

    [TestMethod]
    public async Task Handle407_RoundCap_RewritesResponse()
    {
        using var proxy = new ProxyServer(false, false, false);
        proxy.UpstreamProxyWinAuthTokenGenerator = (_, scheme, _, _) => $" {scheme}-tok";

        using var session = MakeSession(proxy);
        using var serverConn = await CreateServerConnectionAsync(proxy,
            new ExternalProxy { HostName = "up.proxy", Port = 8080, UseDefaultCredentials = true });
        session.HttpClient.SetConnection(serverConn);

        PrepareResponseBody(session, "<html><body>proxy auth</body></html>");
        session.HttpClient.Response.StatusCode = 407;
        session.HttpClient.Response.Headers.AddHeader("Proxy-Authenticate", "Negotiate");
        session.HttpClient.Data["WinAuthRoundCount"] = 3;

        await InvokeHandle407(proxy, session);

        Assert.IsFalse(session.ReRequest);
        StringAssert.Contains(Encoding.UTF8.GetString(session.HttpClient.Response.Body), "inserted-by-proxy");
    }

    [TestMethod]
    public async Task Handle407_GeneratorToken_SetsProxyAuthorizationAndReRequest()
    {
        using var proxy = new ProxyServer(false, false, false);
        proxy.UpstreamProxyWinAuthTokenGenerator = (_, scheme, challenge, _) =>
            challenge == null ? $" {scheme}-init" : $" {scheme}-final";

        using var session = MakeSession(proxy);
        using var serverConn = await CreateServerConnectionAsync(proxy,
            new ExternalProxy { HostName = "up.proxy", Port = 8080, UseDefaultCredentials = true });
        session.HttpClient.SetConnection(serverConn);

        session.HttpClient.Request.Url = "http://example.com/";
        session.HttpClient.Request.Method = "GET";
        session.HttpClient.Request.Locked = true;
        session.HttpClient.Response.StatusCode = 407;
        session.HttpClient.Response.IsBodyRead = true;
        session.HttpClient.Response.Body = Array.Empty<byte>();
        session.HttpClient.Response.Headers.AddHeader("Proxy-Authenticate", "NTLM");

        await InvokeHandle407(proxy, session);

        Assert.IsTrue(session.ReRequest);
        Assert.AreEqual("NTLM NTLM-init",
            session.HttpClient.Request.Headers.GetFirstHeader("Proxy-Authorization")?.Value);
    }

    [TestMethod]
    public async Task Handle407_ChallengeToken_SetsFinalProxyAuthorization()
    {
        using var proxy = new ProxyServer(false, false, false);
        proxy.UpstreamProxyWinAuthTokenGenerator = (_, scheme, challenge, _) =>
            $" {scheme}:{challenge}";

        using var session = MakeSession(proxy);
        using var serverConn = await CreateServerConnectionAsync(proxy,
            new ExternalProxy { HostName = "up.proxy", Port = 8080, UseDefaultCredentials = true });
        session.HttpClient.SetConnection(serverConn);

        session.HttpClient.Request.Url = "http://example.com/";
        session.HttpClient.Request.Method = "GET";
        session.HttpClient.Request.Locked = true;
        session.HttpClient.Response.StatusCode = 407;
        session.HttpClient.Response.IsBodyRead = true;
        session.HttpClient.Response.Body = Array.Empty<byte>();
        session.HttpClient.Response.Headers.AddHeader("Proxy-Authenticate", "Negotiate abc123");

        await InvokeHandle407(proxy, session);

        Assert.IsTrue(session.ReRequest);
        Assert.IsTrue(session.HttpClient.Connection.IsWinAuthenticated);
        Assert.AreEqual("Negotiate Negotiate:abc123",
            session.HttpClient.Request.Headers.GetFirstHeader("Proxy-Authorization")?.Value);
    }

    private static async Task<TcpServerConnection> CreateServerConnectionAsync(ProxyServer proxy,
        IExternalProxy upStreamProxy)
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var connectTask = listener.AcceptSocketAsync();
        var client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        await client.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
        var accepted = await connectTask;
        accepted.Dispose();

        var stream = new HttpServerStream(proxy, new NetworkStream(client, ownsSocket: true),
            new DefaultBufferPool(), CancellationToken.None);
        return new TcpServerConnection(proxy, client, stream, "origin.test", 80, false,
            default, HttpHeader.Version11, upStreamProxy, null, "cache-key");
    }
}
