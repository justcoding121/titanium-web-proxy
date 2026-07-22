// The Encoder type (like Http2Helper) only compiles for net6.0+ targets; this whole file is a no-op
// on net462/net48, matching that existing convention. It activates once the unit test project itself
// moves to net10.0 (Phase 0B) or is otherwise built against a net6.0+ target.
#if NET6_0_OR_GREATER
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Http2.Hpack;

namespace Titanium.Web.Proxy.UnitTests
{
    /// <summary>
    ///     Phase 0A characterization tests for <see cref="Encoder" />. Before Phase 0A there were no dedicated
    ///     Encoder tests (only Decoder/DynamicTable regressions). These tests establish two baselines that Phase 2
    ///     (HTTP/2 HPACK persistence) will build on:
    ///     1. The Encoder type itself already supports dynamic-table reuse correctly when the *same instance* is
    ///        used across calls.
    ///     2. Http2Helper.SendHeader's current wiring constructs a brand-new Encoder on every call, so in
    ///        production no dynamic-table reuse happens across header blocks today. Update/replace this second
    ///        test once Http2Helper persists one Encoder per connection direction.
    /// </summary>
    [TestClass]
    public class Http2HpackEncoderTests
    {
        [TestMethod]
        public void Encoder_ReusedInstance_IndexesRepeatedHeaderIntoDynamicTable()
        {
            var encoder = new Encoder(4096);
            var decoder = new Decoder(8192, 4096);
            var listener = new RecordingHeaderListener();

            var first = EncodeHeader(encoder, "x-custom-header", "some-repeated-value");
            var second = EncodeHeader(encoder, "x-custom-header", "some-repeated-value");

            Assert.IsTrue(second.Length < first.Length,
                "A reused Encoder instance should emit a compact indexed reference for a header " +
                "it has already added to the dynamic table.");

            Decode(decoder, listener, first);
            Decode(decoder, listener, second);

            Assert.AreEqual(2, listener.Headers.Count);
            foreach (var (name, value) in listener.Headers)
            {
                Assert.AreEqual("x-custom-header", name);
                Assert.AreEqual("some-repeated-value", value);
            }
        }

        [TestMethod]
        public void Encoder_FreshInstancePerCall_MirroringHttp2HelperSendHeaderWiring_NeverIndexesRepeatedHeader()
        {
            // Characterizes today's Http2Helper.SendHeader, which does `new Encoder(settings.HeaderTableSize)`
            // on every call instead of persisting one encoder per connection/direction. With a fresh instance
            // each time, the dynamic table never accumulates state across header blocks, so repeated headers
            // are always encoded literally (no size benefit, and the two peers' HPACK contexts never diverge
            // only because they're never actually shared to begin with).
            var first = EncodeHeader(new Encoder(4096), "x-custom-header", "some-repeated-value");
            var second = EncodeHeader(new Encoder(4096), "x-custom-header", "some-repeated-value");

            Assert.AreEqual(first.Length, second.Length,
                "A fresh Encoder per call cannot benefit from dynamic-table indexing across calls; " +
                "this pins today's (sub-optimal) Http2Helper wiring ahead of the Phase 2 HPACK persistence work.");
        }

        private static byte[] EncodeHeader(Encoder encoder, string name, string value)
        {
            using var ms = new MemoryStream();
            using var writer = new BinaryWriter(ms);
            encoder.EncodeHeader(writer, Encoding.ASCII.GetBytes(name), Encoding.ASCII.GetBytes(value));
            return ms.ToArray();
        }

        private static void Decode(Decoder decoder, RecordingHeaderListener listener, byte[] encoded)
        {
            using var reader = new BinaryReader(new MemoryStream(encoded));
            decoder.Decode(reader, listener);
            decoder.EndHeaderBlock();
        }

        private sealed class RecordingHeaderListener : IHeaderListener
        {
            internal List<(string, string)> Headers { get; } = new();

            public void AddHeader(Models.ByteString name, Models.ByteString value, bool sensitive)
            {
                Headers.Add((name.ToString(), value.ToString()));
            }
        }
    }
}
#endif
