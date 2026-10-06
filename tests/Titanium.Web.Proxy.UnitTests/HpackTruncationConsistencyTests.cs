using System.Collections.Generic;
using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Extensions;
using Titanium.Web.Proxy.Http2.Hpack;
using Titanium.Web.Proxy.Models;

namespace Titanium.Web.Proxy.UnitTests;

/// <summary>
///     A truncated header block must leave the HPACK dynamic table usable. The next block on the
///     same connection can reference entries inserted before the truncation.
/// </summary>
[TestClass]
public class HpackTruncationConsistencyTests
{
    [TestMethod]
    public void TruncatedBlock_KeepsEarlierDynamicEntries_ForTheNextBlock()
    {
        var encoder = new Encoder(4096);
        var first = Encode(encoder,
            ("x-session", "alpha"),
            ("x-huge", new string('v', 200)));

        var headers = new List<(string Name, string Value)>();
        var decoder = new Decoder(maxHeaderSize: 64, maxHeaderTableSize: 4096);
        decoder.Decode(first, new CollectingListener(headers));
        Assert.IsTrue(decoder.EndHeaderBlock(), "the oversized literal must report truncation");
        Assert.IsTrue(headers.Exists(h => h.Name == "x-session" && h.Value == "alpha"));
        Assert.IsFalse(headers.Exists(h => h.Name == "x-huge"));

        var second = Encode(encoder, ("x-session", "alpha"));
        headers.Clear();
        decoder.Decode(second, new CollectingListener(headers));
        Assert.IsFalse(decoder.EndHeaderBlock());
        Assert.AreEqual(1, headers.Count);
        Assert.AreEqual("x-session", headers[0].Name);
        Assert.AreEqual("alpha", headers[0].Value);
    }

    private static byte[] Encode(Encoder encoder, params (string Name, string Value)[] fields)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        foreach (var (name, value) in fields)
            encoder.EncodeHeader(writer, (ByteString)name, (ByteString)value);
        writer.Flush();
        return stream.ToArray();
    }

    private sealed class CollectingListener(List<(string Name, string Value)> headers) : IHeaderListener
    {
        public void AddHeader(ByteString name, ByteString value, bool sensitive) =>
            headers.Add((name.GetString(), value.GetString()));
    }
}
