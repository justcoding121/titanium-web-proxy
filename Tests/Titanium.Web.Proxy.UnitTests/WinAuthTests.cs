using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Network.WinAuth;

namespace Titanium.Web.Proxy.UnitTests
{
    [TestClass]
    public class WinAuthTests
    {
        [TestMethod]
        public void Test_Acquire_Client_Token()
        {
            var token = WinAuthHandler.GetInitialAuthToken("mylocalserver.com");
            Assert.IsTrue(token.Length > 1);
        }
    }
}
