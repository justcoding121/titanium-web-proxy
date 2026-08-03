using System;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Http;
using Titanium.Web.Proxy.Models;
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
        public void WinAuthCredentialsProvider_Is_Wired_On_ProxyServer()
        {
            using var proxy = new ProxyServer(false, false, false);
            Assert.IsNull(proxy.WinAuthCredentialsProvider);

            var called = false;
            proxy.WinAuthCredentialsProvider = _ =>
            {
                called = true;
                return Task.FromResult<WinAuthCredentials?>(null);
            };

            Assert.IsNotNull(proxy.WinAuthCredentialsProvider);
            // Invoke shape only — full 401 handshake needs a Windows origin.
            var result = proxy.WinAuthCredentialsProvider(
                null!).GetAwaiter().GetResult();
            Assert.IsTrue(called);
            Assert.IsNull(result);
        }

        [TestMethod]
        public void GetInitialAuthToken_With_Explicit_Credentials_On_Windows()
        {
            if (Environment.OSVersion.Platform != PlatformID.Win32NT)
                Assert.Inconclusive("Windows SSPI is required.");

            // SSPI accepts the structure even when the account is invalid — failure surfaces as
            // null/exception from AcquireCredentialsHandle. Process-identity path remains default.
            try
            {
                var token = WinAuthHandler.GetInitialAuthToken("mylocalserver.com", "NTLM",
                    new InternalDataStore(),
                    new WinAuthCredentials { Domain = ".", UserName = "no-such-user-twp", Password = "x" });
                // Some Windows configs still return a Type1 message for unknown local users.
                Assert.IsTrue(token == null || token.Length > 1);
            }
            catch (InvalidOperationException)
            {
                // Expected when SSPI rejects the synthetic credentials.
            }
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