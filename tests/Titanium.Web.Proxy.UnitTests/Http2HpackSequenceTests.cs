using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Extensions;
using Titanium.Web.Proxy.Http;
using Titanium.Web.Proxy.Http2;
using Titanium.Web.Proxy.Models;
using HpackDecoder = Titanium.Web.Proxy.Http2.Hpack.Decoder;

namespace Titanium.Web.Proxy.UnitTests;

/// <summary>
///     Sequence round trip: many browser-shaped requests through the real <c>SendHeader</c> encoder,
///     decoded by a strict HPACK decoder whose table matches the peer default (4096).
/// </summary>
[TestClass]
public class Http2HpackSequenceTests
{
    private sealed class Capture : Http2.Hpack.IHeaderListener
    {
        internal readonly List<(string Name, string Value)> Fields = new();

        public void AddHeader(ByteString name, ByteString value, bool sensitive) =>
            Fields.Add((name.GetString(), value.GetString()));
    }

    [TestMethod]
    public async Task SendHeader_LongBrowserShapedSequence_DecodesIdentically()
    {
        var rng = new Random(12345);
        var settings = new Http2Settings { MaxFrameSize = 16384 };
        var decoder = new HpackDecoder(1024 * 1024, 4096);

        var stableNames = new[]
        {
            "accept", "accept-encoding", "accept-language", "authorization", "cache-control", "content-type",
            "origin", "priority", "referer", "sec-ch-ua", "sec-ch-ua-mobile", "sec-ch-ua-platform",
            "sec-fetch-dest", "sec-fetch-mode", "sec-fetch-site", "user-agent", "x-csrf-token",
            "x-twitter-active-user", "x-twitter-auth-type", "x-twitter-client-language", "x-client-uuid"
        };

        for (var n = 0; n < 400; n++)
        {
            var request = new Request
            {
                Method = rng.Next(4) == 0 ? "POST" : "GET",
                HttpVersion = HttpHeader.Version20,
                IsHttps = true,
                Authority = "x.com".GetByteString(),
                RequestUriString = "/i/api/graphql/" + rng.Next(40) + "/Op" + rng.Next(25),
                Priority = rng.Next(3) == 0 ? 0x80_00_00_00_dbL : 0xdbL
            };

            foreach (var name in stableNames)
                request.Headers.AddHeader(name, "value-of-" + name + "-" + (rng.Next(6) == 0 ? rng.Next(5) : 0));

            // Large cookie that changes now and then, like x.com's session cookies.
            var cookieSeed = rng.Next(8) == 0 ? rng.Next(1000) : 7;
            request.Headers.AddHeader("cookie", new string((char)('a' + cookieSeed % 26), 1200 + cookieSeed % 700));
            for (var i = 0; i < 12; i++)
                request.Headers.AddHeader("x-extra-" + i, "v" + rng.Next(3));
            request.Headers.AddHeader("x-client-transaction-id", Guid.NewGuid().ToString("N"));

            using var ms = new MemoryStream();
            var frame = new Http2FrameHeader { StreamId = 1 + n * 2 };
            await Http2Helper.SendHeader(settings, frame, new byte[9], request, endStream: true, ms, false);

            var wire = ms.ToArray();
            var len = (wire[0] << 16) | (wire[1] << 8) | wire[2];
            var flags = wire[4];
            Assert.AreNotEqual(0, flags & 0x4, "single HEADERS frame expected");
            var skip = (flags & 0x20) != 0 ? 5 : 0;
            var block = new byte[len - skip];
            Buffer.BlockCopy(wire, 9 + skip, block, 0, block.Length);

            var capture = new Capture();
            try
            {
                decoder.Decode(block, capture);
            }
            catch (Exception ex)
            {
                Assert.Fail($"request {n}: decoder rejected block: {ex.Message}");
            }

            decoder.EndHeaderBlock();

            var expected = new List<(string, string)>();
            foreach (var h in request.Headers)
                expected.Add((h.Name.ToLowerInvariant(), h.Value));

            var actual = capture.Fields.FindAll(f => !f.Name.StartsWith(':'));
            Assert.AreEqual(expected.Count, actual.Count, $"request {n}: header count");
            for (var i = 0; i < expected.Count; i++)
            {
                Assert.AreEqual(expected[i].Item1, actual[i].Name, $"request {n} header {i} name");
                Assert.AreEqual(expected[i].Item2, actual[i].Value, $"request {n} header {i} value");
            }
        }
    }
}
