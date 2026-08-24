using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Http3;

namespace Titanium.Web.Proxy.UnitTests;

/// <summary>
///     Unit coverage for HTTP/3 frame encode/decode over <see cref="Stream" /> (RFC 9114 §7.1).
/// </summary>
[TestClass]
public class Http3FrameTests
{
    [TestMethod]
    public async Task WriteThenRead_RoundTripsTypeAndPayload()
    {
        await using var ms = new MemoryStream();
        var payload = new byte[] { 0x01, 0x02, 0x03 };

        await Http3Frame.WriteAsync(ms, Http3FrameType.Data, payload, CancellationToken.None);
        ms.Position = 0;

        var frame = await Http3Frame.ReadAsync(ms, maxPayloadBytes: 1024, CancellationToken.None);

        Assert.IsNotNull(frame);
        Assert.AreEqual(Http3FrameType.Data, frame!.Type);
        CollectionAssert.AreEqual(payload, frame.Payload.ToArray());
    }

    [TestMethod]
    public async Task WriteThenRead_ZeroPayloadFrame_RoundTrips()
    {
        await using var ms = new MemoryStream();

        await Http3Frame.WriteAsync(ms, Http3FrameType.Settings, CancellationToken.None);
        ms.Position = 0;

        var frame = await Http3Frame.ReadAsync(ms, maxPayloadBytes: 0, CancellationToken.None);

        Assert.IsNotNull(frame);
        Assert.AreEqual(Http3FrameType.Settings, frame!.Type);
        Assert.AreEqual(0, frame.Payload.Length);
    }

    [TestMethod]
    public async Task ReadAsync_EmptyStream_ReturnsNull()
    {
        await using var ms = new MemoryStream();
        var frame = await Http3Frame.ReadAsync(ms, maxPayloadBytes: 1024, CancellationToken.None);
        Assert.IsNull(frame);
    }

    [TestMethod]
    public async Task ReadAsync_OversizedPayload_ThrowsExcessiveLoad()
    {
        await using var ms = new MemoryStream();
        var payload = new byte[64];
        await Http3Frame.WriteAsync(ms, Http3FrameType.Data, payload, CancellationToken.None);
        ms.Position = 0;

        var ex = await Assert.ThrowsExactlyAsync<Http3ConnectionException>(
            () => Http3Frame.ReadAsync(ms, maxPayloadBytes: 16, CancellationToken.None).AsTask());

        Assert.AreEqual(Http3ErrorCode.ExcessiveLoad, ex.ErrorCode);
    }

    [TestMethod]
    public async Task ReadAsync_TruncatedPayload_ThrowsFrameError()
    {
        await using var ms = new MemoryStream();
        // Type=DATA (0), Length=10, but only 3 payload bytes written.
        ms.WriteByte(0x00); // type
        ms.WriteByte(0x0A); // length 10
        ms.Write(new byte[] { 1, 2, 3 });
        ms.Position = 0;

        var ex = await Assert.ThrowsExactlyAsync<Http3ConnectionException>(
            () => Http3Frame.ReadAsync(ms, maxPayloadBytes: 0, CancellationToken.None).AsTask());

        Assert.AreEqual(Http3ErrorCode.FrameError, ex.ErrorCode);
    }

    [TestMethod]
    public async Task ReadAsync_TruncatedAfterType_ThrowsFrameError()
    {
        await using var ms = new MemoryStream();
        ms.WriteByte(0x00); // type only — length missing
        ms.Position = 0;

        var ex = await Assert.ThrowsExactlyAsync<Http3ConnectionException>(
            () => Http3Frame.ReadAsync(ms, maxPayloadBytes: 0, CancellationToken.None).AsTask());

        Assert.AreEqual(Http3ErrorCode.FrameError, ex.ErrorCode);
    }

    [TestMethod]
    public async Task WriteThenRead_HeadersFrame_PreservesQpackBytes()
    {
        await using var ms = new MemoryStream();
        var qpack = new byte[] { 0x00, 0x00, 0xD1 }; // typical empty RIC/base + indexed status

        await Http3Frame.WriteAsync(ms, Http3FrameType.Headers, qpack, CancellationToken.None);
        ms.Position = 0;

        var frame = await Http3Frame.ReadAsync(ms, maxPayloadBytes: 4096, CancellationToken.None);

        Assert.IsNotNull(frame);
        Assert.AreEqual(Http3FrameType.Headers, frame!.Type);
        CollectionAssert.AreEqual(qpack, frame.Payload.ToArray());
        frame.ReturnPayload();
        frame.ReturnPayload(); // idempotent
    }

    [TestMethod]
    public async Task WriteAsync_LargePayload_UsesHeaderThenBodyWrites()
    {
        await using var ms = new MemoryStream();
        var payload = new byte[512];
        payload.AsSpan().Fill(0xAB);

        await Http3Frame.WriteAsync(ms, Http3FrameType.Data, payload, CancellationToken.None);
        ms.Position = 0;

        var frame = await Http3Frame.ReadAsync(ms, maxPayloadBytes: 1024, CancellationToken.None);

        Assert.IsNotNull(frame);
        Assert.AreEqual(Http3FrameType.Data, frame!.Type);
        Assert.AreEqual(512, frame.Payload.Length);
        Assert.AreEqual(0xAB, frame.Payload.Span[0]);
        frame.ReturnPayload();
    }

    [TestMethod]
    public async Task WriteHeadersAndDataAsync_CoalescesIntoReadableFrames()
    {
        await using var ms = new MemoryStream();
        var headers = new byte[] { 0x00, 0x00, 0xD1 };
        var data = new byte[] { 1, 2, 3, 4, 5 };

        await Http3Frame.WriteHeadersAndDataAsync(ms, headers, data, CancellationToken.None);
        ms.Position = 0;

        var headersFrame = await Http3Frame.ReadAsync(ms, maxPayloadBytes: 4096, CancellationToken.None);
        var dataFrame = await Http3Frame.ReadAsync(ms, maxPayloadBytes: 4096, CancellationToken.None);

        Assert.IsNotNull(headersFrame);
        Assert.AreEqual(Http3FrameType.Headers, headersFrame!.Type);
        CollectionAssert.AreEqual(headers, headersFrame.Payload.ToArray());
        Assert.IsNotNull(dataFrame);
        Assert.AreEqual(Http3FrameType.Data, dataFrame!.Type);
        CollectionAssert.AreEqual(data, dataFrame.Payload.ToArray());
        headersFrame.ReturnPayload();
        dataFrame.ReturnPayload();
    }
}
