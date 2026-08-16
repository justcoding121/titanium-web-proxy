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
        public void TryMatchValue_InternsKeepAliveAndChunked()
        {
            Assert.IsTrue(KnownHeaders.TryMatchValue("keep-alive", out var keepAlive));
            Assert.AreSame(KnownHeaders.ConnectionKeepAlive, keepAlive);

            Assert.IsTrue(KnownHeaders.TryMatchValue("chunked", out var chunked));
            Assert.AreSame(KnownHeaders.TransferEncodingChunked, chunked);
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
        public void Request_ResetState_ClearsLineAndHeaders()
        {
            var request = new Request
            {
                Method = "GET",
                HttpVersion = HttpHeader.Version11,
                RequestUriString = "/",
                Host = "127.0.0.1"
            };
            request.Locked = true;
            request.CancelRequest = true;

            request.ResetState();

            Assert.AreEqual(string.Empty, request.Method);
            Assert.AreEqual(string.Empty, request.RequestUriString);
            Assert.IsFalse(request.Locked);
            Assert.IsFalse(request.CancelRequest);
            Assert.IsNull(request.Host);
            Assert.AreEqual(0, request.Headers.HeaderCount());
        }

        [TestMethod]
        public void Response_ResetState_ClearsStatus()
        {
            var response = new Response
            {
                StatusCode = 200,
                StatusDescription = "OK",
                HttpVersion = HttpHeader.Version11
            };
            response.Headers.AddHeader(KnownHeaders.ContentType, "application/json");

            response.ResetState();

            Assert.AreEqual(0, response.StatusCode);
            Assert.AreEqual(string.Empty, response.StatusDescription);
            Assert.AreEqual(0, response.Headers.HeaderCount());
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
