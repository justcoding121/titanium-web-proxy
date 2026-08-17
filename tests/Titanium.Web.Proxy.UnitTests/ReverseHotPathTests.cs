using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Http;
using Titanium.Web.Proxy.Models;

namespace Titanium.Web.Proxy.UnitTests
{
    [TestClass]
    public class ReverseHotPathTests
    {
        [TestMethod]
        public void TryMatchName_InternsHostAndContentLength()
        {
            Assert.IsTrue(KnownHeaders.TryMatchName("Host", out var host));
            Assert.AreSame(KnownHeaders.Host, host);

            Assert.IsTrue(KnownHeaders.TryMatchName("content-length", out var cl));
            Assert.AreSame(KnownHeaders.ContentLength, cl);

            Assert.IsFalse(KnownHeaders.TryMatchName("X-Custom", out _));
        }

        [TestMethod]
        public void TryMatchValue_InternsKeepAliveChunkedAndEncodings()
        {
            Assert.IsTrue(KnownHeaders.TryMatchValue("keep-alive", out var keepAlive));
            Assert.AreSame(KnownHeaders.ConnectionKeepAlive, keepAlive);

            Assert.IsTrue(KnownHeaders.TryMatchValue("chunked", out var chunked));
            Assert.AreSame(KnownHeaders.TransferEncodingChunked, chunked);

            Assert.IsTrue(KnownHeaders.TryMatchValue("gzip", out var gzip));
            Assert.AreSame(KnownHeaders.ContentEncodingGzip, gzip);

            Assert.IsTrue(KnownHeaders.TryMatchValue("deflate", out var deflate));
            Assert.AreSame(KnownHeaders.ContentEncodingDeflate, deflate);

            Assert.IsTrue(KnownHeaders.TryMatchValue("br", out var br));
            Assert.AreSame(KnownHeaders.ContentEncodingBrotli, br);

            Assert.IsTrue(KnownHeaders.TryMatchValue("identity", out var identity));
            Assert.AreSame(KnownHeaders.ContentEncodingIdentity, identity);
        }

        [TestMethod]
        public void HeaderParser_AddHeaderLine_UsesKnownHeaderNameInstance()
        {
            var headers = new HeaderCollection();
            // Exercise the intern path the same way HeaderParser.AddHeaderLine does.
            if (KnownHeaders.TryMatchName("Connection", out var name) &&
                KnownHeaders.TryMatchValue("keep-alive", out var value))
                headers.AddHeader(name, value);

            var header = headers.GetFirstHeader(KnownHeaders.Connection);
            Assert.IsNotNull(header);
            Assert.AreSame(KnownHeaders.Connection.String, header.Name);
            Assert.AreSame(KnownHeaders.ConnectionKeepAlive.String, header.Value);
        }

        [TestMethod]
        public void HeaderParser_UnknownNameAndValue_RoundTripViaByteString()
        {
            var headers = new HeaderCollection();
            HeaderParser.ReadHeaders(
                new SingleLineReader("X-Custom: hello-world"),
                headers,
                default).AsTask().GetAwaiter().GetResult();

            var header = headers.GetFirstHeader("X-Custom");
            Assert.IsNotNull(header);
            Assert.AreEqual("X-Custom", header.Name);
            Assert.AreEqual("hello-world", header.Value);
        }

        [TestMethod]
        public void HeaderParser_KnownNameUnknownValue_PreservesValue()
        {
            var headers = new HeaderCollection();
            HeaderParser.ReadHeaders(
                new SingleLineReader("Host: example.com:8443"),
                headers,
                default).AsTask().GetAwaiter().GetResult();

            var header = headers.GetFirstHeader(KnownHeaders.Host);
            Assert.IsNotNull(header);
            Assert.AreSame(KnownHeaders.Host.String, header.Name);
            Assert.AreEqual("example.com:8443", header.Value);
        }

        private sealed class SingleLineReader : Titanium.Web.Proxy.StreamExtended.Network.ILineStream
        {
            private readonly string?[] lines;
            private int index;

            public SingleLineReader(params string?[] lines) => this.lines = lines;

            public bool DataAvailable => false;

            public System.Threading.Tasks.ValueTask<bool> FillBufferAsync(
                System.Threading.CancellationToken cancellationToken) =>
                new(false);

            public byte ReadByteFromBuffer() => throw new System.InvalidOperationException();

            public System.Threading.Tasks.ValueTask<string?> ReadLineAsync(
                System.Threading.CancellationToken cancellationToken)
            {
                if (index >= lines.Length) return new System.Threading.Tasks.ValueTask<string?>((string?)null);
                return new System.Threading.Tasks.ValueTask<string?>(lines[index++]);
            }
        }

        [TestMethod]
        public void HeaderBuilder_WriteRequestLine_FromByteString_DoesNotRequireStringUrl()
        {
            var builder = HeaderBuilder.Rent();
            try
            {
                builder.WriteRequestLine("GET", Request.OriginFormRoot, HttpHeader.Version11);
                builder.WriteHeaders(new HeaderCollection());
                var text = builder.GetString(HttpHeader.Encoding);
                StringAssert.StartsWith(text, "GET / HTTP/1.1\r\n");
            }
            finally
            {
                HeaderBuilder.Return(builder);
            }
        }

        [TestMethod]
        public void HeaderBuilder_WriteHeader_UsesByteStringPayload()
        {
            var headers = new HeaderCollection();
            headers.AddHeader(KnownHeaders.Host, "127.0.0.1");
            var builder = HeaderBuilder.Rent();
            try
            {
                builder.WriteHeaders(headers);
                var text = builder.GetString(HttpHeader.Encoding);
                StringAssert.Contains(text, "Host: 127.0.0.1\r\n");
            }
            finally
            {
                HeaderBuilder.Return(builder);
            }
        }

        [TestMethod]
        public void ParseResponseLine_InternsOkDescription()
        {
            Response.ParseResponseLine("HTTP/1.1 200 OK", out _, out var status, out var description);
            Assert.AreEqual(200, status);
            Assert.AreSame("OK", description);
        }
    }

    internal static class HeaderCollectionTestExtensions
    {
        public static int HeaderCount(this HeaderCollection headers)
        {
            var n = 0;
            foreach (var _ in headers)
                n++;
            return n;
        }
    }
}
