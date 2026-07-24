using System;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Http;
using Titanium.Web.Proxy.Models;
using Titanium.Web.Proxy.ProxySocket;

namespace Titanium.Web.Proxy.UnitTests
{
    [TestClass]
    public class HttpModelAndProxySocketTests
    {
        [TestMethod]
        public void NewHttpModels_HaveNonNullMethodDefaults()
        {
            var request = new Request();
            var response = new Response();

            Assert.AreEqual(string.Empty, request.Method);
            Assert.AreEqual(string.Empty, response.RequestMethod);
            Assert.AreEqual(string.Empty, response.StatusDescription);
        }

        [TestMethod]
        public void BeginConnect_InvalidProxyTypeWithProxyEndpoint_Throws()
        {
            using (var socket = CreateSocket())
            {
                socket.ProxyEndPoint = new IPEndPoint(IPAddress.Loopback, 1);
                socket.ProxyType = (ProxyTypes)int.MaxValue;

                var exception = Assert.ThrowsException<InvalidOperationException>(() =>
                    socket.BeginConnect(new IPEndPoint(IPAddress.Loopback, 80), null, null));

                StringAssert.Contains(exception.Message, "Unsupported proxy type");
            }
        }

        [TestMethod]
        public async Task BeginConnect_InvalidProxyTypeWithoutProxyEndpoint_ConnectsDirectly()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();

            try
            {
                var endpoint = (IPEndPoint)listener.LocalEndpoint;
                var acceptTask = listener.AcceptSocketAsync();

                using (var socket = CreateSocket())
                {
                    socket.ProxyType = (ProxyTypes)int.MaxValue;
                    socket.ProxyEndPoint = null;

                    var result = socket.BeginConnect(endpoint, null, null);
                    socket.EndConnect(result);

                    using (var accepted = await acceptTask)
                    {
                        Assert.IsTrue(socket.Connected);
                        Assert.IsTrue(accepted.Connected);
                    }
                }
            }
            finally
            {
                listener.Stop();
            }
        }

        /// <summary>
        /// Regression test for issue #744: a successful (2xx) CONNECT response must not carry
        /// Content-Length or Transfer-Encoding headers — those would be interpreted as belonging
        /// to the tunnelled byte stream rather than the response body.
        /// </summary>
        [TestMethod]
        public void ConnectResponse_Success_HasNoContentLengthOrTransferEncoding()
        {
            var response = ConnectResponse.CreateSuccessfulConnectResponse(new Version(1, 1));

            Assert.IsNull(response.Headers.GetHeaderValueOrNull("Content-Length"),
                "Successful CONNECT response must not have Content-Length");
            Assert.IsNull(response.Headers.GetHeaderValueOrNull("Transfer-Encoding"),
                "Successful CONNECT response must not have Transfer-Encoding");
            Assert.AreEqual(200, response.StatusCode);
        }

        /// <summary>
        /// Regression test for issue #839: UriExtensions.GetRawAuthority must extract the host[:port]
        /// from an absolute-form URI without going through System.Uri.
        /// </summary>
        [DataTestMethod]
        [DataRow("http://example.com/path", "example.com")]
        [DataRow("https://example.com:8443/path?q=1", "example.com:8443")]
        [DataRow("http://example.com", "example.com")]
        [DataRow("/path?q=1", null)]                        // origin-form → no authority
        [DataRow("http://example.com/", "example.com")]
        public void UriExtensions_GetRawAuthority_ExtractsCorrectly(string uri, string? expected)
        {
            var byteStr = (Titanium.Web.Proxy.Models.ByteString)uri;
            var actual = Titanium.Web.Proxy.Extensions.UriExtensions.GetRawAuthority(byteStr);
            Assert.AreEqual(expected, actual);
        }

        /// <summary>
        /// Regression test for issue #931: the request target forwarded to an upstream HTTP proxy
        /// must be the verbatim raw bytes from the original request line, not the output of
        /// System.Uri.ToString() which normalises percent-encoding and may alter non-ASCII sequences.
        /// </summary>
        [TestMethod]
        public void Request_Url_PreservesRawQueryBytes()
        {
            // Simulate an origin-form request line with a percent-encoded Unicode query string
            // (e.g., "?q=测试" encoded as UTF-8 percent sequences)
            const string rawUri = "http://example.com/search?q=%E6%B5%8B%E8%AF%95&filter=a%2Fb";

            var request = new Request();
            // Set via the raw-bytes path (as RequestHandler does from the request line)
            request.RequestUriString = rawUri;
            request.Headers.SetOrAddHeaderValue("Host", "example.com");

            // Request.Url must return the raw string without normalisation
            var url = request.Url;
            Assert.AreEqual(rawUri, url,
                "Request.Url must preserve the original percent-encoded query string verbatim");

            // System.Uri.ToString() re-encodes and may alter the query — this is the old buggy path
            var uriToString = request.RequestUri.ToString();
            // The test simply asserts that Request.Url does not go through System.Uri normalisation.
            // The only guarantee is that Request.Url matches the original raw string.
            Assert.AreEqual(rawUri, url, "Request.Url round-trip must be lossless");
        }

        private static Titanium.Web.Proxy.ProxySocket.ProxySocket CreateSocket()
        {
            return new Titanium.Web.Proxy.ProxySocket.ProxySocket(
                AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        }
    }
}
