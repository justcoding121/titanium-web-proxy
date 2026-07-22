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
// System.Text also defines abstract Encoder/Decoder types (for char<->byte transcoding); alias the HPACK
// ones explicitly so they win over those System.Text names brought in by the `using System.Text;` above.
using Encoder = Titanium.Web.Proxy.Http2.Hpack.Encoder;
using Decoder = Titanium.Web.Proxy.Http2.Hpack.Decoder;

namespace Titanium.Web.Proxy.UnitTests
{
    /// <summary>
    ///     Unit tests for <see cref="Encoder" />. Originally written in Phase 0A as characterization tests
    ///     (before dedicated Encoder tests existed), establishing two baselines:
    ///     1. The Encoder type itself supports dynamic-table reuse correctly when the *same instance* is used
    ///        across calls (still true, and still the mechanism Phase 2 relies on).
    ///     2. Two independent Encoder instances can never benefit from cross-instance indexing - this is an
    ///        inherent property of HPACK's per-connection-direction dynamic table, not a bug.
    ///     Phase 2 (HTTP/2 HPACK persistence) made <c>Http2Helper.SendHeader</c> reuse one <c>Encoder</c> per
    ///     connection direction (stored on the shared <c>Http2Settings</c> instance) instead of constructing a
    ///     fresh one on every call, so production traffic now benefits from baseline 1 above across streams on
    ///     the same HTTP/2 connection. See <c>Http2Tests.Http2_Repeated_Response_Header_Round_Trips_Correctly_Across_Multiple_Requests</c>
    ///     in the integration test suite for an end-to-end proof through the real relay.
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
        public void Encoder_FreshInstancePerCall_NeverIndexesRepeatedHeader()
        {
            // Two independent Encoder instances have two independent (empty) dynamic tables, so neither can
            // ever emit an indexed reference into the other's table - this is inherent to HPACK, not something
            // any wiring change can affect. Http2Helper.SendHeader no longer constructs a fresh Encoder per
            // call (Phase 2); this test just pins the Encoder type's own behavior for the case where a caller
            // genuinely does use unrelated instances.
            var first = EncodeHeader(new Encoder(4096), "x-custom-header", "some-repeated-value");
            var second = EncodeHeader(new Encoder(4096), "x-custom-header", "some-repeated-value");

            Assert.AreEqual(first.Length, second.Length,
                "Two independent Encoder instances/tables can never benefit from cross-instance indexing.");
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
