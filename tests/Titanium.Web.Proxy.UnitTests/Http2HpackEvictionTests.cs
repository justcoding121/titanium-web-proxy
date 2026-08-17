using System.IO;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Http2.Hpack;
using Encoder = Titanium.Web.Proxy.Http2.Hpack.Encoder;
using Decoder = Titanium.Web.Proxy.Http2.Hpack.Decoder;

namespace Titanium.Web.Proxy.UnitTests;

/// <summary>
///     Regression coverage for HPACK dynamic-table eviction: a persistent encoder/decoder pair (as used per
///     connection direction by <c>Http2Helper</c>) must keep producing correctly-decodable output as the
///     dynamic table fills and entries get evicted, both for many distinct headers and for the
///     Kestrel-shaped repeated-header case that originally exposed the encoder's <c>COMPRESSION_ERROR</c>
///     bug (see the end-to-end coverage in <c>Titanium.Web.Proxy.IntegrationTests.Http2Tests</c>).
/// </summary>
[TestClass]
public class Http2HpackEvictionTests
{
    [TestMethod]
    public void Encoder_ManyDistinctHeaders_ForcingEviction_StillDecodesCorrectly()
    {
        var encoder = new Encoder(4096);
        var decoder = new Decoder(8192, 4096);
        var listener = new RecordingHeaderListener();

        for (var i = 0; i < 200; i++)
        {
            var name = $"x-header-{i % 7}";
            var value = $"value-{i}-" + new string('v', 60);

            using var ms = new MemoryStream();
            using var writer = new BinaryWriter(ms);
            encoder.EncodeHeader(writer, Encoding.ASCII.GetBytes(name), Encoding.ASCII.GetBytes(value));
            var encoded = ms.ToArray();

            listener.Headers.Clear();
            decoder.Decode(encoded, listener);
            decoder.EndHeaderBlock();

            Assert.AreEqual(1, listener.Headers.Count, $"iteration {i}: expected exactly one decoded header");
            Assert.AreEqual(name, listener.Headers[0].Item1, $"iteration {i}: name mismatch");
            Assert.AreEqual(value, listener.Headers[0].Item2, $"iteration {i}: value mismatch");
        }
    }

    [TestMethod]
    public void Encoder_KestrelLikeResponseHeaders_RepeatedAcrossManyResponses_StillDecodesCorrectly()
    {
        var encoder = new Encoder(4096);
        var decoder = new Decoder(8192, 4096);
        const string repeatedValue =
            "a-fairly-long-repeated-header-value-used-to-exercise-http2-hpack-dynamic-table-reuse-across-requests";

        for (var i = 0; i < 10; i++)
        {
            var headers = new (string name, string value)[]
            {
                (":status", "200"),
                ("date", $"Wed, 22 Jul 2026 18:{i:D2}:00 GMT"),
                ("content-type", "text/plain"),
                ("server", "Kestrel"),
                ("x-custom-repeated", repeatedValue),
            };

            using var ms = new MemoryStream();
            using var writer = new BinaryWriter(ms);
            foreach (var (name, value) in headers)
                encoder.EncodeHeader(writer, Encoding.ASCII.GetBytes(name), Encoding.ASCII.GetBytes(value));
            var encoded = ms.ToArray();

            var listener = new RecordingHeaderListener();
            decoder.Decode(encoded, listener);
            decoder.EndHeaderBlock();

            Assert.AreEqual(headers.Length, listener.Headers.Count, $"iteration {i}: header count mismatch");
            for (var h = 0; h < headers.Length; h++)
            {
                Assert.AreEqual(headers[h].name, listener.Headers[h].Item1, $"iteration {i}, header {h}: name mismatch");
                Assert.AreEqual(headers[h].value, listener.Headers[h].Item2, $"iteration {i}, header {h}: value mismatch");
            }
        }
    }

    private sealed class RecordingHeaderListener : IHeaderListener
    {
        internal System.Collections.Generic.List<(string, string)> Headers { get; } = new();

        public void AddHeader(Models.ByteString name, Models.ByteString value, bool sensitive)
        {
            Headers.Add((name.ToString(), value.ToString()));
        }
    }
}
