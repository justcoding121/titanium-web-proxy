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
