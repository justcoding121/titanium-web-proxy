using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Inspector.Services;
using Titanium.Web.Proxy.Http;
using Titanium.Web.Proxy.Models;

namespace Titanium.Inspector.Tests;

[TestClass]
public class ProgressiveDeliveryTests
{
    [TestMethod]
    public void Eligibility_Table()
    {
        var body = new byte[ProgressiveDelivery.MinWireBytes];
        var ok = ResponseOf(200, body, "Set-Cookie", "a=1");
        ok.Headers.AddHeader(new HttpHeader("Set-Cookie", "b=2"));
        Assert.AreEqual(ProgressiveSkipReason.None, Check(ok, body));

        Assert.AreEqual(ProgressiveSkipReason.Head, Check(ok, body, method: "HEAD"));
        Assert.AreEqual(ProgressiveSkipReason.Status, Check(ResponseOf(204, body), body));
        Assert.AreEqual(ProgressiveSkipReason.Status, Check(ResponseOf(304, body), body));
        Assert.AreEqual(ProgressiveSkipReason.Status, Check(ResponseOf(100, body), body));
        Assert.AreEqual(ProgressiveSkipReason.TooSmall, Check(ResponseOf(200, new byte[32]), new byte[32], wire: 32));
        Assert.AreEqual(ProgressiveSkipReason.NoBody, Check(ResponseOf(200, []), []));
        Assert.AreEqual(ProgressiveSkipReason.Edited, Check(ok, body, edited: true));
        Assert.AreEqual(ProgressiveSkipReason.Transcoded, Check(ok, body, transcoded: true));
        Assert.AreEqual(ProgressiveSkipReason.Disabled, Check(ok, body, enabled: false));

        var upgrade = ResponseOf(200, body);
        upgrade.Headers.AddHeader(new HttpHeader("Upgrade", "websocket"));
        Assert.AreEqual(ProgressiveSkipReason.Upgrade, Check(upgrade, body));

        var trailers = ResponseOf(200, body);
        trailers.Headers.AddHeader(new HttpHeader("Trailer", "Checksum"));
        Assert.AreEqual(ProgressiveSkipReason.Trailers, Check(trailers, body));

        var unknown = ResponseOf(200, body);
        unknown.Headers.AddHeader(new HttpHeader("Content-Encoding", "zstd"));
        Assert.AreEqual(ProgressiveSkipReason.Encoding, Check(unknown, body));
    }

    [TestMethod]
    public void Build_CopiesDuplicateHeaders_AndRecompressesGzip()
    {
        var plain = Encoding.UTF8.GetBytes(new string('x', ProgressiveDelivery.MinWireBytes));
        var origin = ResponseOf(200, plain);
        origin.Headers.AddHeader(new HttpHeader("Content-Encoding", "gzip"));
        origin.Headers.AddHeader(new HttpHeader("Set-Cookie", "a=1"));
        origin.Headers.AddHeader(new HttpHeader("Set-Cookie", "b=2"));

        var plan = ProgressiveDelivery.TryBuild(origin, RequestOf("GET"), plain, plain.Length, false, false, true);
        Assert.IsNotNull(plan);
        Assert.AreEqual(2, plan!.Response.Headers.Count(h => h.Name.Equals("Set-Cookie", StringComparison.OrdinalIgnoreCase)));
        Assert.AreEqual("gzip", plan.Response.ContentEncoding);
        Assert.AreEqual(plan.Wire.Length, plan.Response.ContentLength);
        using var gzip = new GZipStream(new MemoryStream(plan.Wire), CompressionMode.Decompress);
        using var decoded = new MemoryStream();
        gzip.CopyTo(decoded);
        CollectionAssert.AreEqual(plain, decoded.ToArray());
        Assert.AreEqual(plain.Length, origin.Body.Length);
    }

    [TestMethod]
    public async Task Writer_WritesExactBytes_InSlices_AndChargesWireSize()
    {
        var wire = new byte[40];
        for (var i = 0; i < wire.Length; i++)
        {
            wire[i] = (byte)i;
        }

        var delays = new List<TimeSpan>();
        using var paced = new LinkShaper(
            new NetworkThrottleProfile("t", 0, 1_000),
            new ManualTime(),
            (span, _) =>
            {
                delays.Add(span);
                return Task.CompletedTask;
            });
        var dest = new MemoryStream();
        await ProgressiveDelivery.WritePacedAsync(dest, wire, chargeWireBytes: 100, () => paced, CancellationToken.None, sliceBytes: 10);
        CollectionAssert.AreEqual(wire, dest.ToArray());
        Assert.AreEqual(4, delays.Count);
        Assert.AreEqual(TimeSpan.FromMilliseconds(100), delays[^1]);
    }

    [TestMethod]
    public async Task Writer_EmptyBody_DoesNotReserve()
    {
        var called = false;
        using var shaper = new LinkShaper(
            new NetworkThrottleProfile("t", 0, 1_000),
            delay: (_, _) =>
            {
                called = true;
                return Task.CompletedTask;
            });
        await ProgressiveDelivery.WritePacedAsync(new MemoryStream(), [], 0, () => shaper, CancellationToken.None);
        Assert.IsFalse(called);
    }

    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    ///     Release gate 6: recompression cost. The body is already buffered (32 MiB cap), so the extra memory is
    ///     one compressed copy plus encoder buffers, and the CPU cost is a single Fastest-level pass.
    /// </summary>
    [TestMethod]
    [DataRow("gzip")]
    [DataRow("deflate")]
    [DataRow("br")]
    public void Recompression_CostIsBounded_ForTheLargestBufferedBody(string encoding)
    {
        const int plainBytes = 32 * 1024 * 1024;
        var plain = new byte[plainBytes];
        var random = new Random(7);
        var text = Encoding.ASCII.GetBytes("The quick brown fox jumps over the lazy dog. ");
        for (var i = 0; i < plain.Length; i += text.Length)
        {
            Buffer.BlockCopy(text, 0, plain, i, Math.Min(text.Length, plain.Length - i));
        }

        // Mix in incompressible runs so the encoder cannot take only the cheap path.
        for (var offset = 0; offset < plain.Length; offset += 1024 * 1024)
        {
            random.NextBytes(plain.AsSpan(offset, 64 * 1024));
        }

        var response = new Response { StatusCode = 200, HttpVersion = new Version(1, 1) };
        response.Headers.AddHeader("Content-Encoding", encoding);
        var request = new Request { Method = "GET" };

        GC.Collect();
        var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        var watch = Stopwatch.StartNew();
        var plan = ProgressiveDelivery.TryBuild(response, request, plain, plainBytes, false, false, true);
        watch.Stop();
        var allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;

        Assert.IsNotNull(plan);
        TestContext.WriteLine(
            $"{encoding}: 32 MiB -> {plan!.Wire.Length / 1024} KiB wire in {watch.ElapsedMilliseconds} ms, " +
            $"allocated {allocated / (1024 * 1024)} MiB");

        // Pooled working buffer plus the exact-size compressed copy.
        Assert.IsTrue(allocated < 2L * plainBytes + (4 << 20), $"Allocated {allocated} bytes for a {plainBytes} byte body.");
        Assert.IsTrue(watch.Elapsed < TimeSpan.FromSeconds(15), $"Recompression took {watch.Elapsed}.");
    }

    [TestMethod]
    [DataRow("gzip")]
    [DataRow("br")]
    public void Recompression_WorstCase_IncompressibleLargestBody_StaysBounded(string encoding)
    {
        const int plainBytes = 32 * 1024 * 1024;
        var plain = new byte[plainBytes];
        new Random(11).NextBytes(plain);
        var response = new Response { StatusCode = 200, HttpVersion = new Version(1, 1) };
        response.Headers.AddHeader("Content-Encoding", encoding);
        var request = new Request { Method = "GET" };

        GC.Collect();
        var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        var watch = Stopwatch.StartNew();
        var plan = ProgressiveDelivery.TryBuild(response, request, plain, plainBytes, false, false, true);
        watch.Stop();
        var allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;

        Assert.IsNotNull(plan);
        TestContext.WriteLine(
            $"{encoding} incompressible: 32 MiB -> {plan!.Wire.Length / (1024 * 1024)} MiB wire in " +
            $"{watch.ElapsedMilliseconds} ms, allocated {allocated / (1024 * 1024)} MiB");
        // Pooled working buffer sized for the worst case, plus the one exact-size compressed copy.
        Assert.IsTrue(allocated < 2L * plainBytes + (4 << 20), $"Allocated {allocated} bytes for a {plainBytes} byte body.");
        Assert.IsTrue(watch.Elapsed < TimeSpan.FromSeconds(15), $"Recompression took {watch.Elapsed}.");
    }

    [TestMethod]
    public void AppendVia_AddsOnce_AndExtendsAnExistingChain()
    {
        var headers = new HeaderCollection();
        ProgressiveDelivery.AppendVia(headers, new Version(1, 1), "titanium-web-proxy");
        ProgressiveDelivery.AppendVia(headers, new Version(1, 1), "titanium-web-proxy");
        Assert.AreEqual("1.1 titanium-web-proxy", headers.GetHeaders("Via")!.Single().Value);

        var chained = new HeaderCollection();
        chained.AddHeader("Via", "1.0 upstream");
        ProgressiveDelivery.AppendVia(chained, new Version(2, 0), "titanium-web-proxy");
        Assert.AreEqual("1.0 upstream, 2.0 titanium-web-proxy", chained.GetHeaders("Via")!.Single().Value);
    }

    [TestMethod]
    public void MarkConnectionClose_ReplacesKeepAliveHeaders()
    {
        var headers = new HeaderCollection();
        headers.AddHeader("Connection", "keep-alive");
        headers.AddHeader("Keep-Alive", "timeout=5");
        ProgressiveDelivery.MarkConnectionClose(headers);
        Assert.AreEqual("close", headers.GetHeaders("Connection")!.Single().Value);
        Assert.IsNull(headers.GetHeaders("Keep-Alive"));
    }

    [TestMethod]
    public void Evaluate_BodiesUnderTheThreshold_AreLeftToTheBurstPath()
    {
        var response = new Response { StatusCode = 200, HttpVersion = new Version(1, 1) };
        var request = new Request { Method = "GET" };
        Assert.AreEqual(
            ProgressiveSkipReason.TooSmall,
            ProgressiveDelivery.Evaluate(response, request, new byte[500], 500, false, false, true));
        Assert.AreEqual(
            ProgressiveSkipReason.None,
            ProgressiveDelivery.Evaluate(response, request, new byte[70 * 1024], 70 * 1024, false, false, true));
    }

    [TestMethod]
    public async Task Writer_CallerCancel_Stops()
    {
        using var shaper = new LinkShaper(
            NetworkThrottle.Slow3G,
            delay: (_, token) => Task.Delay(Timeout.Infinite, token));
        using var cts = new CancellationTokenSource();
        var writing = ProgressiveDelivery.WritePacedAsync(
            new MemoryStream(), new byte[100], 100, () => shaper, cts.Token, sliceBytes: 10);
        cts.Cancel();
        await Assert.ThrowsExactlyAsync<TaskCanceledException>(async () => await writing);
    }

    [TestMethod]
    public async Task Writer_EpochCancel_FinishesTheBody()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var shaper = new LinkShaper(
            NetworkThrottle.Slow3G,
            delay: (_, token) =>
            {
                gate.TrySetResult();
                return Task.Delay(Timeout.Infinite, token);
            });
        var dest = new MemoryStream();
        var writing = ProgressiveDelivery.WritePacedAsync(
            dest, new byte[30], 30, () => shaper, CancellationToken.None, sliceBytes: 10);
        await gate.Task.WaitAsync(TimeSpan.FromSeconds(2));
        shaper.ReleaseWaiters();
        await writing.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.AreEqual(30, dest.Length);
    }

    private static ProgressiveSkipReason Check(
        Response response,
        byte[] body,
        string method = "GET",
        long? wire = null,
        bool transcoded = false,
        bool edited = false,
        bool enabled = true) =>
        ProgressiveDelivery.Evaluate(
            response,
            RequestOf(method),
            body,
            wire ?? body.Length,
            transcoded,
            edited,
            enabled);

    private static Request RequestOf(string method) => new() { Method = method };

    private static Response ResponseOf(int status, byte[] body, string? headerName = null, string? headerValue = null)
    {
        var response = new Response
        {
            StatusCode = status,
            StatusDescription = "x",
            HttpVersion = new Version(1, 1),
            Body = body,
        };
        if (headerName is not null)
        {
            response.Headers.AddHeader(new HttpHeader(headerName, headerValue ?? ""));
        }

        return response;
    }

    private sealed class ManualTime : TimeProvider
    {
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => 0;
    }
}
