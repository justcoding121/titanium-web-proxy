// The Encoder type (like Http2Helper) only compiles for net6.0+ targets; this whole file is a no-op
// on net462/net48, matching that existing convention. It activates once the unit test project itself
// moves to net10.0 or is otherwise built against a net6.0+ target.
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
    ///     Unit tests for <see cref="Encoder" />, establishing two baselines:
    ///     1. The Encoder type itself supports dynamic-table reuse correctly when the *same instance* is used
    ///        across calls.
    ///     2. Two independent Encoder instances can never benefit from cross-instance indexing - this is an
    ///        inherent property of HPACK's per-connection-direction dynamic table, not a bug.
    ///     <c>Http2Helper.SendHeader</c> reuses one <c>Encoder</c> per connection direction (stored on the
    ///     shared <c>Http2Settings</c> instance) instead of constructing a fresh one on every call, so
    ///     production traffic benefits from baseline 1 across streams on the same HTTP/2 connection. See
    ///     <c>Http2Tests.Http2_Repeated_Response_Header_Round_Trips_Correctly_Across_Multiple_Requests</c>
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
            // call; this test just pins the Encoder type's own behavior for the case where a caller
            // genuinely does use unrelated instances.
            var first = EncodeHeader(new Encoder(4096), "x-custom-header", "some-repeated-value");
            var second = EncodeHeader(new Encoder(4096), "x-custom-header", "some-repeated-value");

            Assert.AreEqual(first.Length, second.Length,
                "Two independent Encoder instances/tables can never benefit from cross-instance indexing.");
        }

        [TestMethod]
        public void Encoder_SensitiveHeader_UsesNeverIndexedPrefix()
        {
            var encoder = new Encoder(4096);
            var encoded = EncodeHeader(encoder, "authorization", "secret", sensitive: true);
            Assert.IsTrue(encoded.Length > 0);
            Assert.AreEqual(0x10, encoded[0] & 0xF0);

            // A second sensitive encode of the same header stays a full literal (never indexed).
            var again = EncodeHeader(encoder, "authorization", "secret", sensitive: true);
            Assert.AreEqual(encoded.Length, again.Length);
        }

        [TestMethod]
        public void Encoder_MaxHeaderTableSizeZero_UsesLiteralWithoutIndexingForUnknown()
        {
            var encoder = new Encoder(0);
            var custom = EncodeHeader(encoder, "x-custom", "v");
            Assert.AreEqual(0x00, custom[0] & 0xF0); // literal without indexing
            Assert.IsTrue(custom.Length > 1);

            // Round-trip still works with a zero-capacity encoder/decoder pair.
            var decoder = new Decoder(8192, 0);
            var listener = new RecordingHeaderListener();
            Decode(decoder, listener, custom);
            Assert.AreEqual(1, listener.Headers.Count);
            Assert.AreEqual("x-custom", listener.Headers[0].Item1);
        }

        [TestMethod]
        public void Encoder_IndexTypeNone_DoesNotEnableLaterIndexing()
        {
            var encoder = new Encoder(4096);
            var first = EncodeHeader(encoder, "x-path-like", "/a", sensitive: false, HpackUtil.IndexType.None);
            var second = EncodeHeader(encoder, "x-path-like", "/a", sensitive: false, HpackUtil.IndexType.None);
            Assert.AreEqual(first.Length, second.Length);
            Assert.AreEqual(0x00, first[0] & 0xF0);
        }

        [TestMethod]
        public void Encoder_UseStaticNameFalse_StillRoundTrips()
        {
            var encoded = EncodeHeader(new Encoder(4096), "x-unique-name", "v1", useStaticName: false);
            var decoder = new Decoder(8192, 4096);
            var listener = new RecordingHeaderListener();
            Decode(decoder, listener, encoded);
            Assert.AreEqual(1, listener.Headers.Count);
            Assert.AreEqual("x-unique-name", listener.Headers[0].Item1);
            Assert.AreEqual("v1", listener.Headers[0].Item2);
        }

        private static byte[] EncodeHeader(Encoder encoder, string name, string value,
            bool sensitive = false, HpackUtil.IndexType indexType = HpackUtil.IndexType.Incremental,
            bool useStaticName = true)
        {
            using var ms = new MemoryStream();
            using var writer = new BinaryWriter(ms);
            encoder.EncodeHeader(writer, Encoding.ASCII.GetBytes(name), Encoding.ASCII.GetBytes(value),
                sensitive, indexType, useStaticName);
            return ms.ToArray();
        }

        private static void Decode(Decoder decoder, RecordingHeaderListener listener, byte[] encoded)
        {
            decoder.Decode(encoded, listener);
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
