using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Extensions;
using Titanium.Web.Proxy.Helpers;
using Titanium.Web.Proxy.Http;
using Titanium.Web.Proxy.Http2.Hpack;
using Titanium.Web.Proxy.Http3.Qpack;
using Titanium.Web.Proxy.Models;

namespace Titanium.Web.Proxy.UnitTests;

[TestClass]
public class MitmStaticRebuildHelperTests
{
    [TestMethod]
    public void DropOneUniqueHeader_AllowsDropOnlyDiff()
    {
        var before = new HeaderCollection();
        before.AddHeader("accept", "*/*");
        before.AddHeader("user-agent", "test");
        var baseline = MitmCompressedRelayHelper.HeaderRelayBaseline.Capture(before);

        var after = new HeaderCollection();
        after.AddHeader("accept", "*/*");

        Assert.IsTrue(baseline.TryDiffDropOnly(after, MitmCompressedRelayHelper.DefaultMaxDrops, out var dropped));
        Assert.AreEqual(1, dropped.Count);
        Assert.AreEqual("user-agent", dropped[0]);
    }

    [TestMethod]
    public void DropTwoUniqueHeaders_AllowsDropOnlyDiff()
    {
        var before = new HeaderCollection();
        before.AddHeader("accept", "*/*");
        before.AddHeader("user-agent", "test");
        before.AddHeader("referer", "https://example.com");
        var baseline = MitmCompressedRelayHelper.HeaderRelayBaseline.Capture(before);

        var after = new HeaderCollection();
        after.AddHeader("accept", "*/*");

        Assert.IsTrue(baseline.TryDiffDropOnly(after, MitmCompressedRelayHelper.DefaultMaxDrops, out var dropped));
        Assert.AreEqual(2, dropped.Count);
    }

    [TestMethod]
    public void DropFiveUniqueHeaders_Rejected()
    {
        var before = new HeaderCollection();
        for (var i = 0; i < 6; i++)
            before.AddHeader($"X-H{i}", "v");
        var baseline = MitmCompressedRelayHelper.HeaderRelayBaseline.Capture(before);

        var after = new HeaderCollection();
        after.AddHeader("X-H0", "v");

        Assert.IsFalse(baseline.TryDiffDropOnly(after, MitmCompressedRelayHelper.DefaultMaxDrops, out _));
    }

    [TestMethod]
    public void DropAndAdd_Rejected()
    {
        var before = new HeaderCollection();
        before.AddHeader("accept", "*/*");
        before.AddHeader("user-agent", "test");
        var baseline = MitmCompressedRelayHelper.HeaderRelayBaseline.Capture(before);

        var after = new HeaderCollection();
        after.AddHeader("accept", "*/*");
        after.AddHeader("X-New", "1");

        Assert.IsFalse(baseline.TryDiffDropOnly(after, MitmCompressedRelayHelper.DefaultMaxDrops, out _));
    }

    [TestMethod]
    public void ModifyValue_Rejected()
    {
        var before = new HeaderCollection();
        before.AddHeader("accept", "*/*");
        before.AddHeader("user-agent", "test");
        var baseline = MitmCompressedRelayHelper.HeaderRelayBaseline.Capture(before);

        var after = new HeaderCollection();
        after.AddHeader("accept", "text/plain");
        after.AddHeader("user-agent", "test");

        Assert.IsFalse(baseline.TryDiffDropOnly(after, MitmCompressedRelayHelper.DefaultMaxDrops, out _));
    }

    [TestMethod]
    public void RebuildStaticHpackBlock_DropOneHeader_MatchesManualEncode()
    {
        var original = EncodeStaticHpack(
            (StaticTable.KnownHeaderMethod, (ByteString)"GET"),
            ((ByteString)"accept", (ByteString)"*/*"),
            ((ByteString)"user-agent", (ByteString)"test"));

        Assert.IsTrue(MitmStaticRebuildHelper.IsStaticOnlyHpackBlock(original));

        var dropped = default(MitmCompressedRelayHelper.DroppedNameBuffer);
        dropped.Add("user-agent");
        Assert.IsTrue(MitmStaticRebuildHelper.TryRebuildStaticHpackBlock(original, dropped, out var rebuilt));

        var expected = EncodeStaticHpack(
            (StaticTable.KnownHeaderMethod, (ByteString)"GET"),
            ((ByteString)"accept", (ByteString)"*/*"));

        Assert.IsTrue(DecodeHpack(expected).SetEquals(DecodeHpack(rebuilt)));
    }

    [TestMethod]
    public void RebuildStaticHpackBlock_DropTwoHeaders_RoundTrips()
    {
        var original = EncodeStaticHpack(
            (StaticTable.KnownHeaderMethod, (ByteString)"GET"),
            ((ByteString)"accept", (ByteString)"*/*"),
            ((ByteString)"user-agent", (ByteString)"test"),
            ((ByteString)"referer", (ByteString)"https://example.com"));

        var dropped = default(MitmCompressedRelayHelper.DroppedNameBuffer);
        dropped.Add("user-agent");
        dropped.Add("referer");
        Assert.IsTrue(MitmStaticRebuildHelper.TryRebuildStaticHpackBlock(original, dropped, out var rebuilt));

        var decoded = DecodeHpack(rebuilt);
        Assert.AreEqual(2, decoded.Count);
        Assert.IsTrue(decoded.Contains((":method", "GET")));
        Assert.IsTrue(decoded.Contains(("accept", "*/*")));
    }

    [TestMethod]
    public void RebuildStaticQpackBlock_DropOneHeader_RoundTrips()
    {
        var original = QpackEncoder.Encode(new[]
        {
            (":method", "GET"),
            ("accept", "*/*"),
            ("user-agent", "test")
        });

        Assert.IsTrue(MitmStaticRebuildHelper.IsStaticOnlyQpackBlock(original));

        var dropped = default(MitmCompressedRelayHelper.DroppedNameBuffer);
        dropped.Add("user-agent");
        Assert.IsTrue(MitmStaticRebuildHelper.TryRebuildStaticQpackBlock(original, dropped, out var rebuilt));

        var decoded = QpackDecoder.Decode(rebuilt);
        Assert.AreEqual(2, decoded.Count);
        Assert.IsTrue(decoded.Exists(h => h.Name == ":method" && h.Value == "GET"));
        Assert.IsTrue(decoded.Exists(h => h.Name == "accept" && h.Value == "*/*"));
        Assert.IsFalse(decoded.Exists(h => h.Name == "user-agent"));
    }

    [TestMethod]
    public void TryPrepareStaticQpackRelay_AppendOnly_ReturnsOriginalBlock()
    {
        var original = QpackEncoder.Encode(new[]
        {
            (":method", "GET"),
            ("accept", "*/*")
        });

        var before = new HeaderCollection();
        before.AddHeader("accept", "*/*");
        var baseline = MitmCompressedRelayHelper.HeaderRelayBaseline.Capture(before);

        var after = new HeaderCollection();
        after.AddHeader("accept", "*/*");
        after.AddHeader("X-Track", "1");

        Assert.IsTrue(MitmStaticRebuildHelper.TryPrepareStaticQpackRelay(
            original, baseline, after, out var block, out var added));
        Assert.AreSame(original, block);
        Assert.AreEqual(1, added.Count);
    }

    [TestMethod]
    public void TryPrepareStaticQpackRelay_DropOnly_RebuildsBlock()
    {
        var original = QpackEncoder.Encode(new[]
        {
            (":method", "GET"),
            ("accept", "*/*"),
            ("user-agent", "test")
        });

        var before = new HeaderCollection();
        before.AddHeader("accept", "*/*");
        before.AddHeader("user-agent", "test");
        var baseline = MitmCompressedRelayHelper.HeaderRelayBaseline.Capture(before);

        var after = new HeaderCollection();
        after.AddHeader("accept", "*/*");

        Assert.IsTrue(MitmStaticRebuildHelper.TryPrepareStaticQpackRelay(
            original, baseline, after, out var block, out var added));
        Assert.AreNotSame(original, block);
        Assert.AreEqual(0, added.Count);
        var decoded = QpackDecoder.Decode(block);
        Assert.AreEqual(2, decoded.Count);
        Assert.IsFalse(decoded.Exists(h => h.Name == "user-agent"));
    }

    [TestMethod]
    public void NonUniqueSnapshot_TrailingAppend_AllowsRelay()
    {
        var before = new HeaderCollection();
        before.AddHeader("set-cookie", "a=1");
        before.AddHeader("set-cookie", "b=2");
        var baseline = MitmCompressedRelayHelper.HeaderRelayBaseline.Capture(before);

        var after = new HeaderCollection();
        after.AddHeader("set-cookie", "a=1");
        after.AddHeader("set-cookie", "b=2");
        after.AddHeader("set-cookie", "c=3");

        Assert.IsTrue(baseline.TryDiffAppendOnly(
            after, MitmCompressedRelayHelper.DefaultMaxAppendHeaders, out var added));
        Assert.AreEqual(1, added.Count);
        Assert.AreEqual("c=3", added[0].Value);
    }

    [TestMethod]
    public void TryPrepareStaticHpackRelay_DropOnly_RebuildsBlock()
    {
        var original = EncodeStaticHpack(
            (StaticTable.KnownHeaderMethod, (ByteString)"GET"),
            ((ByteString)"accept", (ByteString)"*/*"),
            ((ByteString)"user-agent", (ByteString)"test"));

        var before = new HeaderCollection();
        before.AddHeader("accept", "*/*");
        before.AddHeader("user-agent", "test");
        var baseline = MitmCompressedRelayHelper.HeaderRelayBaseline.Capture(before);

        var after = new HeaderCollection();
        after.AddHeader("accept", "*/*");

        Assert.IsTrue(MitmStaticRebuildHelper.TryPrepareStaticHpackRelay(
            original, baseline, after, out var block, out var added));
        Assert.AreNotSame(original, block);
        Assert.AreEqual(0, added.Count);
    }

    [TestMethod]
    public void TryPrepareStaticHpackRelay_ModifyValue_Rejected()
    {
        var original = EncodeStaticHpack(
            (StaticTable.KnownHeaderMethod, (ByteString)"GET"),
            ((ByteString)"accept", (ByteString)"*/*"));

        var before = new HeaderCollection();
        before.AddHeader("accept", "*/*");
        var baseline = MitmCompressedRelayHelper.HeaderRelayBaseline.Capture(before);

        var after = new HeaderCollection();
        after.AddHeader("accept", "text/plain");

        Assert.IsFalse(MitmStaticRebuildHelper.TryPrepareStaticHpackRelay(
            original, baseline, after, out _, out _));
    }

    [TestMethod]
    public void DropOnly_UnchangedHeaders_ReturnsFalse()
    {
        var headers = new HeaderCollection();
        headers.AddHeader("accept", "*/*");
        var baseline = MitmCompressedRelayHelper.HeaderRelayBaseline.Capture(headers);
        Assert.IsFalse(baseline.TryDiffDropOnly(headers, MitmCompressedRelayHelper.DefaultMaxDrops, out _));
    }

    [TestMethod]
    public void NonUnique_RemoveFromList_Rejected()
    {
        var before = new HeaderCollection();
        before.AddHeader("set-cookie", "a=1");
        before.AddHeader("set-cookie", "b=2");
        var baseline = MitmCompressedRelayHelper.HeaderRelayBaseline.Capture(before);

        var after = new HeaderCollection();
        after.AddHeader("set-cookie", "a=1");

        Assert.IsFalse(baseline.TryDiffAppendOnly(
            after, MitmCompressedRelayHelper.DefaultMaxAppendHeaders, out _));
    }

    [TestMethod]
    public void RebuildStaticHpackBlock_DropFourHeaders_RoundTrips()
    {
        var original = EncodeStaticHpack(
            (StaticTable.KnownHeaderMethod, (ByteString)"GET"),
            ((ByteString)"h1", (ByteString)"1"),
            ((ByteString)"h2", (ByteString)"2"),
            ((ByteString)"h3", (ByteString)"3"),
            ((ByteString)"h4", (ByteString)"4"),
            ((ByteString)"h5", (ByteString)"5"));

        var dropped = default(MitmCompressedRelayHelper.DroppedNameBuffer);
        dropped.Add("h2");
        dropped.Add("h4");
        Assert.IsTrue(MitmStaticRebuildHelper.TryRebuildStaticHpackBlock(original, dropped, out var rebuilt));

        var decoded = DecodeHpack(rebuilt);
        Assert.AreEqual(4, decoded.Count);
        Assert.IsFalse(decoded.Contains(("h2", "2")));
        Assert.IsFalse(decoded.Contains(("h4", "4")));
    }

    [TestMethod]
    public void RebuildStaticHpackBlock_IncrementalIndexingBlock_Rejected()
    {
        var encoder = new Encoder(4096);
        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms);
        encoder.EncodeHeader(writer, (ByteString)"custom", (ByteString)"value");
        writer.Flush();
        var block = ms.ToArray();

        Assert.IsFalse(MitmStaticRebuildHelper.IsStaticOnlyHpackBlock(block));

        var dropped = default(MitmCompressedRelayHelper.DroppedNameBuffer);
        dropped.Add("custom");
        Assert.IsFalse(MitmStaticRebuildHelper.TryRebuildStaticHpackBlock(block, dropped, out _));
    }

    private static byte[] EncodeStaticHpack(params (ByteString Name, ByteString Value)[] headers)
    {
        var encoder = new Encoder(0);
        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms);
        foreach (var (name, value) in headers)
            encoder.EncodeHeader(writer, name, value);
        writer.Flush();
        return ms.ToArray();
    }

    private static HashSet<(string Name, string Value)> DecodeHpack(byte[] block)
    {
        var result = new HashSet<(string, string)>();
        var decoder = new Decoder(8192, 0);
        decoder.Decode(block, new CollectingListener((n, v) =>
            result.Add((n.GetString(), v.GetString()))));
        return result;
    }

    private sealed class CollectingListener : IHeaderListener
    {
        private readonly Action<ByteString, ByteString> _add;

        internal CollectingListener(Action<ByteString, ByteString> add) => _add = add;

        public void AddHeader(ByteString name, ByteString value, bool sensitive) => _add(name, value);
    }
}
