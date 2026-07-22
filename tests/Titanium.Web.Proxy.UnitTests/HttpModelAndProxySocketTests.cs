using System;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Http;
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

        private static Titanium.Web.Proxy.ProxySocket.ProxySocket CreateSocket()
        {
            return new Titanium.Web.Proxy.ProxySocket.ProxySocket(
                AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        }
    }
}
