using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Http3;

namespace Titanium.Web.Proxy.UnitTests;

/// <summary>
///     Stream-based <see cref="Http3VarInt.ReadAsync" /> coverage (span APIs are covered separately).
/// </summary>
[TestClass]
public class Http3VarIntStreamTests
{
    [TestMethod]
    [DataRow(0UL)]
    [DataRow(63UL)]
    [DataRow(64UL)]
    [DataRow(16383UL)]
    [DataRow(16384UL)]
    [DataRow(1UL << 30)]
    public async Task ReadAsync_RoundTripsEncodedValue(ulong value)
    {
        var buf = new byte[8];
        var written = Http3VarInt.Write(buf, value);
        await using var ms = new MemoryStream(buf, 0, written);

        var decoded = await Http3VarInt.ReadAsync(ms, CancellationToken.None);

        Assert.IsTrue(decoded.HasValue);
        Assert.AreEqual(value, decoded!.Value);
    }

    [TestMethod]
    public async Task ReadAsync_EmptyStream_ReturnsNull()
    {
        await using var ms = new MemoryStream();
        var decoded = await Http3VarInt.ReadAsync(ms, CancellationToken.None);
        Assert.IsNull(decoded);
    }

    [TestMethod]
    public async Task ReadAsync_TruncatedMultiByte_ReturnsNull()
    {
        // Prefix claims 2-byte encoding but only the first byte is present.
        await using var ms = new MemoryStream([0x40]);
        var decoded = await Http3VarInt.ReadAsync(ms, CancellationToken.None);
        Assert.IsNull(decoded);
    }

    [TestMethod]
    public void GetByteCount_OverMax_Throws()
    {
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => Http3VarInt.GetByteCount(Http3VarInt.Max8ByteValue + 1));
    }
}
