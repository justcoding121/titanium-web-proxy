using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.EventArguments;
using Titanium.Web.Proxy.Helpers;
using Titanium.Web.Proxy.Http;
using Titanium.Web.Proxy.Http2;
using Titanium.Web.Proxy.Models;
using Titanium.Web.Proxy.Network.Tcp;

namespace Titanium.Web.Proxy.UnitTests;

[TestClass]
public class Http2HeaderListenerAndSessionRespondTests
{
    private static ByteString Bs(string s) => Encoding.ASCII.GetBytes(s);

    private static SessionEventArgs MakeSession()
    {
        var proxy = new ProxyServer(false, false, false);
        var endPoint = new ExplicitProxyEndPoint(IPAddress.Loopback, 0, false);
        var connection = new QuicClientConnection(
            proxy, new IPEndPoint(IPAddress.Loopback, 4433), new IPEndPoint(IPAddress.Loopback, 12345));
        var cts = new CancellationTokenSource();
        var clientStream = new HttpClientStream(proxy, connection, Stream.Null, proxy.BufferPool, cts.Token);
        var session = new SessionEventArgs(proxy, endPoint, clientStream, null, cts);
        session.HttpClient.Request.HttpVersion = HttpHeader.Version11;
        session.HttpClient.Request.Method = "GET";
        session.HttpClient.Request.RequestUriString = "http://example.com/";
        return session;
    }

    [TestMethod]
    public void MyHeaderListener_ValidRequest_BuildsUri()
    {
        var added = new List<(string, string)>();
        var listener = new Http2Helper.MyHeaderListener((n, v) =>
            added.Add((Encoding.ASCII.GetString(n.Span), Encoding.ASCII.GetString(v.Span))), isRequest: true);

        listener.AddHeader(Bs(":method"), Bs("GET"), false);
        listener.AddHeader(Bs(":scheme"), Bs("https"), false);
        listener.AddHeader(Bs(":authority"), Bs("example.com"), false);
        listener.AddHeader(Bs(":path"), Bs("/x"), false);
        listener.AddHeader(Bs("user-agent"), Bs("test"), false);

        Assert.IsFalse(listener.HasMalformedHeader);
        Assert.AreEqual("https", listener.Scheme);
        Assert.AreEqual(new Uri("https://example.com/x"), listener.GetUri());
        Assert.AreEqual(1, added.Count);
    }

    [TestMethod]
    public void MyHeaderListener_EmptyAuthority_ThrowsFromGetUri()
    {
        var listener = new Http2Helper.MyHeaderListener((_, _) => { }, isRequest: true);
        listener.AddHeader(Bs(":method"), Bs("GET"), false);
        listener.AddHeader(Bs(":scheme"), Bs("http"), false);
        listener.AddHeader(Bs(":path"), Bs("/"), false);
        Assert.ThrowsExactly<InvalidOperationException>(() => listener.GetUri());
    }

    [TestMethod]
    public void MyHeaderListener_RejectsDuplicatesWrongDirectionAndUppercase()
    {
        var req = new Http2Helper.MyHeaderListener((_, _) => { }, isRequest: true);
        req.AddHeader(Bs(":method"), Bs("GET"), false);
        req.AddHeader(Bs(":method"), Bs("POST"), false);
        Assert.IsTrue(req.HasMalformedHeader);
        StringAssert.Contains(req.MalformedReason, "duplicate");

        var resp = new Http2Helper.MyHeaderListener((_, _) => { }, isRequest: false);
        resp.AddHeader(Bs(":method"), Bs("GET"), false);
        Assert.IsTrue(resp.HasMalformedHeader);

        var order = new Http2Helper.MyHeaderListener((_, _) => { }, isRequest: true);
        order.AddHeader(Bs("host"), Bs("h"), false);
        order.AddHeader(Bs(":path"), Bs("/"), false);
        Assert.IsTrue(order.HasMalformedHeader);
        StringAssert.Contains(order.MalformedReason, "after a regular");

        var upper = new Http2Helper.MyHeaderListener((_, _) => { }, isRequest: true);
        upper.AddHeader(Bs("X-Foo"), Bs("1"), false);
        Assert.IsTrue(upper.HasMalformedHeader);
        StringAssert.Contains(upper.MalformedReason, "uppercase");
    }

    [TestMethod]
    public void MyHeaderListener_UnknownPseudoAndStatusInRequest_AreMalformed()
    {
        var unknown = new Http2Helper.MyHeaderListener((_, _) => { }, isRequest: true);
        unknown.AddHeader(Bs(":foo"), Bs("bar"), false);
        Assert.IsTrue(unknown.HasMalformedHeader);

        var statusInReq = new Http2Helper.MyHeaderListener((_, _) => { }, isRequest: true);
        statusInReq.AddHeader(Bs(":status"), Bs("200"), false);
        Assert.IsTrue(statusInReq.HasMalformedHeader);

        var protocol = new Http2Helper.MyHeaderListener((_, _) => { }, isRequest: true);
        protocol.AddHeader(Bs(":protocol"), Bs("websocket"), false);
        Assert.IsFalse(protocol.HasMalformedHeader);
        Assert.AreEqual("websocket", Encoding.ASCII.GetString(protocol.Protocol.Span));
    }

    [TestMethod]
    public void MyHeaderListener_ResponseStatus_Ok()
    {
        var listener = new Http2Helper.MyHeaderListener((_, _) => { }, isRequest: false);
        listener.AddHeader(Bs(":status"), Bs("204"), false);
        Assert.IsFalse(listener.HasMalformedHeader);
        Assert.AreEqual("204", Encoding.ASCII.GetString(listener.Status.Span));
    }

    [TestMethod]
    public void Session_Ok_GenericResponse_Redirect_CancelRequest()
    {
        using var session = MakeSession();
        session.Ok("<html/>", new Dictionary<string, HttpHeader>
        {
            ["X-Test"] = new HttpHeader("X-Test", "1")
        });
        Assert.IsTrue(session.HttpClient.Request.CancelRequest);
        Assert.IsTrue(session.HttpClient.Request.Locked);
        Assert.AreEqual(200, session.HttpClient.Response.StatusCode);
        Assert.AreEqual("1", session.HttpClient.Response.Headers.GetFirstHeader("X-Test")?.Value);

        using var session2 = MakeSession();
        session2.GenericResponse("nope", HttpStatusCode.Forbidden);
        Assert.AreEqual(403, session2.HttpClient.Response.StatusCode);

        using var session3 = MakeSession();
        session3.Redirect("https://example.com/r");
        Assert.AreEqual("https://example.com/r",
            session3.HttpClient.Response.Headers.GetFirstHeader(KnownHeaders.Location)?.Value);

        using var session4 = MakeSession();
        session4.Ok(Encoding.ASCII.GetBytes("bytes"));
        Assert.AreEqual("bytes", Encoding.ASCII.GetString(session4.HttpClient.Response.Body));
    }

    [TestMethod]
    public void Session_RespondStreaming_And_TerminateServerConnection()
    {
        using var session = MakeSession();
        var response = new Response { StatusCode = 200, StatusDescription = "OK" };
        session.RespondStreaming(response, async (stream, ct) =>
        {
            await stream.WriteAsync(Encoding.ASCII.GetBytes("x"), ct);
        });
        Assert.IsNotNull(session.HttpClient.Response.StreamBodyWriter);
        Assert.IsTrue(session.HttpClient.Response.IsChunked);

        session.TerminateServerConnection();
        Assert.IsTrue(session.HttpClient.CloseServerConnection);
    }

    [TestMethod]
    public void Session_Respond_AfterResponseLocked_Throws()
    {
        using var session = MakeSession();
        session.HttpClient.Request.Locked = true;
        session.HttpClient.Response.Locked = true;
        Assert.ThrowsExactly<InvalidOperationException>(() => session.Respond(new Response { StatusCode = 200 }));
    }
}
