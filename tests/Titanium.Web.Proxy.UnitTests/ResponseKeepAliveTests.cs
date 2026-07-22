using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Http;

namespace Titanium.Web.Proxy.UnitTests
{
    [TestClass]
    public class ResponseKeepAliveTests
    {
        private static readonly Version Http10 = new Version(1, 0);
        private static readonly Version Http11 = new Version(1, 1);
        private static readonly Version Http20 = new Version(2, 0);

        [TestMethod]
        public void Http11_NoConnectionHeader_IsKeepAlive()
        {
            var response = new Response { HttpVersion = Http11 };
            Assert.IsTrue(response.KeepAlive);
        }

        [TestMethod]
        public void Http11_ConnectionClose_IsNotKeepAlive()
        {
            var response = new Response { HttpVersion = Http11 };
            response.Headers.AddHeader("Connection", "close");
            Assert.IsFalse(response.KeepAlive);
        }

        [TestMethod]
        public void Http10_NoConnectionHeader_IsNotKeepAlive()
        {
            // HTTP/1.0 defaults to close: such a connection must not be pooled/reused.
            var response = new Response { HttpVersion = Http10 };
            Assert.IsFalse(response.KeepAlive);
        }

        [TestMethod]
        public void Http10_ConnectionKeepAlive_IsKeepAlive()
        {
            var response = new Response { HttpVersion = Http10 };
            response.Headers.AddHeader("Connection", "keep-alive");
            Assert.IsTrue(response.KeepAlive);
        }

        [TestMethod]
        public void Http10_ConnectionClose_IsNotKeepAlive()
        {
            var response = new Response { HttpVersion = Http10 };
            response.Headers.AddHeader("Connection", "close");
            Assert.IsFalse(response.KeepAlive);
        }

        [TestMethod]
        public void Http2_NoConnectionHeader_IsKeepAlive()
        {
            var response = new Response { HttpVersion = Http20 };
            Assert.IsTrue(response.KeepAlive);
        }
    }
}
