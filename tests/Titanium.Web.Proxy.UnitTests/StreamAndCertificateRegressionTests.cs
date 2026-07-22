using System.IO;
using System.Net.Security;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Helpers;
using Titanium.Web.Proxy.StreamExtended.BufferPool;

namespace Titanium.Web.Proxy.UnitTests
{
    [TestClass]
    public class StreamAndCertificateRegressionTests
    {
        [TestMethod]
        public async Task WriteAsync_ReadOnlyMemory_WritesOnlyRequestedBytes()
        {
            var destination = new MemoryStream();
            var stream = new HttpStream(
                new ProxyServer(),
                destination,
                new DefaultBufferPool(),
                CancellationToken.None,
                true);
            var data = new byte[] { 1, 2, 3 };

            await stream.WriteAsync(new System.ReadOnlyMemory<byte>(data), CancellationToken.None);

            CollectionAssert.AreEqual(data, destination.ToArray());
        }

        [TestMethod]
        public async Task ReadLineAsync_LineWithoutTrailingNewlineAtEof_ReturnsContent()
        {
            // Regression guard: the pooled buffer must not be returned before the final
            // string is built when the stream ends without a trailing '\n'.
            var payload = System.Text.Encoding.ASCII.GetBytes("GET / HTTP/1.1");
            var source = new MemoryStream(payload);
            var stream = new HttpStream(
                new ProxyServer(),
                source,
                new DefaultBufferPool(),
                CancellationToken.None,
                false);

            var line = await stream.ReadLineAsync(CancellationToken.None);

            Assert.AreEqual("GET / HTTP/1.1", line);
        }

        [TestMethod]
        public async Task ReadLineAsync_MultipleLinesWithCrLf_ReturnsEachLine()
        {
            var payload = System.Text.Encoding.ASCII.GetBytes("first\r\nsecond\r\n");
            var source = new MemoryStream(payload);
            var stream = new HttpStream(
                new ProxyServer(),
                source,
                new DefaultBufferPool(),
                CancellationToken.None,
                false);

            var first = await stream.ReadLineAsync(CancellationToken.None);
            var second = await stream.ReadLineAsync(CancellationToken.None);

            Assert.AreEqual("first", first);
            Assert.AreEqual("second", second);
        }

        [TestMethod]
        public void CertificateCallbacks_NullSessionUseSafeDefaultsWithoutInvocation()
        {
            var validationInvoked = false;
            var selectionInvoked = false;
            var proxy = new ProxyServer();
            proxy.ServerCertificateValidationCallback += (sender, args) =>
            {
                validationInvoked = true;
                return Task.CompletedTask;
            };
            proxy.ClientCertificateSelectionCallback += (sender, args) =>
            {
                selectionInvoked = true;
                return Task.CompletedTask;
            };

            var valid = proxy.ValidateServerCertificate(
                proxy, null, null, null, SslPolicyErrors.None);
            var invalid = proxy.ValidateServerCertificate(
                proxy, null, null, null, SslPolicyErrors.RemoteCertificateNotAvailable);
            var selected = proxy.SelectClientCertificate(
                proxy, null, "example.test", null, null, null);

            Assert.IsTrue(valid);
            Assert.IsFalse(invalid);
            Assert.IsNull(selected);
            Assert.IsFalse(validationInvoked);
            Assert.IsFalse(selectionInvoked);
        }
    }
}
