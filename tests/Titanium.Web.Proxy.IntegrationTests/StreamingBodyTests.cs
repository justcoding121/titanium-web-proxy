using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Security;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Http;
using Titanium.Web.Proxy.IntegrationTests.Helpers;
using Titanium.Web.Proxy.IntegrationTests.Setup;

namespace Titanium.Web.Proxy.IntegrationTests;

[DoNotParallelize]
[TestClass]
public class StreamingBodyTests
{
    private static TestServer sharedServer = null!;
    private static readonly string[] writeBody = new[] { "chunk1", "chunk2", "chunk3" };

    [ClassInitialize]
    public static void ClassSetup(TestContext _)
    {
        sharedServer = new TestServer(TestCertificateAuthority.ServerCertificate, requireMutualTls: false);
    }

    [ClassCleanup(ClassCleanupBehavior.EndOfClass)]
    public static void ClassCleanup()
    {
        sharedServer?.Dispose();
    }

    // The per-chunk body-write hook only runs for plain (non-TLS) HTTP, because it operates directly on the
    // network stream. These tests therefore go over http:// so the hook is exercised.

    [TestMethod]
    public async Task OnResponseBodyWrite_Passthrough_Is_Byte_For_Byte()
    {
        using var testSuite = new TestSuite(sharedServer);

        const string expected = "I am server. I received your greetings.";

        var server = testSuite.GetServer();
        server.HandleRequest(context => context.Response.WriteAsync(expected));

        var proxy = testSuite.GetProxy();

        var callbackCount = 0;
        proxy.OnResponseBodyWrite += (sender, e) =>
        {
            callbackCount++;
            // leave the bytes unchanged
            return Task.CompletedTask;
        };

        var client = testSuite.GetClient(proxy);

        var response = await client.GetAsync(new Uri(server.ListeningHttpUrl));
        var body = await response.Content.ReadAsStringAsync();

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual(expected, body);
        Assert.IsTrue(callbackCount > 0, "The response body write hook should have been invoked.");
    }

    [TestMethod]
    public async Task OnResponseBodyWrite_Can_Rewrite_Body()
    {
        using var testSuite = new TestSuite(sharedServer);

        var server = testSuite.GetServer();
        server.HandleRequest(context => context.Response.WriteAsync("hello world"));

        var proxy = testSuite.GetProxy();

        proxy.OnResponseBodyWrite += (sender, e) =>
        {
            var text = Encoding.ASCII.GetString(e.BodyBytes);
            e.BodyBytes = Encoding.ASCII.GetBytes(text.ToUpperInvariant());
            return Task.CompletedTask;
        };

        var client = testSuite.GetClient(proxy);

        var response = await client.GetAsync(new Uri(server.ListeningHttpUrl));
        var body = await response.Content.ReadAsStringAsync();

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual("HELLO WORLD", body);
    }

    [TestMethod]
    public async Task Large_Response_Streams_Incrementally_Without_Full_Buffering()
    {
        using var testSuite = new TestSuite(sharedServer);

        // ~1 MB body so it spans many buffer-sized reads.
        const int totalSize = 1024 * 1024;
        var payload = new byte[totalSize];
        for (var i = 0; i < payload.Length; i++) payload[i] = (byte)(i % 251);

        var server = testSuite.GetServer();
        server.HandleRequest(async context => await context.Response.Body.WriteAsync(payload));

        var proxy = testSuite.GetProxy();

        var callbackCount = 0;
        long observedBytes = 0;
        proxy.OnResponseBodyWrite += (sender, e) =>
        {
            Interlocked.Increment(ref callbackCount);
            observedBytes += e.BodyBytes.Length;
            return Task.CompletedTask;
        };

        var client = testSuite.GetClient(proxy);

        var response = await client.GetAsync(new Uri(server.ListeningHttpUrl));
        var body = await response.Content.ReadAsByteArrayAsync();

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual(totalSize, body.Length);
        CollectionAssert.AreEqual(payload, body);

        // The hook must have fired multiple times (proving the body was streamed in pieces, not buffered whole).
        Assert.IsTrue(callbackCount > 1,
            $"Expected the body to stream in multiple pieces but the hook fired {callbackCount} time(s).");
        Assert.AreEqual(totalSize, observedBytes);
    }

    [TestMethod]
    public async Task RespondStreaming_Chunked_Generates_Body_Without_Contacting_Server()
    {
        using var testSuite = new TestSuite(sharedServer);

        var serverCalled = false;
        var server = testSuite.GetServer();
        server.HandleRequest(context =>
        {
            serverCalled = true;
            return context.Response.WriteAsync("from server");
        });

        var proxy = testSuite.GetProxy();
        proxy.BeforeRequest += (sender, e) =>
        {
            var response = new Response
            {
                StatusCode = 200,
                StatusDescription = "OK",
                HttpVersion = e.HttpClient.Request.HttpVersion
            };

            e.RespondStreaming(response, async (stream, ct) =>
            {
                foreach (var part in writeBody)
                {
                    var bytes = Encoding.ASCII.GetBytes(part);
                    await stream.WriteAsync(bytes, ct);
                }
            }, closeServerConnection: true);

            return Task.CompletedTask;
        };

        var client = testSuite.GetClient(proxy);

        var response = await client.GetAsync(new Uri(server.ListeningHttpUrl));
        var body = await response.Content.ReadAsStringAsync();

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual("chunk1chunk2chunk3", body);
        Assert.IsFalse(serverCalled, "Server should not be contacted for a synthetic streamed response.");
    }

    [TestMethod]
    public async Task RespondStreaming_FixedLength_Writes_Raw_With_ContentLength()
    {
        using var testSuite = new TestSuite(sharedServer);

        var server = testSuite.GetServer();
        server.HandleRequest(context => context.Response.WriteAsync("from server"));

        var payload = Encoding.ASCII.GetBytes("0123456789ABCDEF");

        var proxy = testSuite.GetProxy();
        proxy.BeforeRequest += (sender, e) =>
        {
            var response = new Response
            {
                StatusCode = 200,
                StatusDescription = "OK",
                HttpVersion = e.HttpClient.Request.HttpVersion
            };
            response.Headers.AddHeader(KnownHeaders.ContentLength, payload.Length.ToString());

            e.RespondStreaming(response, async (stream, ct) =>
            {
                // write in two pieces to prove streaming
                await stream.WriteAsync(payload.AsMemory(0, 8), ct);
                await stream.WriteAsync(payload.AsMemory(8, payload.Length - 8), ct);
            }, closeServerConnection: true);

            return Task.CompletedTask;
        };

        var client = testSuite.GetClient(proxy);

        var response = await client.GetAsync(new Uri(server.ListeningHttpUrl));
        var body = await response.Content.ReadAsByteArrayAsync();

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        CollectionAssert.AreEqual(payload, body);
        Assert.AreEqual(payload.Length, response.Content.Headers.ContentLength);
    }

    [TestMethod]
    public async Task OnResponseBodyWrite_Tls_Decrypted_Http11_Body_Relays_Correctly_And_Hook_Fires()
    {
        // The per-chunk body-write hook gate in HttpStream.CopyBodyAsync checks the internal
        // ITransportCapableStream.SupportsBodyWriteHook capability instead of the old IsNetworkStream flag,
        // and HttpStream reports that capability as true whenever its backing stream is either a plain
        // NetworkStream or a decrypted SslStream. So OnResponseBodyWrite must fire with parity for a
        // TLS-decrypted HTTP/1.x connection, exactly as it already does for plain HTTP.
        using var testSuite = new TestSuite(sharedServer);

        const string expected = "I am server. I received your greetings.";

        var server = testSuite.GetServer();
        server.HandleRequest(context => context.Response.WriteAsync(expected));

        var proxy = testSuite.GetProxy();

        var callbackCount = 0;
        var observedBytes = new List<byte>();
        proxy.OnResponseBodyWrite += (sender, e) =>
        {
            callbackCount++;
            observedBytes.AddRange(e.BodyBytes);
            return Task.CompletedTask;
        };

        var client = testSuite.GetClient(proxy);

        var response = await client.GetAsync(new Uri(server.ListeningHttpsUrl));
        var body = await response.Content.ReadAsStringAsync();

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual(expected, body);
        Assert.IsTrue(callbackCount > 0,
            "The response body write hook should now fire for TLS-decrypted HTTP/1.x connections too.");
        Assert.AreEqual(expected, Encoding.ASCII.GetString(observedBytes.ToArray()),
            "The hook should observe the same bytes that were relayed to the client.");
    }

    [TestMethod]
    public async Task OnResponseBodyWrite_Tls_Decrypted_Http11_Can_Rewrite_Body()
    {
        // Companion to the read-only test above: proves the hook is not just invoked but its mutation of
        // e.BodyBytes is actually relayed to the client for a TLS-decrypted HTTP/1.x connection, matching
        // the plain-HTTP behavior in OnResponseBodyWrite_Can_Rewrite_Body.
        using var testSuite = new TestSuite(sharedServer);

        var server = testSuite.GetServer();
        server.HandleRequest(context => context.Response.WriteAsync("hello world"));

        var proxy = testSuite.GetProxy();

        proxy.OnResponseBodyWrite += (sender, e) =>
        {
            var text = Encoding.ASCII.GetString(e.BodyBytes);
            e.BodyBytes = Encoding.ASCII.GetBytes(text.ToUpperInvariant());
            return Task.CompletedTask;
        };

        var client = testSuite.GetClient(proxy);

        var response = await client.GetAsync(new Uri(server.ListeningHttpsUrl));
        var body = await response.Content.ReadAsStringAsync();

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual("HELLO WORLD", body);
    }

    [TestMethod]
    public async Task OnResponseBodyWrite_Http2_Can_Rewrite_Body()
    {
        using var testSuite = new TestSuite(sharedServer);

        var server = testSuite.GetServer();
        server.HandleRequest(context => context.Response.WriteAsync("hello world"));

        var proxy = testSuite.GetProxy();
        proxy.EnableHttp2 = true;

        proxy.OnResponseBodyWrite += (sender, e) =>
        {
            var text = Encoding.ASCII.GetString(e.BodyBytes);
            e.BodyBytes = Encoding.ASCII.GetBytes(text.ToUpperInvariant());
            return Task.CompletedTask;
        };

        using var client = CreateHttp2Client(proxy);

        var response = await client.GetAsync(new Uri(server.ListeningHttpsUrl));
        var body = await response.Content.ReadAsStringAsync();

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual(new Version(2, 0), response.Version);
        Assert.AreEqual("HELLO WORLD", body);
    }

    [TestMethod]
    public async Task RespondStreaming_Http2_Generates_Body_Without_Contacting_Server()
    {
        using var testSuite = new TestSuite(sharedServer);

        var serverCalled = false;
        var server = testSuite.GetServer();
        server.HandleRequest(context =>
        {
            serverCalled = true;
            return context.Response.WriteAsync("from server");
        });

        var proxy = testSuite.GetProxy();
        proxy.EnableHttp2 = true;

        proxy.BeforeRequest += (sender, e) =>
        {
            var response = new Response
            {
                StatusCode = 200,
                StatusDescription = "OK",
                HttpVersion = e.HttpClient.Request.HttpVersion
            };

            e.RespondStreaming(response, async (stream, ct) =>
            {
                foreach (var part in writeBody)
                {
                    var bytes = Encoding.ASCII.GetBytes(part);
                    await stream.WriteAsync(bytes, ct);
                }
            }, closeServerConnection: true);

            return Task.CompletedTask;
        };

        using var client = CreateHttp2Client(proxy);

        var response = await client.GetAsync(new Uri(server.ListeningHttpsUrl));
        var body = await response.Content.ReadAsStringAsync();

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual(new Version(2, 0), response.Version);
        Assert.AreEqual("chunk1chunk2chunk3", body);
        Assert.IsFalse(serverCalled, "Server should not be contacted for a synthetic streamed response.");
    }

    private static HttpClient CreateHttp2Client(ProxyServer proxy)
    {
        var handler = new SocketsHttpHandler
        {
            Proxy = new TestHelper.TestProxy($"http://localhost:{proxy.ProxyEndPoints[0].Port}", false),
            UseProxy = true,
            SslOptions =
            {
                RemoteCertificateValidationCallback =
                    (_, certificate, _, errors) => TestCertificateAuthority.Validate(certificate, errors)
            }
        };

        return new HttpClient(handler)
        {
            DefaultRequestVersion = new Version(2, 0),
            DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact
        };
    }
}
