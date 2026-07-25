using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Http2;
using Titanium.Web.Proxy.IntegrationTests.Helpers;
using Titanium.Web.Proxy.IntegrationTests.Setup;

namespace Titanium.Web.Proxy.IntegrationTests;

[TestClass]
public class Http2MultipartTests
{
    /// <summary>
    ///     Builds a minimal multipart/form-data body with the given boundary and one field.
    ///     Kept well under the HTTP/2 default 65,535-byte flow-control window so the test
    ///     does not need to send WINDOW_UPDATE frames.
    /// </summary>
    private static byte[] BuildMultipartBody(string boundary, string fieldName, string value)
    {
        var sb = new StringBuilder();
        sb.Append($"--{boundary}\r\n");
        sb.Append($"Content-Disposition: form-data; name=\"{fieldName}\"\r\n");
        sb.Append("\r\n");
        sb.Append(value);
        sb.Append($"\r\n--{boundary}--\r\n");
        return Encoding.UTF8.GetBytes(sb.ToString());
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task Http2_Multipart_Request_Fires_PartSent_Events()
    {
        // Verify that multipart/form-data requests over HTTP/2 fire the same
        // OnMultipartRequestPartSent events as HTTP/1.x does.
        using var testSuite = new TestSuite();
        var server = testSuite.GetServer();
        server.HandleRequest(context => context.Response.WriteAsync("ok"));

        var proxy = testSuite.GetProxy();
        proxy.EnableHttp2 = true;

        var partHeaders = new ConcurrentBag<string>();
        proxy.BeforeRequest += (_, e) =>
        {
            e.MultipartRequestPartSent += (_, partArgs) =>
            {
                partHeaders.Add(partArgs.Headers.GetFirstHeader("Content-Disposition")?.Value ?? "");
            };
            return Task.CompletedTask;
        };

        const string boundary = "----TestBoundary123";
        var bodyBytes = BuildMultipartBody(boundary, "field1", "value1");
        var contentType = $"multipart/form-data; boundary={boundary}";

        using var rawClient = await Http2RawClient.ConnectAsync(proxy.ProxyEndPoints[0].Port, "localhost",
            server.HttpsListeningPort);

        var requestHeaders = rawClient.Connection.EncodeHeaders(
            new[]
            {
                (":method", "POST"), (":scheme", "https"), (":authority", "localhost"), (":path", "/")
            },
            new[]
            {
                ("content-type", contentType),
                ("content-length", bodyBytes.Length.ToString())
            });

        // Send HEADERS without END_STREAM, then DATA with END_STREAM
        await rawClient.Connection.WriteHeaderBlockAsync(1, requestHeaders, false);
        await rawClient.Connection.WriteFrameAsync(Http2FrameType.Data, 1, Http2FrameFlag.EndStream, bodyBytes);

        // Read and discard the response
        var (_, responseHeaders, endStream) = await rawClient.Connection.ReadHeaderBlockAsync();
        Assert.AreEqual("200", responseHeaders.Single(h => h.Name == ":status").Value);
        if (!endStream)
        {
            Http2RawFrame.Frame frame;
            do
            {
                frame = await rawClient.Connection.ReadFrameAsync();
            } while (frame.Type != Http2FrameType.Data || (frame.Flags & Http2FrameFlag.EndStream) == 0);
        }

        // Give any in-flight observer callbacks a moment to complete
        await Task.Delay(100);

        Assert.IsTrue(partHeaders.Count >= 1,
            $"Expected at least 1 part event, got {partHeaders.Count}. " +
            "Multipart boundary-aware streaming must work over h2.");
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task Http11_Multipart_Request_PartSent_Still_Works()
    {
        // Regression: HTTP/1.1 multipart must still fire events after the h2 changes.
        using var testSuite = new TestSuite();
        var server = testSuite.GetServer();
        server.HandleRequest(async context =>
        {
            context.Response.StatusCode = 200;
            await context.Response.WriteAsync("ok");
        });

        var proxy = testSuite.GetProxy();
        var partCount = 0;
        proxy.BeforeRequest += (_, e) =>
        {
            e.MultipartRequestPartSent += (_, _) => partCount++;
            return Task.CompletedTask;
        };

        using var client = testSuite.GetClient(proxy);

        var content = new MultipartFormDataContent("----BoundaryABC");
        content.Add(new StringContent("hello"), "greeting");

        var response = await client.PostAsync(new Uri(server.ListeningHttpsUrl), content);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);

        await Task.Delay(100);
        Assert.IsTrue(partCount >= 1, "HTTP/1.1 multipart part events must still fire.");
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task Http2_Non_Multipart_Request_No_Events()
    {
        // Non-multipart h2 requests must not create observers or fire events.
        using var testSuite = new TestSuite();
        var server = testSuite.GetServer();
        server.HandleRequest(context => context.Response.WriteAsync("ok"));

        var proxy = testSuite.GetProxy();
        proxy.EnableHttp2 = true;

        var eventFired = false;
        proxy.BeforeRequest += (_, e) =>
        {
            e.MultipartRequestPartSent += (_, _) => eventFired = true;
            return Task.CompletedTask;
        };

        var bodyBytes = Encoding.UTF8.GetBytes("{\"key\":\"value\"}");

        using var rawClient = await Http2RawClient.ConnectAsync(proxy.ProxyEndPoints[0].Port, "localhost",
            server.HttpsListeningPort);

        var requestHeaders = rawClient.Connection.EncodeHeaders(
            new[]
            {
                (":method", "POST"), (":scheme", "https"), (":authority", "localhost"), (":path", "/")
            },
            new[]
            {
                ("content-type", "application/json"),
                ("content-length", bodyBytes.Length.ToString())
            });

        await rawClient.Connection.WriteHeaderBlockAsync(1, requestHeaders, false);
        await rawClient.Connection.WriteFrameAsync(Http2FrameType.Data, 1, Http2FrameFlag.EndStream, bodyBytes);

        var (_, responseHeaders, endStream) = await rawClient.Connection.ReadHeaderBlockAsync();
        Assert.AreEqual("200", responseHeaders.Single(h => h.Name == ":status").Value);
        if (!endStream)
        {
            Http2RawFrame.Frame frame;
            do
            {
                frame = await rawClient.Connection.ReadFrameAsync();
            } while (frame.Type != Http2FrameType.Data || (frame.Flags & Http2FrameFlag.EndStream) == 0);
        }

        await Task.Delay(100);
        Assert.IsFalse(eventFired, "Non-multipart requests must not fire multipart events.");
    }
}
