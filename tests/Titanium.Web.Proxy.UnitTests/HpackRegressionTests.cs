using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Http2.Hpack;
using Titanium.Web.Proxy.Models;

namespace Titanium.Web.Proxy.UnitTests
{
    [TestClass]
    public class HpackRegressionTests
    {
        [TestMethod]
        public void Decode_FragmentedStringLiteral_EmitsCompleteHeader()
        {
            var encodedHeader = new byte[]
            {
                0x00, // literal header without indexing, new name
                0x03, (byte)'f', (byte)'o', (byte)'o',
                0x03, (byte)'b', (byte)'a', (byte)'r'
            };
            var listener = new RecordingHeaderListener();
            var decoder = new Decoder(8192, 4096);

            using (var stream = new FragmentedReadStream(encodedHeader))
            using (var reader = new BinaryReader(stream))
            {
                decoder.Decode(reader, listener);
                decoder.EndHeaderBlock();
            }

            Assert.AreEqual(1, listener.Headers.Count);
            Assert.AreEqual("foo", listener.Headers[0].Item1);
            Assert.AreEqual("bar", listener.Headers[0].Item2);
        }

        [TestMethod]
        public void DynamicTable_WrappedEntriesSurviveCapacityChangeInIndexOrder()
        {
            var table = new DynamicTable(68);
            table.Add(new HttpHeader("a", "1"));
            table.Add(new HttpHeader("b", "2"));

            // Adding the third same-sized entry evicts the oldest and wraps the circular queue.
            table.Add(new HttpHeader("c", "3"));
            table.SetCapacity(100);

            Assert.AreEqual(2, table.Length());
            Assert.AreEqual("c", table.GetEntry(1).Name);
            Assert.AreEqual("3", table.GetEntry(1).Value);
            Assert.AreEqual("b", table.GetEntry(2).Name);
            Assert.AreEqual("2", table.GetEntry(2).Value);
        }

        [TestMethod]
        public void Decode_StaticIndexedMethodGet_EmitsHeader()
        {
            // 0x82 = indexed header field index 2 (:method GET)
            var listener = new RecordingHeaderListener();
            var decoder = new Decoder(8192, 4096);
            using (var stream = new MemoryStream(new byte[] { 0x82 }))
            using (var reader = new BinaryReader(stream))
            {
                decoder.Decode(reader, listener);
                decoder.EndHeaderBlock();
            }

            Assert.AreEqual(1, listener.Headers.Count);
            Assert.AreEqual(":method", listener.Headers[0].Item1);
            Assert.AreEqual("GET", listener.Headers[0].Item2);
        }

        [TestMethod]
        public void Decode_IndexedZero_Throws()
        {
            var decoder = new Decoder(8192, 4096);
            using var stream = new MemoryStream(new byte[] { 0x80 }); // indexed, index 0
            using var reader = new BinaryReader(stream);
            Assert.ThrowsException<IOException>(() => decoder.Decode(reader, new RecordingHeaderListener()));
        }

        [TestMethod]
        public void Decode_DynamicTableSizeUpdate_ThenHeader()
        {
            var listener = new RecordingHeaderListener();
            var decoder = new Decoder(8192, 4096);
            // 0x20 = DTSU with size 0, then literal foo/bar
            var bytes = new byte[]
            {
                0x20,
                0x00, 0x03, (byte)'f', (byte)'o', (byte)'o',
                0x03, (byte)'b', (byte)'a', (byte)'r'
            };
            using (var stream = new MemoryStream(bytes))
            using (var reader = new BinaryReader(stream))
            {
                decoder.Decode(reader, listener);
                decoder.EndHeaderBlock();
            }

            Assert.AreEqual(0, decoder.GetMaxHeaderTableSize());
            Assert.AreEqual(1, listener.Headers.Count);
        }

        [TestMethod]
        public void SetMaxHeaderTableSize_RequiresDtsuBeforeOtherInstructions()
        {
            var decoder = new Decoder(8192, 4096);
            decoder.SetMaxHeaderTableSize(100);
            using var stream = new MemoryStream(new byte[] { 0x82 });
            using var reader = new BinaryReader(stream);
            Assert.ThrowsException<IOException>(() => decoder.Decode(reader, new RecordingHeaderListener()));
        }

        private sealed class RecordingHeaderListener : IHeaderListener
        {
            internal List<Tuple<string, string>> Headers { get; } = new List<Tuple<string, string>>();

            public void AddHeader(ByteString name, ByteString value, bool sensitive)
            {
                Headers.Add(Tuple.Create(name.ToString(), value.ToString()));
            }
        }

        private sealed class FragmentedReadStream : MemoryStream
        {
            internal FragmentedReadStream(byte[] buffer) : base(buffer)
            {
            }

            public override int Read(byte[] buffer, int offset, int count)
            {
                return base.Read(buffer, offset, Math.Min(1, count));
            }
        }
    }
}
