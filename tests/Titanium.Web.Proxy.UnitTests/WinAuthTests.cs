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
    }
}