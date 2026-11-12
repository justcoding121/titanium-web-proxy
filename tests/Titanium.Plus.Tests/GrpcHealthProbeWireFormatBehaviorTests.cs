using System.Buffers.Binary;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Plus.Clusters;

namespace Titanium.Plus.Tests;

/// <summary>
///     Wire-format leftovers for <see cref="GrpcHealthProbe.TryParseResponse"/>: skip unknown fields,
///     short length-delimited payloads, and unsupported wire types.
/// </summary>
[TestClass]
public class GrpcHealthProbeWireFormatBehaviorTests
{
    [TestMethod]
    public void TryParseResponse_SkipsUnknownFields_BeforeStatus()
    {
        // field 2 = fixed64 (wire 1), then field 1 = varint status=SERVING
        var message = new byte[]
        {
            0x11, 1, 2, 3, 4, 5, 6, 7, 8, // tag 2<<3|1, 8 bytes
            0x08, 0x01,                   // tag 1<<3|0, status 1
        };
        var body = Wrap(message);
        Assert.IsTrue(GrpcHealthProbe.TryParseResponse(body, 0, out var serving));
        Assert.IsTrue(serving);
    }

    [TestMethod]
    public void TryParseResponse_SkipsLengthDelimitedAndFixed32Fields()
    {
        var message = new byte[]
        {
            0x12, 0x03, (byte)'a', (byte)'b', (byte)'c', // field 2, wire 2, len 3
            0x1D, 9, 8, 7, 6,                             // field 3, wire 5, 4 bytes
            0x08, 0x01,
        };
        Assert.IsTrue(GrpcHealthProbe.TryParseResponse(Wrap(message), 0, out var serving));
        Assert.IsTrue(serving);
    }

    [TestMethod]
    public void TryParseResponse_TruncatedSkipPayload_Fails()
    {
        var shortFixed64 = new byte[] { 0x11, 1, 2, 3 }; // needs 8 bytes after tag
        Assert.IsFalse(GrpcHealthProbe.TryParseResponse(Wrap(shortFixed64), 0, out _));

        var shortLen = new byte[] { 0x12, 0x05, 1, 2 }; // claims 5 bytes, only 2 present
        Assert.IsFalse(GrpcHealthProbe.TryParseResponse(Wrap(shortLen), 0, out _));
    }

    [TestMethod]
    public void TryParseResponse_UnsupportedWireType_Fails()
    {
        // field 1 with wire type 3 (start group) is unsupported
        var message = new byte[] { 0x0B };
        Assert.IsFalse(GrpcHealthProbe.TryParseResponse(Wrap(message), 0, out _));
    }

    [TestMethod]
    public void TryParseResponse_OnlyUnknownFields_FailsWithoutStatus()
    {
        var message = new byte[] { 0x12, 0x01, (byte)'x' };
        Assert.IsFalse(GrpcHealthProbe.TryParseResponse(Wrap(message), 0, out _));
    }

    private static byte[] Wrap(ReadOnlySpan<byte> message)
    {
        var body = new byte[5 + message.Length];
        body[0] = 0;
        BinaryPrimitives.WriteUInt32BigEndian(body.AsSpan(1), (uint)message.Length);
        message.CopyTo(body.AsSpan(5));
        return body;
    }
}
