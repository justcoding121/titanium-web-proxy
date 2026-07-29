using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Http;

namespace Titanium.Web.Proxy.UnitTests
{
    /// <summary>
    ///     Response.HasBody must apply the RFC 9110 section 6.4.1 status/method exclusions (1xx, 204,
    ///     304, HEAD, and a successful response to CONNECT) before any framing-based check, so that a
    ///     "Connection: close" header on one of those responses cannot make it report a body.
    /// </summary>
    [TestClass]
    public class ResponseHasBodyTests
    {
        private static readonly Version Http11 = new Version(1, 1);
        private static readonly Version Http10 = new Version(1, 0);

        private static Response CreateResponse(int statusCode, string requestMethod = "GET",
            bool connectionClose = false, long? contentLength = null)
        {
            var response = new Response
            {
                HttpVersion = Http11,
                StatusCode = statusCode,
                RequestMethod = requestMethod
            };

            if (connectionClose) response.Headers.AddHeader("Connection", "close");
            if (contentLength.HasValue) response.Headers.AddHeader("Content-Length", contentLength.Value.ToString());

            return response;
        }

        [DataTestMethod]
        [DataRow(100)]
        [DataRow(101)]
        [DataRow(199)]
        public void InformationalResponse_NeverHasBody_EvenWithConnectionClose(int statusCode)
        {
            var response = CreateResponse(statusCode, connectionClose: true);
            Assert.IsFalse(response.HasBody);
        }

        [TestMethod]
        public void Status204_WithConnectionClose_HasNoBody()
        {
            // Regression: "!KeepAlive" used to short-circuit to "has body" before any status
            // check ran, so a 204 with "Connection: close" incorrectly reported a body.
            var response = CreateResponse(204, connectionClose: true);
            Assert.IsFalse(response.HasBody);
        }

        [TestMethod]
        public void Status204_WithContentLength_HasNoBody()
        {
            var response = CreateResponse(204, contentLength: 10);
            Assert.IsFalse(response.HasBody);
        }

        [TestMethod]
        public void Status304_WithConnectionCloseAndContentLength_HasNoBody()
        {
            // A 304 may carry a Content-Length describing the resource, but it must not be used
            // for wire framing: the response itself never has a body.
            var response = CreateResponse(304, connectionClose: true, contentLength: 1024);
            Assert.IsFalse(response.HasBody);
        }

        [TestMethod]
        public void HeadResponse_WithContentLength_HasNoBody()
        {
            var response = CreateResponse(200, requestMethod: "HEAD", contentLength: 512);
            Assert.IsFalse(response.HasBody);
        }

        [DataTestMethod]
        [DataRow(200)]
        [DataRow(204)]
        [DataRow(299)]
        public void SuccessfulConnectResponse_NeverHasBody(int statusCode)
        {
            // RFC 9110 section 6.4.1: once a 2xx response to CONNECT is sent, the connection
            // becomes an opaque tunnel; there is no further HTTP framing to carry a body.
            var response = CreateResponse(statusCode, requestMethod: "CONNECT", contentLength: 100);
            Assert.IsFalse(response.HasBody);
        }

        [TestMethod]
        public void FailedConnectResponse_UsesNormalFramingRules()
        {
            // A non-2xx response to CONNECT (e.g. 407) is a normal HTTP response with a body.
            var response = CreateResponse(407, requestMethod: "CONNECT", contentLength: 100);
            Assert.IsTrue(response.HasBody);
        }

        [TestMethod]
        public void Status200_WithPositiveContentLength_HasBody()
        {
            var response = CreateResponse(200, contentLength: 100);
            Assert.IsTrue(response.HasBody);
        }

        [TestMethod]
        public void Status200_WithZeroContentLength_HasNoBody()
        {
            var response = CreateResponse(200, contentLength: 0);
            Assert.IsFalse(response.HasBody);
        }

        [TestMethod]
        public void Status200_Chunked_HasBody()
        {
            var response = CreateResponse(200);
            response.IsChunked = true;
            Assert.IsTrue(response.HasBody);
        }

        [TestMethod]
        public void Status200_NoFramingHeaders_ConnectionClose_HasBody()
        {
            // No Content-Length/chunking, but "Connection: close" means read-until-close.
            var response = CreateResponse(200, connectionClose: true);
            Assert.IsTrue(response.HasBody);
        }

        [TestMethod]
        public void Http10_KeepAlive_NoContentLength_HasBody()
        {
            var response = new Response
            {
                HttpVersion = Http10,
                StatusCode = 200,
                RequestMethod = "GET"
            };
            response.Headers.AddHeader("Connection", "keep-alive");
            Assert.IsTrue(response.HasBody);
        }
    }
}
