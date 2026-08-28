using System;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.EventArguments;
using Titanium.Web.Proxy.Helpers;
using Titanium.Web.Proxy.Http;
using Titanium.Web.Proxy.Models;
using Titanium.Web.Proxy.Network.Tcp;

namespace Titanium.Web.Proxy.UnitTests;

[TestClass]
public class ProxyResultsTests
{
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
        return session;
    }

    [TestMethod]
    public void Html_Sets_ContentType_And_Status()
    {
        var response = ProxyResults.Html("<html/>");

        Assert.AreEqual(200, response.StatusCode);
        Assert.AreEqual("text/html; charset=utf-8", response.ContentType);
        Assert.AreEqual("<html/>", Encoding.UTF8.GetString(response.Body));
    }

    [TestMethod]
    public void Text_Sets_ContentType()
    {
        var response = ProxyResults.Text("hello");

        Assert.AreEqual("text/plain; charset=utf-8", response.ContentType);
        Assert.AreEqual("hello", Encoding.UTF8.GetString(response.Body));
    }

    [TestMethod]
    public void Bytes_Sets_ContentType_And_Body()
    {
        var data = new byte[] { 1, 2, 3 };
        var response = ProxyResults.Bytes(data, "image/png");

        Assert.AreEqual(200, response.StatusCode);
        Assert.AreEqual("image/png", response.ContentType);
        CollectionAssert.AreEqual(data, response.Body);
    }

    [TestMethod]
    public void Json_Serializes_Object()
    {
        var response = ProxyResults.Json(new { error = "blocked" }, HttpStatusCode.Forbidden);

        Assert.AreEqual(403, response.StatusCode);
        Assert.AreEqual("application/json; charset=utf-8", response.ContentType);
        StringAssert.Contains(Encoding.UTF8.GetString(response.Body), "blocked");
    }

    [TestMethod]
    public void Redirect_Sets_Location_And_Status()
    {
        var response = ProxyResults.Redirect("https://example.com/", HttpStatusCode.MovedPermanently);

        Assert.AreEqual(301, response.StatusCode);
        Assert.AreEqual("https://example.com/", response.Headers.GetFirstHeader(KnownHeaders.Location)?.Value);
        Assert.AreEqual(0, response.Body.Length);
    }

    [TestMethod]
    public void NoContent_Has_No_Body()
    {
        var response = ProxyResults.NoContent();

        Assert.AreEqual(204, response.StatusCode);
        Assert.IsFalse(response.HasBody);
    }

    [TestMethod]
    public void Stream_Sets_ContentLength_When_Provided()
    {
        var result = ProxyResults.Stream(
            HttpStatusCode.OK,
            "application/octet-stream",
            (_, _) => Task.CompletedTask,
            contentLength: 42);

        Assert.AreEqual(42, result.Response.ContentLength);
        Assert.AreEqual("application/octet-stream", result.Response.ContentType);
    }

    [TestMethod]
    public void File_Throws_When_Missing()
    {
        Assert.ThrowsExactly<FileNotFoundException>(() =>
            ProxyResults.File(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".missing"), "text/plain"));
    }

    [TestMethod]
    public void Ok_String_Sets_Html_ContentType()
    {
        using var session = MakeSession();
        session.Ok("<html/>");

        Assert.AreEqual("text/html; charset=utf-8", session.HttpClient.Response.ContentType);
    }

    [TestMethod]
    public void Respond_StreamingProxyResult_Configures_StreamWriter()
    {
        using var session = MakeSession();
        var result = ProxyResults.Stream(
            HttpStatusCode.OK,
            "text/plain",
            async (stream, ct) => await stream.WriteAsync("x"u8.ToArray(), ct));

        session.RespondStreaming(result, closeServerConnection: false);

        Assert.IsNotNull(session.HttpClient.Response.StreamBodyWriter);
        Assert.IsTrue(session.HttpClient.Request.CancelRequest);
    }
}
