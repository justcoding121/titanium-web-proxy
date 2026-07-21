using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Http;
using Titanium.Web.Proxy.Network.WinAuth;

namespace Titanium.Web.Proxy.UnitTests
{
    [TestClass]
    public class WinAuthTests
    {
        [TestMethod]
        public void Test_Acquire_Client_Token()
        {
            var token = WinAuthHandler.GetInitialAuthToken("mylocalserver.com", "NTLM", new InternalDataStore());
            Assert.IsTrue(token.Length > 1);
        }

        [TestMethod]
        public void Test_Acquire_Upstream_Proxy_Client_Token()
        {
            if (Environment.OSVersion.Platform != PlatformID.Win32NT)
                Assert.Inconclusive("Windows SSPI is required.");

            var token =
                WinAuthHandler.GetInitialProxyAuthToken("proxy.example.com", "NTLM", new InternalDataStore());

            StringAssert.StartsWith(token, " ");
            Assert.IsTrue(token.Length > 1);
        }

        [TestMethod]
        public void ReuseConnectionForAuthReRequest_ProxyAuth_ReusesConnection()
        {
            // 407 (upstream proxy auth) must reuse the same connection regardless of the WinAuth flag.
            Assert.IsTrue(ProxyServer.ShouldReuseConnectionForAuthReRequest(407, false));
            Assert.IsTrue(ProxyServer.ShouldReuseConnectionForAuthReRequest(407, true));
        }

        [TestMethod]
        public void ReuseConnectionForAuthReRequest_ServerWinAuth401_ReusesConnection()
        {
            // 401 handled by NTLM/Negotiate is connection-oriented and must reuse the same connection.
            Assert.IsTrue(ProxyServer.ShouldReuseConnectionForAuthReRequest(401, true));
        }

        [TestMethod]
        public void ReuseConnectionForAuthReRequest_NonAuthReRequest_UsesFreshConnection()
        {
            // A user-initiated re-request (not an auth handshake) may target a different destination.
            Assert.IsFalse(ProxyServer.ShouldReuseConnectionForAuthReRequest(200, false));
            Assert.IsFalse(ProxyServer.ShouldReuseConnectionForAuthReRequest(302, false));
            Assert.IsFalse(ProxyServer.ShouldReuseConnectionForAuthReRequest(401, false));
        }
    }
}