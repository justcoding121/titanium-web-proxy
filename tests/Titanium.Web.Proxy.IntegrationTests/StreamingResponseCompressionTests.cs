using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.Metrics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Abstractions.Routing;
using static Titanium.Web.Proxy.IntegrationTests.ProtocolMatrixTests;

namespace Titanium.Web.Proxy.IntegrationTests;

/// <summary>
///     Streaming response compression and bounded request decompression through a real proxy. The origin and the
///     HTTP/1.1 client are raw sockets so a test controls exactly when bytes move in each direction.
/// </summary>
[TestClass]
[DoNotParallelize]
public class StreamingResponseCompressionTests
{
    [ClassInitialize]
    public static void WarmUpChainBuilding(TestContext _)
    {
        // See ResponseCompressionIntegrationTests: the first X509Chain.Build in a process can throw.
        try
        {
            using var chain = new System.Security.Cryptography.X509Certificates.X509Chain();
            chain.Build(Titanium.Web.Proxy.IntegrationTests.Setup.TestCertificateAuthority.ServerCertificate);
        }
        catch (CryptographicException)
        {
            // Expected on some hosts; the outcome is irrelevant.
        }
    }

    private static RouteConfig[] Routes() =>
    [
        new RouteConfig
        {
            Id = "r",
            ClusterId = "c1",
            Match = new RouteMatch { Path = "/", PathKind = PathMatchKind.Prefix },
        },
    ];

    private static ResponseCompressionConfig Streaming() =>
        new() { Enabled = true, Mode = ResponseCompressionConfig.ModeStreaming };

    private static ResponseCompressionConfig Buffered() => new() { Enabled = true };

    /// <summary>Text that compresses several-fold but nowhere near the request ratio limit.</summary>
    private static byte[] Varied(int length, int seed = 11)
    {
        var random = new Random(seed);
        var words = new[] { "alpha", "bravo", "charlie", "delta", "echo", "foxtrot", "golf", "hotel", "india" };
        var text = new StringBuilder();
        while (text.Length < length)
        {
            text.Append(words[random.Next(words.Length)]).Append(random.Next(1000)).Append(' ');
        }

        return Encoding.ASCII.GetBytes(text.ToString())[..length];
    }

    private static byte[] Encode(string encoding, byte[] body, CompressionLevel level = CompressionLevel.Fastest)
    {
        using var output = new MemoryStream();
        using (Stream encoder = encoding == "br"
                   ? new BrotliStream(output, level, leaveOpen: true)
                   : new GZipStream(output, level, leaveOpen: true))
        {
            encoder.Write(body);
        }

        return output.ToArray();
    }

    private static byte[] Decode(string encoding, byte[] wire)
    {
        using var input = new MemoryStream(wire);
        using Stream decoder = encoding == "br"
            ? new BrotliStream(input, CompressionMode.Decompress)
            : new GZipStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        decoder.CopyTo(output);
        return output.ToArray();
    }

    /// <summary>Decodes as much of a possibly unfinished gzip stream as is available.</summary>
    private static int DecodablePrefix(byte[] wire)
    {
        using var input = new MemoryStream(wire);
        using var decoder = new GZipStream(input, CompressionMode.Decompress);
        var buffer = new byte[16384];
        var total = 0;
        try
        {
            int read;
            while ((read = decoder.Read(buffer, 0, buffer.Length)) > 0)
            {
                total += read;
            }
        }
        catch (InvalidDataException)
        {
            // A cut-off stream.
        }

        return total;
    }

    private static string Header(string head, string name)
    {
        var match = Regex.Match(head, "^" + Regex.Escape(name) + @":[ \t]*(.*?)[ \t]*\r?$",
            RegexOptions.IgnoreCase | RegexOptions.Multiline);
        return match.Success ? match.Groups[1].Value : "";
    }

    private static int Status(string head) => int.Parse(head.Split(' ')[1]);

    private static byte[] Chunked(params byte[][] pieces)
    {
        using var output = new MemoryStream();
        foreach (var piece in pieces)
        {
            output.Write(Encoding.ASCII.GetBytes(piece.Length.ToString("X") + "\r\n"));
            output.Write(piece);
            output.Write("\r\n"u8);
        }

        output.Write("0\r\n\r\n"u8);
        return output.ToArray();
    }

    private static string Get(string path, string acceptEncoding, string extra = "", string method = "GET") =>
        method + " http://matrix.test" + path + " HTTP/1.1\r\nHost: matrix.test\r\nAccept-Encoding: " + acceptEncoding
        + "\r\nConnection: close\r\n" + extra + "\r\n";

    // ---------------------------------------------------------------------------------------------------------
    // Streaming response compression
    // ---------------------------------------------------------------------------------------------------------

    [DataTestMethod]
    [DataRow(ClientKind.H1Cleartext, "br")]
    [DataRow(ClientKind.H1Cleartext, "gzip")]
    [DataRow(ClientKind.H2Tls, "br")]
    [DataRow(ClientKind.H2Tls, "gzip")]
    [Timeout(30_000)]
    public async Task ChunkedOrigin_RoundTripsWithRewrittenHeaders(ClientKind kind, string encoding)
    {
        var pieces = Enumerable.Range(0, 8).Select(i => Varied(4096, i)).ToArray();
        var plain = pieces.SelectMany(p => p).ToArray();
        await using var origin = new ScriptedOrigin(async (_, conn, ct) =>
        {
            await conn.WriteAsync("HTTP/1.1 200 OK\r\nContent-Type: text/html\r\nETag: \"v1\"\r\nVary: Origin\r\n"
                                  + "Transfer-Encoding: chunked\r\n\r\n");
            await conn.WriteAsync(Chunked(pieces));
        });
        await using var proxy = await StartAsync(origin.Port, Routes(), kind);
        proxy.Server.ResponseCompression = Streaming();

        using var response = await SendAsync(proxy, kind, "/page", [("Accept-Encoding", encoding)]);
        var wire = await response.Content.ReadAsByteArrayAsync();

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        CollectionAssert.AreEqual(new[] { encoding }, response.Content.Headers.ContentEncoding.ToArray());
        Assert.IsFalse(response.Content.Headers.NonValidated.Contains("Content-Length"), "a streamed body has no length");
        Assert.IsTrue(wire.Length < plain.Length);
        CollectionAssert.AreEqual(plain, Decode(encoding, wire));
        var vary = string.Join(",", response.Headers.Vary);
        StringAssert.Contains(vary, "Accept-Encoding");
        StringAssert.Contains(vary, "Origin");
        Assert.AreEqual("W/\"v1\"", response.Headers.ETag?.ToString());
    }

    [DataTestMethod]
    [DataRow(ClientKind.H1Cleartext)]
    [DataRow(ClientKind.H2Tls)]
    [Timeout(30_000)]
    public async Task KnownLengthOrigin_IsStreamedWithoutALength(ClientKind kind)
    {
        var plain = Varied(300_000);
        await using var origin = new ScriptedOrigin(async (_, conn, _) =>
        {
            await conn.WriteAsync("HTTP/1.1 200 OK\r\nContent-Type: text/plain\r\nContent-Length: " + plain.Length
                                  + "\r\n\r\n");
            await conn.WriteAsync(plain);
        });
        await using var proxy = await StartAsync(origin.Port, Routes(), kind);
        proxy.Server.ResponseCompression = Streaming();

        using var response = await SendAsync(proxy, kind, "/", [("Accept-Encoding", "gzip")]);
        var wire = await response.Content.ReadAsByteArrayAsync();

        CollectionAssert.AreEqual(new[] { "gzip" }, response.Content.Headers.ContentEncoding.ToArray());
        Assert.IsFalse(response.Content.Headers.NonValidated.Contains("Content-Length"));
        CollectionAssert.AreEqual(plain, Decode("gzip", wire));
    }

    [DataTestMethod]
    [DataRow(true)]
    [DataRow(false)]
    [Timeout(60_000)]
    public async Task BodyAboveTheBufferedLimit_IsCompressedOnlyWhenStreaming(bool streaming)
    {
        // 3 MiB is above the 1 MiB MaxBytes that bounds buffered mode.
        var plain = Varied(3 * 1024 * 1024);
        await using var origin = new ScriptedOrigin(async (_, conn, _) =>
        {
            await conn.WriteAsync("HTTP/1.1 200 OK\r\nContent-Type: text/plain\r\nContent-Length: " + plain.Length
                                  + "\r\n\r\n");
            await conn.WriteAsync(plain);
        });
        await using var proxy = await StartAsync(origin.Port, Routes(), ClientKind.H1Cleartext);
        proxy.Server.ResponseCompression = streaming ? Streaming() : Buffered();

        using var response = await SendAsync(proxy, ClientKind.H1Cleartext, "/big", [("Accept-Encoding", "gzip")]);
        var wire = await response.Content.ReadAsByteArrayAsync();

        if (streaming)
        {
            CollectionAssert.AreEqual(new[] { "gzip" }, response.Content.Headers.ContentEncoding.ToArray());
            Assert.IsTrue(wire.Length < plain.Length / 2);
            CollectionAssert.AreEqual(plain, Decode("gzip", wire));
        }
        else
        {
            Assert.AreEqual(0, response.Content.Headers.ContentEncoding.Count);
            Assert.AreEqual(plain.Length, response.Content.Headers.ContentLength);
            CollectionAssert.AreEqual(plain, wire);
        }
    }

    [TestMethod]
    [Timeout(60_000)]
    public async Task EveryUpstreamChunk_IsFlushedToTheClient()
    {
        var first = Varied(4096, 1);
        var second = Varied(4096, 2);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var origin = new ScriptedOrigin(async (_, conn, ct) =>
        {
            await conn.WriteAsync("HTTP/1.1 200 OK\r\nContent-Type: text/html\r\nTransfer-Encoding: chunked\r\n\r\n");
            await conn.WriteAsync(Chunked(first)[..^5]);
            // Hold the rest back until the client has the first piece, compressed and readable.
            await gate.Task.WaitAsync(TimeSpan.FromSeconds(20), ct);
            await conn.WriteAsync(Chunked(second));
        });
        await using var proxy = await StartAsync(origin.Port, Routes(), ClientKind.H1Cleartext);
        proxy.Server.ResponseCompression = Streaming();

        using var client = await RawConnection.ConnectAsync(proxy.Clear.Port);
        await client.WriteAsync(Get("/sse-like", "gzip"));
        var head = await client.ReadHeadAsync();
        Assert.AreEqual("gzip", Header(head!, "Content-Encoding"));
        Assert.AreEqual("chunked", Header(head!, "Transfer-Encoding"));

        var wire = new MemoryStream();
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (DecodablePrefix(wire.ToArray()) < first.Length)
        {
            Assert.IsTrue(DateTime.UtcNow < deadline,
                "the first origin chunk never reached the client while the origin was still holding the next one");
            var chunk = await client.ReadChunkAsync().WaitAsync(TimeSpan.FromSeconds(15));
            Assert.IsNotNull(chunk, "the body ended before the first piece was readable");
            wire.Write(chunk!);
        }

        Assert.AreEqual(0, origin.Completed, "the origin has not been released yet");
        gate.SetResult();
        while (await client.ReadChunkAsync() is { } rest)
        {
            wire.Write(rest);
        }

        CollectionAssert.AreEqual(first.Concat(second).ToArray(), Decode("gzip", wire.ToArray()));
    }

    [DataTestMethod]
    [DataRow("sse")]
    [DataRow("no-transform")]
    [DataRow("encoded")]
    [DataRow("small")]
    [DataRow("range")]
    [DataRow("head")]
    [DataRow("grpc")]
    [Timeout(30_000)]
    public async Task IneligibleResponses_AreRelayedUntouched(string scenario)
    {
        var plain = Varied(scenario == "small" ? 400 : 8192);
        var encodedBody = Encode("gzip", plain);
        var method = scenario == "head" ? "HEAD" : "GET";
        await using var origin = new ScriptedOrigin(async (_, conn, _) =>
        {
            switch (scenario)
            {
                case "sse":
                    await conn.WriteAsync("HTTP/1.1 200 OK\r\nContent-Type: text/event-stream\r\n"
                                          + "Transfer-Encoding: chunked\r\n\r\n");
                    await conn.WriteAsync(Chunked(plain));
                    break;
                case "grpc":
                    await conn.WriteAsync("HTTP/1.1 200 OK\r\nContent-Type: application/grpc\r\n"
                                          + "Transfer-Encoding: chunked\r\n\r\n");
                    await conn.WriteAsync(Chunked(plain));
                    break;
                case "encoded":
                    await conn.WriteAsync("HTTP/1.1 200 OK\r\nContent-Type: text/html\r\nContent-Encoding: gzip\r\n"
                                          + "Content-Length: " + encodedBody.Length + "\r\n\r\n");
                    await conn.WriteAsync(encodedBody);
                    break;
                case "range":
                    await conn.WriteAsync("HTTP/1.1 206 Partial Content\r\nContent-Type: text/html\r\n"
                                          + "Content-Range: bytes 0-" + (plain.Length - 1) + "/99999\r\n"
                                          + "Content-Length: " + plain.Length + "\r\n\r\n");
                    await conn.WriteAsync(plain);
                    break;
                case "head":
                    await conn.WriteAsync("HTTP/1.1 200 OK\r\nContent-Type: text/html\r\nContent-Length: "
                                          + plain.Length + "\r\n\r\n");
                    break;
                default:
                    await conn.WriteAsync("HTTP/1.1 200 OK\r\nContent-Type: text/html\r\n"
                                          + (scenario == "no-transform" ? "Cache-Control: no-transform\r\n" : "")
                                          + "Content-Length: " + plain.Length + "\r\n\r\n");
                    await conn.WriteAsync(plain);
                    break;
            }
        });
        await using var proxy = await StartAsync(origin.Port, Routes(), ClientKind.H1Cleartext);
        proxy.Server.ResponseCompression = Streaming();

        using var client = await RawConnection.ConnectAsync(proxy.Clear.Port);
        await client.WriteAsync(Get("/x", "br, gzip", scenario == "range" ? "Range: bytes=0-8191\r\n" : "", method));
        var head = (await client.ReadHeadAsync())!;
        var body = await client.ReadBodyAsync(head, noBody: method == "HEAD");

        Assert.AreEqual(scenario == "encoded" ? "gzip" : "", Header(head, "Content-Encoding"), head);
        Assert.AreEqual(scenario == "range" ? 206 : 200, Status(head));
        if (scenario == "head")
        {
            Assert.AreEqual(plain.Length.ToString(), Header(head, "Content-Length"));
            Assert.AreEqual(0, body.Length);
        }
        else
        {
            CollectionAssert.AreEqual(scenario == "encoded" ? encodedBody : plain, body);
        }
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task NotAcceptedEncoding_StaysIdentity()
    {
        var plain = Varied(8192);
        await using var origin = new ScriptedOrigin(async (_, conn, _) =>
        {
            await conn.WriteAsync("HTTP/1.1 200 OK\r\nContent-Type: text/html\r\nTransfer-Encoding: chunked\r\n\r\n");
            await conn.WriteAsync(Chunked(plain));
        });
        await using var proxy = await StartAsync(origin.Port, Routes(), ClientKind.H1Cleartext);
        proxy.Server.ResponseCompression = Streaming();

        using var client = await RawConnection.ConnectAsync(proxy.Clear.Port);
        await client.WriteAsync(Get("/x", "gzip;q=0, identity"));
        var head = (await client.ReadHeadAsync())!;
        var body = await client.ReadBodyAsync(head);

        Assert.AreEqual("", Header(head, "Content-Encoding"));
        CollectionAssert.AreEqual(plain, body);
    }

    // ---------------------------------------------------------------------------------------------------------
    // Backpressure and early abort
    // ---------------------------------------------------------------------------------------------------------

    private const int BlockBytes = 64 * 1024;
    private static readonly byte[] BlockTemplate = RandomNumberGenerator.GetBytes(BlockBytes);

    private static byte[] Block(int index)
    {
        var block = (byte[])BlockTemplate.Clone();
        BitConverter.TryWriteBytes(block, index);
        return block;
    }

    [TestMethod]
    [Timeout(120_000)]
    public async Task SlowClient_HoldsTheOriginBackWithBoundedMemory()
    {
        const int blocks = 1024; // 64 MiB of incompressible data
        long originWritten = 0;
        await using var origin = new ScriptedOrigin(async (_, conn, ct) =>
        {
            await conn.WriteAsync("HTTP/1.1 200 OK\r\nContent-Type: application/octet-stream\r\n"
                                  + "Transfer-Encoding: chunked\r\n\r\n");
            for (var i = 0; i < blocks; i++)
            {
                await conn.WriteAsync(Chunked(Block(i))[..^5]);
                Interlocked.Add(ref originWritten, BlockBytes);
            }

            await conn.WriteAsync("0\r\n\r\n");
        });
        await using var proxy = await StartAsync(origin.Port, Routes(), ClientKind.H1Cleartext);
        proxy.Server.ResponseCompression = new ResponseCompressionConfig
        {
            Enabled = true,
            Mode = ResponseCompressionConfig.ModeStreaming,
            MimeTypes = ["application/octet-stream"],
        };

        using var client = await RawConnection.ConnectAsync(proxy.Clear.Port, receiveBuffer: 16 * 1024);
        GC.Collect();
        var heapBefore = GC.GetTotalMemory(true);
        await client.WriteAsync(Get("/blob", "gzip"));
        var head = (await client.ReadHeadAsync())!;
        Assert.AreEqual("gzip", Header(head, "Content-Encoding"), head);

        var wire = new MemoryStream();
        wire.Write((await client.ReadChunkAsync())!);

        // The client now stops reading. Wait until the origin has made no progress for a while.
        long previous = -1;
        for (var i = 0; i < 40; i++)
        {
            await Task.Delay(250);
            var now = Interlocked.Read(ref originWritten);
            if (now == previous && i > 4)
            {
                break;
            }

            previous = now;
        }

        var stalledAt = Interlocked.Read(ref originWritten);
        var heapStalled = GC.GetTotalMemory(true);
        Assert.IsTrue(stalledAt < 32L * 1024 * 1024,
            $"the origin wrote {stalledAt / 1024 / 1024} MiB of 64 MiB although the client read nothing");
        Assert.IsTrue(heapStalled - heapBefore < 40L * 1024 * 1024,
            $"managed heap grew by {(heapStalled - heapBefore) / 1024 / 1024} MiB while the client was stalled");

        // Reading resumes: every byte arrives, intact.
        while (await client.ReadChunkAsync() is { } chunk)
        {
            wire.Write(chunk);
        }

        using var expected = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        for (var i = 0; i < blocks; i++)
        {
            expected.AppendData(Block(i));
        }

        using var actual = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        wire.Position = 0;
        using (var decoder = new GZipStream(wire, CompressionMode.Decompress))
        {
            var buffer = new byte[81920];
            int read;
            while ((read = decoder.Read(buffer, 0, buffer.Length)) > 0)
            {
                actual.AppendData(buffer, 0, read);
            }
        }

        CollectionAssert.AreEqual(expected.GetHashAndReset(), actual.GetHashAndReset());
    }

    [TestMethod]
    [Timeout(60_000)]
    public async Task ClientAbortMidStream_ClosesTheOriginAndLeavesTheProxyUsable()
    {
        await using var origin = new ScriptedOrigin(async (request, conn, ct) =>
        {
            if (request.Head.StartsWith("GET http://matrix.test/ok", StringComparison.Ordinal)
                || request.Head.StartsWith("GET /ok", StringComparison.Ordinal))
            {
                await conn.WriteAsync("HTTP/1.1 200 OK\r\nContent-Type: text/plain\r\nContent-Length: 2\r\n\r\nok");
                return;
            }

            await conn.WriteAsync("HTTP/1.1 200 OK\r\nContent-Type: text/plain\r\nTransfer-Encoding: chunked\r\n\r\n");
            for (var i = 0; i < 100_000; i++)
            {
                // Throws once the proxy has closed the origin connection.
                await conn.WriteAsync(Chunked(Varied(4096, i % 7))[..^5]);
                await Task.Delay(1, ct);
            }
        });
        await using var proxy = await StartAsync(origin.Port, Routes(), ClientKind.H1Cleartext);
        proxy.Server.ResponseCompression = Streaming();

        using (var client = await RawConnection.ConnectAsync(proxy.Clear.Port))
        {
            await client.WriteAsync(Get("/endless", "gzip"));
            var head = (await client.ReadHeadAsync())!;
            Assert.AreEqual("gzip", Header(head, "Content-Encoding"));
            Assert.IsNotNull(await client.ReadChunkAsync());

            // Reset the connection mid-stream.
            client.Client.Client.LingerState = new LingerOption(true, 0);
        }

        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (origin.Failures == 0 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(50);
        }

        Assert.IsTrue(origin.Failures > 0, "the origin connection was not closed after the client went away");

        using var response = await SendAsync(proxy, ClientKind.H1Cleartext, "/ok");
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual("ok", await response.Content.ReadAsStringAsync());
    }

    // ---------------------------------------------------------------------------------------------------------
    // Request decompression
    // ---------------------------------------------------------------------------------------------------------

    private static RequestDecompressionConfig Decompression(int maxBytes = 0, int ratio = 0) => new()
    {
        Enabled = true,
        MaxDecompressedBytes = maxBytes > 0 ? maxBytes : RequestDecompressionConfig.DefaultMaxDecompressedBytes,
        MaxRatio = ratio > 0 ? ratio : RequestDecompressionConfig.DefaultMaxRatio,
    };

    private static byte[] Post(string encoding, byte[] body, bool chunked = false)
    {
        var head = "POST http://matrix.test/upload HTTP/1.1\r\nHost: matrix.test\r\n"
                   + "Content-Type: application/json\r\nContent-Encoding: " + encoding + "\r\n"
                   + (chunked ? "Transfer-Encoding: chunked\r\n" : "Content-Length: " + body.Length + "\r\n")
                   + "Connection: close\r\n\r\n";
        var wire = Encoding.ASCII.GetBytes(head).ToList();
        if (chunked)
        {
            var half = body.Length / 2;
            wire.AddRange(Chunked(body[..half], body[half..]));
        }
        else
        {
            wire.AddRange(body);
        }

        return wire.ToArray();
    }

    private static ScriptedOrigin AcceptingOrigin() => new(async (_, conn, _) =>
        await conn.WriteAsync("HTTP/1.1 200 OK\r\nContent-Type: text/plain\r\nContent-Length: 2\r\n\r\nok"));

    private static async Task<int> PostAsync(ScriptedOrigin origin, RequestDecompressionConfig? config, byte[] request)
    {
        await using var proxy = await StartAsync(origin.Port, Routes(), ClientKind.H1Cleartext);
        proxy.Server.RequestDecompression = config;
        using var client = await RawConnection.ConnectAsync(proxy.Clear.Port);
        await client.WriteAsync(request);
        var head = (await client.ReadHeadAsync())!;
        return Status(head);
    }

    [DataTestMethod]
    [DataRow("gzip", false)]
    [DataRow("br", false)]
    [DataRow("gzip", true)]
    [DataRow("br", true)]
    [Timeout(30_000)]
    public async Task CompressedRequest_ReachesTheOriginDecompressed(string encoding, bool chunked)
    {
        var plain = Varied(150_000);
        await using var origin = AcceptingOrigin();

        var status = await PostAsync(origin, Decompression(), Post(encoding, Encode(encoding, plain), chunked));

        Assert.AreEqual(200, status);
        Assert.IsTrue(origin.Requests.TryPeek(out var received));
        Assert.AreEqual("", Header(received!.Head, "Content-Encoding"), received.Head);
        CollectionAssert.AreEqual(plain, received.Body);
        if (Header(received.Head, "Transfer-Encoding") == "")
        {
            Assert.AreEqual(plain.Length.ToString(), Header(received.Head, "Content-Length"));
        }
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task ZipBomb_IsRejectedWith413BeforeTheOriginSeesIt()
    {
        var bomb = Encode("gzip", new byte[40 * 1024 * 1024], CompressionLevel.SmallestSize);
        Assert.IsTrue(bomb.Length < 120_000, "the setup should compress far beyond the 100:1 default");
        await using var origin = AcceptingOrigin();

        var status = await PostAsync(origin, Decompression(), Post("gzip", bomb));

        Assert.AreEqual(413, status);
        Assert.AreEqual(0, origin.Requests.Count);
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task DecompressedSizeLimit_IsRejectedWith413()
    {
        var plain = Varied(300_000);
        await using var origin = AcceptingOrigin();

        var status = await PostAsync(origin, Decompression(maxBytes: 100_000), Post("gzip", Encode("gzip", plain)));

        Assert.AreEqual(413, status);
        Assert.AreEqual(0, origin.Requests.Count);
    }

    [DataTestMethod]
    [DataRow("gzip")]
    [DataRow("br")]
    [Timeout(30_000)]
    public async Task CorruptOrTruncatedBody_IsRejectedWith400(string encoding)
    {
        var good = Encode(encoding, Varied(50_000));
        await using var corruptOrigin = AcceptingOrigin();
        Assert.AreEqual(400,
            await PostAsync(corruptOrigin, Decompression(), Post(encoding, Encoding.ASCII.GetBytes(new string('x', 2000)))));
        Assert.AreEqual(0, corruptOrigin.Requests.Count);

        await using var truncatedOrigin = AcceptingOrigin();
        Assert.AreEqual(400,
            await PostAsync(truncatedOrigin, Decompression(), Post(encoding, good[..(good.Length / 2)])));
        Assert.AreEqual(0, truncatedOrigin.Requests.Count);
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task WhenDisabled_TheBodyIsForwardedAsIs()
    {
        var plain = Varied(20_000);
        var wire = Encode("gzip", plain);
        await using var origin = AcceptingOrigin();

        var status = await PostAsync(origin, null, Post("gzip", wire));

        Assert.AreEqual(200, status);
        Assert.IsTrue(origin.Requests.TryPeek(out var received));
        Assert.AreEqual("gzip", Header(received!.Head, "Content-Encoding"));
        CollectionAssert.AreEqual(wire, received.Body);
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task DecompressedBytes_AreRecordedInTheMetric()
    {
        var plain = Varied(80_000);
        long recorded = 0;
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == "Titanium.Web.Proxy" && instrument.Name == "twp.body.decompressed_bytes")
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((_, value, _, _) => Interlocked.Add(ref recorded, value));
        listener.Start();
        await using var origin = AcceptingOrigin();

        Assert.AreEqual(200, await PostAsync(origin, Decompression(), Post("gzip", Encode("gzip", plain))));
        listener.RecordObservableInstruments();

        Assert.AreEqual(plain.Length, Interlocked.Read(ref recorded));
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task EnablingEitherFeature_KeepsTheFullSessionPath()
    {
        // Terminate-lite (the byte-forwarding fast path) cannot compress or decompress; the proxy must fall back to
        // the full session path, which the tests above exercise end to end. Here: a streaming proxy answers a
        // request on the same listener a lite-eligible proxy would have taken.
        var plain = Varied(8192);
        await using var origin = new ScriptedOrigin(async (_, conn, _) =>
        {
            await conn.WriteAsync("HTTP/1.1 200 OK\r\nContent-Type: text/html\r\nContent-Length: " + plain.Length
                                  + "\r\n\r\n");
            await conn.WriteAsync(plain);
        });
        await using var proxy = await StartAsync(origin.Port, Routes(), ClientKind.H2Tls);
        proxy.Server.ResponseCompression = Streaming();
        proxy.Server.RequestDecompression = Decompression();

        using var response = await SendAsync(proxy, ClientKind.H2Tls, "/", [("Accept-Encoding", "gzip")]);

        CollectionAssert.AreEqual(new[] { "gzip" }, response.Content.Headers.ContentEncoding.ToArray());
        CollectionAssert.AreEqual(plain, Decode("gzip", await response.Content.ReadAsByteArrayAsync()));
    }

    // ---------------------------------------------------------------------------------------------------------
    // Raw socket plumbing
    // ---------------------------------------------------------------------------------------------------------

    private sealed record Received(string Head, byte[] Body);

    /// <summary>A buffered reader and writer over one TCP connection.</summary>
    private sealed class RawConnection : IDisposable
    {
        private readonly byte[] buffer = new byte[65536];
        private readonly CancellationTokenSource cts = new(TimeSpan.FromSeconds(60));
        private int position;
        private int length;

        public RawConnection(TcpClient client)
        {
            Client = client;
            Stream = client.GetStream();
        }

        public TcpClient Client { get; }

        public NetworkStream Stream { get; }

        public CancellationToken Token => cts.Token;

        public static async Task<RawConnection> ConnectAsync(int port, int receiveBuffer = 0)
        {
            var client = new TcpClient();
            if (receiveBuffer > 0)
            {
                client.ReceiveBufferSize = receiveBuffer;
            }

            await client.ConnectAsync(IPAddress.Loopback, port);
            return new RawConnection(client);
        }

        public void Dispose()
        {
            cts.Dispose();
            Client.Dispose();
        }

        public ValueTask WriteAsync(string text) => Stream.WriteAsync(Encoding.ASCII.GetBytes(text), Token);

        public ValueTask WriteAsync(byte[] bytes) => Stream.WriteAsync(bytes, Token);

        private async ValueTask<bool> FillAsync()
        {
            position = 0;
            length = await Stream.ReadAsync(buffer, Token);
            return length > 0;
        }

        private async ValueTask<int> ReadByteAsync()
        {
            if (position >= length && !await FillAsync())
            {
                return -1;
            }

            return buffer[position++];
        }

        /// <summary>One CRLF-terminated line without its terminator, or null at end of stream.</summary>
        public async Task<string?> ReadLineAsync()
        {
            var line = new StringBuilder();
            while (true)
            {
                var b = await ReadByteAsync();
                if (b < 0)
                {
                    return line.Length == 0 ? null : line.ToString();
                }

                if (b == '\n')
                {
                    return line.ToString().TrimEnd('\r');
                }

                line.Append((char)b);
            }
        }

        /// <summary>The start line and headers, each line ending in CRLF; null at end of stream.</summary>
        public async Task<string?> ReadHeadAsync()
        {
            var head = new StringBuilder();
            while (true)
            {
                var line = await ReadLineAsync();
                if (line is null)
                {
                    return head.Length == 0 ? null : head.ToString();
                }

                head.Append(line).Append("\r\n");
                if (line.Length == 0)
                {
                    return head.ToString();
                }
            }
        }

        public async Task<byte[]> ReadExactAsync(int count)
        {
            var result = new byte[count];
            var filled = 0;
            while (filled < count)
            {
                if (position >= length && !await FillAsync())
                {
                    throw new EndOfStreamException();
                }

                var take = Math.Min(count - filled, length - position);
                Buffer.BlockCopy(buffer, position, result, filled, take);
                position += take;
                filled += take;
            }

            return result;
        }

        /// <summary>The next chunk's data, or null after the terminal chunk and its trailers.</summary>
        public async Task<byte[]?> ReadChunkAsync()
        {
            var line = await ReadLineAsync() ?? throw new EndOfStreamException();
            var size = Convert.ToInt32(line.Split(';')[0].Trim(), 16);
            if (size == 0)
            {
                while (await ReadLineAsync() is { Length: > 0 })
                {
                }

                return null;
            }

            var data = await ReadExactAsync(size);
            await ReadLineAsync();
            return data;
        }

        public async Task<byte[]> ReadBodyAsync(string head, bool noBody = false)
        {
            if (noBody)
            {
                return [];
            }

            using var body = new MemoryStream();
            if (Header(head, "Transfer-Encoding").Contains("chunked", StringComparison.OrdinalIgnoreCase))
            {
                while (await ReadChunkAsync() is { } chunk)
                {
                    body.Write(chunk);
                }
            }
            else if (Header(head, "Content-Length") is { Length: > 0 } declared)
            {
                body.Write(await ReadExactAsync(int.Parse(declared)));
            }
            else
            {
                int b;
                while ((b = await ReadByteAsync()) >= 0)
                {
                    body.WriteByte((byte)b);
                }
            }

            return body.ToArray();
        }
    }

    /// <summary>
    ///     Origin that reads each request (head and body) and lets the test write whatever response it wants.
    ///     <see cref="Failures" /> counts handlers that died because the proxy closed the connection.
    /// </summary>
    private sealed class ScriptedOrigin : IAsyncDisposable
    {
        private readonly TcpListener listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource cts = new();
        private readonly Func<Received, RawConnection, CancellationToken, Task> respond;
        private readonly Task loop;
        private int failures;
        private int completed;

        public ScriptedOrigin(Func<Received, RawConnection, CancellationToken, Task> respond)
        {
            this.respond = respond;
            listener.Start();
            Port = ((IPEndPoint)listener.LocalEndpoint).Port;
            loop = AcceptAsync();
        }

        public int Port { get; }

        public ConcurrentQueue<Received> Requests { get; } = new();

        public int Failures => Volatile.Read(ref failures);

        public int Completed => Volatile.Read(ref completed);

        public async ValueTask DisposeAsync()
        {
            cts.Cancel();
            listener.Stop();
            try
            {
                await loop.WaitAsync(TimeSpan.FromSeconds(2));
            }
            catch
            {
                // The accept loop ends when the listener stops.
            }

            cts.Dispose();
        }

        private async Task AcceptAsync()
        {
            while (!cts.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await listener.AcceptTcpClientAsync(cts.Token);
                }
                catch
                {
                    return;
                }

                _ = ServeAsync(client);
            }
        }

        private async Task ServeAsync(TcpClient client)
        {
            using var conn = new RawConnection(client);
            try
            {
                while (!cts.IsCancellationRequested)
                {
                    var head = await conn.ReadHeadAsync();
                    if (head is null)
                    {
                        return;
                    }

                    var body = await conn.ReadBodyAsync(head, noBody: !HasRequestBody(head));
                    var request = new Received(head, body);
                    Requests.Enqueue(request);
                    await respond(request, conn, cts.Token);
                    await conn.Stream.FlushAsync(cts.Token);
                    Interlocked.Increment(ref completed);
                }
            }
            catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException)
            {
                Interlocked.Increment(ref failures);
            }
            catch
            {
                // Cancelled by the test shutting down.
            }
        }

        private static bool HasRequestBody(string head) =>
            Header(head, "Content-Length") is { Length: > 0 } and not "0"
            || Header(head, "Transfer-Encoding").Contains("chunked", StringComparison.OrdinalIgnoreCase);
    }
}
