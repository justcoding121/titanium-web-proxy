using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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

    [TestMethod]
    public async Task RespondStreaming_Http2_DelayedBody_DoesNotBlock_SiblingStream()
    {
        using var testSuite = new TestSuite(sharedServer);

        var server = testSuite.GetServer();
        server.HandleRequest(context =>
        {
            if (context.Request.Path == "/fast")
                return context.Response.WriteAsync("fast-ok");
            return context.Response.WriteAsync("should-not-reach");
        });

        var proxy = testSuite.GetProxy();
        proxy.EnableHttp2 = true;

        var slowStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSlow = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var afterResponseCount = 0;

        proxy.BeforeRequest += (sender, e) =>
        {
            if (e.HttpClient.Request.RequestUri.AbsolutePath != "/slow")
                return Task.CompletedTask;

            var response = new Response
            {
                StatusCode = 200,
                StatusDescription = "OK",
                HttpVersion = e.HttpClient.Request.HttpVersion
            };

            e.RespondStreaming(response, async (stream, ct) =>
            {
                slowStarted.TrySetResult();
                await releaseSlow.Task.WaitAsync(ct);
                var bytes = Encoding.ASCII.GetBytes("slow-done");
                await stream.WriteAsync(bytes, ct);
            }, closeServerConnection: true);

            return Task.CompletedTask;
        };

        proxy.AfterResponse += (_, _) =>
        {
            Interlocked.Increment(ref afterResponseCount);
            return Task.CompletedTask;
        };

        using var client = CreateHttp2Client(proxy);

        var slowTask = client.GetAsync(new Uri(new Uri(server.ListeningHttpsUrl), "/slow"));
        await slowStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));

        // While /slow is still streaming, a sibling multiplexed request must complete.
        using var fastCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var fastResponse = await client.GetAsync(new Uri(new Uri(server.ListeningHttpsUrl), "/fast"),
            fastCts.Token);
        var fastBody = await fastResponse.Content.ReadAsStringAsync(fastCts.Token);

        Assert.AreEqual(HttpStatusCode.OK, fastResponse.StatusCode);
        Assert.AreEqual(new Version(2, 0), fastResponse.Version);
        Assert.AreEqual("fast-ok", fastBody);

        releaseSlow.TrySetResult();
        var slowResponse = await slowTask;
        var slowBody = await slowResponse.Content.ReadAsStringAsync();

        Assert.AreEqual(HttpStatusCode.OK, slowResponse.StatusCode);
        Assert.AreEqual("slow-done", slowBody);

        // AfterResponse should run once per completed stream (not wait for connection teardown).
        for (var i = 0; i < 50 && Volatile.Read(ref afterResponseCount) < 2; i++)
            await Task.Delay(50);
        Assert.IsTrue(Volatile.Read(ref afterResponseCount) >= 2,
            "AfterResponse should fire for each stream once synthetic END_STREAM completes.");
    }

    /// <summary>
    ///     The HTTP/2 body-write hook is awaited on the connection frame loop. A sleep there must not be
    ///     used for relay pacing: this test records whether a sibling stream can finish while the hook is
    ///     still asleep. Inspector skips that sleep when the result is a stall.
    /// </summary>
    [TestMethod]
    public async Task OnResponseBodyWrite_Http2_Sleep_SiblingStream()
    {
        using var testSuite = new TestSuite(sharedServer);
        var server = testSuite.GetServer();
        server.HandleRequest(context =>
        {
            if (context.Request.Path == "/fast")
            {
                return context.Response.WriteAsync("fast-ok");
            }

            return context.Response.WriteAsync(new string('S', 32 * 1024));
        });

        var proxy = testSuite.GetProxy();
        proxy.EnableHttp2 = true;
        var inHook = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        proxy.OnResponseBodyWrite += async (_, e) =>
        {
            if (e.Session.HttpClient.Request.RequestUri.AbsolutePath != "/slow")
            {
                return;
            }

            inHook.TrySetResult();
            await Task.Delay(1500);
        };

        using var client = CreateHttp2Client(proxy);
        var slow = client.GetAsync(new Uri(new Uri(server.ListeningHttpsUrl), "/slow"));
        await inHook.Task.WaitAsync(TimeSpan.FromSeconds(10));

        var siblingStalled = false;
        try
        {
            using var fastCts = new CancellationTokenSource(TimeSpan.FromMilliseconds(400));
            var fast = await client.GetAsync(new Uri(new Uri(server.ListeningHttpsUrl), "/fast"), fastCts.Token);
            Assert.AreEqual("fast-ok", await fast.Content.ReadAsStringAsync(fastCts.Token));
        }
        catch (OperationCanceledException)
        {
            siblingStalled = true;
        }

        // The frame loop is shared. A sleep in the hook stalls the sibling, so relay pacing must not sleep here.
        Assert.IsTrue(siblingStalled,
            "Expected the HTTP/2 body-write hook to stall a sibling stream. If it no longer does, HTTP/2 relay pacing can sleep in the hook.");

        using var drain = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await slow.WaitAsync(drain.Token);
    }

    /// <summary>
    ///     Characterization: BeforeResponse runs inline on the HTTP/2 frame loop, so a plain wait there costs
    ///     one latency per stream (six parallel responses take about six latencies). Inspector therefore does
    ///     not sleep in BeforeResponse on HTTP/2; see the streamed-replacement test below. If this test starts
    ///     failing, Core stopped serializing and the HTTP/2 branch in ApplyResponseThrottleAsync can be simplified.
    /// </summary>
    [TestMethod]
    [Timeout(60_000)]
    public async Task BeforeResponse_Http2_InlineLatencyWait_SerializesParallelStreams()
    {
        const int streams = 6;
        const int latencyMs = 400;
        using var testSuite = new TestSuite(sharedServer);
        var server = testSuite.GetServer();
        server.HandleRequest(context => context.Response.WriteAsync("ok"));

        var proxy = testSuite.GetProxy();
        proxy.EnableHttp2 = true;
        proxy.BeforeResponse += async (_, _) => await Task.Delay(latencyMs);

        using var client = CreateHttp2Client(proxy);
        // Warm the connection so the measurement is not dominated by the handshake.
        using (var warm = await client.GetAsync(new Uri(server.ListeningHttpsUrl)))
        {
            Assert.AreEqual(HttpStatusCode.OK, warm.StatusCode);
        }

        var watch = System.Diagnostics.Stopwatch.StartNew();
        var all = Enumerable.Range(0, streams)
            .Select(_ => client.GetAsync(new Uri(server.ListeningHttpsUrl)))
            .ToArray();
        var responses = await Task.WhenAll(all);
        watch.Stop();
        foreach (var r in responses)
        {
            Assert.AreEqual(new Version(2, 0), r.Version);
            r.Dispose();
        }

        Assert.IsTrue(watch.ElapsedMilliseconds >= latencyMs * 3,
            $"Expected serialized latency; {streams} parallel HTTP/2 responses took only {watch.ElapsedMilliseconds} ms (latency {latencyMs} ms).");
    }

    /// <summary>
    ///     The Inspector fix for the test above: on HTTP/2 the wait moves into a streamed replacement, which
    ///     is written off the frame loop, so parallel streams overlap their latency.
    /// </summary>
    [TestMethod]
    [Timeout(60_000)]
    public async Task BeforeResponse_Http2_LatencyInsideStreamedReplacement_OverlapsParallelStreams()
    {
        const int streams = 6;
        const int latencyMs = 400;
        using var testSuite = new TestSuite(sharedServer);
        var server = testSuite.GetServer();
        server.HandleRequest(context => context.Response.WriteAsync("ok"));

        var proxy = testSuite.GetProxy();
        proxy.EnableHttp2 = true;
        proxy.BeforeResponse += async (_, e) =>
        {
            var body = await e.GetResponseBody();
            var replacement = new Response
            {
                StatusCode = 200,
                StatusDescription = "OK",
                HttpVersion = e.HttpClient.Request.HttpVersion
            };
            replacement.Headers.AddHeader("Content-Length", body.Length.ToString());
            e.RespondStreaming(replacement, async (stream, ct) =>
            {
                await Task.Delay(latencyMs, ct);
                await stream.WriteAsync(body, ct);
            }, closeServerConnection: false);
        };

        using var client = CreateHttp2Client(proxy);
        using (var warm = await client.GetAsync(new Uri(server.ListeningHttpsUrl)))
        {
            Assert.AreEqual(HttpStatusCode.OK, warm.StatusCode);
        }

        var watch = System.Diagnostics.Stopwatch.StartNew();
        var responses = await Task.WhenAll(Enumerable.Range(0, streams)
            .Select(_ => client.GetAsync(new Uri(server.ListeningHttpsUrl))));
        watch.Stop();
        foreach (var r in responses)
        {
            Assert.AreEqual("ok", await r.Content.ReadAsStringAsync());
            r.Dispose();
        }

        Assert.IsTrue(watch.ElapsedMilliseconds < latencyMs * 3,
            $"{streams} parallel HTTP/2 responses took {watch.ElapsedMilliseconds} ms; one latency is {latencyMs} ms.");
    }

    [TestMethod]
    [Timeout(60_000)]
    public async Task BeforeRequest_Http2_UploadWait_DoesNotSerializeParallelStreams()
    {
        const int streams = 6;
        const int waitMs = 400;
        using var testSuite = new TestSuite(sharedServer);
        var server = testSuite.GetServer();
        server.HandleRequest(context => context.Response.WriteAsync("ok"));

        var proxy = testSuite.GetProxy();
        proxy.EnableHttp2 = true;
        proxy.BeforeRequest += async (_, _) => await Task.Delay(waitMs);

        using var client = CreateHttp2Client(proxy);
        using (var warm = await client.GetAsync(new Uri(server.ListeningHttpsUrl)))
        {
            Assert.AreEqual(HttpStatusCode.OK, warm.StatusCode);
        }

        var watch = System.Diagnostics.Stopwatch.StartNew();
        var responses = await Task.WhenAll(Enumerable.Range(0, streams)
            .Select(_ => client.GetAsync(new Uri(server.ListeningHttpsUrl))));
        watch.Stop();
        foreach (var r in responses)
        {
            r.Dispose();
        }

        Assert.IsTrue(watch.ElapsedMilliseconds < waitMs * 3,
            $"{streams} parallel HTTP/2 requests took {watch.ElapsedMilliseconds} ms; one wait is {waitMs} ms.");
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task BeforeResponse_Http2_ReplacesBufferedOriginBody_WithExactBytes()
    {
        using var testSuite = new TestSuite(sharedServer);
        var plain = new byte[70 * 1024];
        for (var i = 0; i < plain.Length; i++)
        {
            plain[i] = (byte)(i % 251);
        }

        var server = testSuite.GetServer();
        server.HandleRequest(async context => await context.Response.Body.WriteAsync(plain));
        var proxy = testSuite.GetProxy();
        proxy.EnableHttp2 = true;
        proxy.BeforeResponse += async (_, e) =>
        {
            var body = await e.GetResponseBody();
            var replacement = new Response
            {
                StatusCode = 200,
                StatusDescription = "OK",
                HttpVersion = e.HttpClient.Request.HttpVersion,
            };
            replacement.Headers.AddHeader("Content-Length", body.Length.ToString());
            e.RespondStreaming(replacement, async (stream, ct) =>
            {
                var offset = 0;
                while (offset < body.Length)
                {
                    var count = Math.Min(16 * 1024, body.Length - offset);
                    await stream.WriteAsync(body.AsMemory(offset, count), ct);
                    offset += count;
                }
            }, closeServerConnection: false);
        };

        using var client = CreateHttp2Client(proxy);
        var response = await client.GetAsync(new Uri(server.ListeningHttpsUrl));
        var got = await response.Content.ReadAsByteArrayAsync();
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual(new Version(2, 0), response.Version);
        CollectionAssert.AreEqual(plain, got);
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
