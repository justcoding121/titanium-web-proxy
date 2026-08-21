using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Http;
using Titanium.Web.Proxy.Models;

namespace Titanium.Web.Proxy.UnitTests
{
    /// <summary>
    ///     Unit tests for <see cref="RequestResponseBase" /> (via <see cref="Response" />):
    ///     body assignment, chunked/content-length bookkeeping, and the compression helper used when relaying a
    ///     buffered, modified body back onto the wire.
    /// </summary>
    [TestClass]
    public class RequestResponseBaseTests
    {
        [TestMethod]
        public void SettingBody_NonChunked_UpdatesContentLengthHeaderToMatchByteLength()
        {
            var response = new Response();

            response.Body = Encoding.ASCII.GetBytes("hello world");

            Assert.AreEqual(11, response.ContentLength);
            Assert.AreEqual("11", response.Headers.GetHeaderValueOrNull(KnownHeaders.ContentLength));
        }

        [TestMethod]
        public void IsChunked_True_ClearsContentLengthAndAddsTransferEncodingHeader()
        {
            var response = new Response();

            response.IsChunked = true;

            Assert.AreEqual(-1, response.ContentLength);
            Assert.IsTrue(response.Headers.HeaderExists("Transfer-Encoding"));

            response.IsChunked = false;

            Assert.IsFalse(response.Headers.HeaderExists("Transfer-Encoding"));
        }

        [TestMethod]
        public void ContentLength_SetToNegative_RemovesContentLengthHeader()
        {
            var response = new Response();
            response.Body = Encoding.ASCII.GetBytes("hello");
            Assert.IsTrue(response.Headers.HeaderExists("Content-Length"));

            response.ContentLength = -1;

            Assert.IsFalse(response.Headers.HeaderExists("Content-Length"));
        }

        [TestMethod]
        public void BodyString_DecodesBodyUsingContentTypeEncoding()
        {
            var response = new Response(Encoding.UTF8.GetBytes("héllo"))
            {
                ContentType = "text/plain; charset=utf-8"
            };

            Assert.AreEqual("héllo", response.BodyString);
        }

        [TestMethod]
        public void CompressBodyAndUpdateContentLength_Gzip_ProducesDecodableOutputAndUpdatesContentLength()
        {
            var original = Encoding.ASCII.GetBytes(
                "the quick brown fox jumps over the lazy dog. the quick brown fox jumps over the lazy dog.");
            var response = new Response(original);
            response.Headers.AddHeader(KnownHeaders.ContentEncoding, "gzip");

            var compressed = response.CompressBodyAndUpdateContentLength();

            Assert.IsNotNull(compressed);
            Assert.AreEqual(compressed.Length, response.ContentLength);

            using (var compressedStream = new MemoryStream(compressed))
            using (var gzip = new GZipStream(compressedStream, CompressionMode.Decompress))
            using (var decompressed = new MemoryStream())
            {
                gzip.CopyTo(decompressed);
                CollectionAssert.AreEqual(original, decompressed.ToArray());
            }
        }

        [TestMethod]
        public void CompressBodyAndUpdateContentLength_Chunked_SetsContentLengthToUnknown()
        {
            var original = Encoding.ASCII.GetBytes("streamed body content");
            var response = new Response(original) { IsChunked = true };
            response.Headers.AddHeader(KnownHeaders.ContentEncoding, "gzip");

            response.CompressBodyAndUpdateContentLength();

            Assert.AreEqual(-1, response.ContentLength);
        }

        [TestMethod]
        public void CompressBodyAndUpdateContentLength_NoBodyAndNotRead_ReturnsNull()
        {
            var response = new Response();

            var result = response.CompressBodyAndUpdateContentLength();

            Assert.IsNull(result);
        }

        [TestMethod]
        public void TrailingHeaders_DefaultsToEmptyAndHasTrailingHeadersReflectsContent()
        {
            var response = new Response();

            // HasTrailingHeaders must not force the lazy allocation that the public getter performs.
            Assert.IsFalse(response.HasTrailingHeaders);

            Assert.IsNotNull(response.TrailingHeaders);
            Assert.IsFalse(response.TrailingHeaders.GetEnumerator().MoveNext());
            Assert.IsFalse(response.HasTrailingHeaders, "An empty collection was allocated but nothing was added.");

            response.TrailingHeaders.AddHeader("X-Checksum", "abc123");

            Assert.IsTrue(response.HasTrailingHeaders);
            Assert.AreEqual("abc123", response.TrailingHeaders.GetFirstHeader("X-Checksum")?.Value);
        }

        [TestMethod]
        public void TrailingHeaders_SameInstanceReturnedOnRepeatedAccess()
        {
            var request = new Request();

            var first = request.TrailingHeaders;
            var second = request.TrailingHeaders;

            Assert.AreSame(first, second);
        }

        [TestMethod]
        public void ResetForKeepAlive_ClearsWireStateAndUnlocksRequest()
        {
            var request = new Request
            {
                Method = "GET",
                HttpVersion = HttpHeader.Version11,
                Locked = true,
                HeaderNamesAreHttp2Normalized = true
            };
            request.Headers.AddHeader("Host", "example.test");
            request.Headers.AddHeader("Content-Length", "0");
            request.TrailingHeaders.AddHeader("X-Checksum", "abc");

            request.ResetForKeepAlive();

            Assert.AreEqual(string.Empty, request.Method);
            Assert.AreEqual(HttpHeader.VersionUnknown, request.HttpVersion);
            Assert.IsFalse(request.Locked);
            Assert.IsFalse(request.HeaderNamesAreHttp2Normalized);
            Assert.AreEqual(0, request.Headers.Count());
            Assert.IsFalse(request.HasTrailingHeaders);
        }
    }
}
