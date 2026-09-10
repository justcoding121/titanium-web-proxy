using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Http2;

namespace Titanium.Web.Proxy.UnitTests;

[TestClass]
public class Http2OriginDiagPickStatsTests
{
    [TestMethod]
    public void IsEnabled_False_ByDefault()
    {
        Assert.IsFalse(Http2OriginConnectionPool.DiagPickStats.IsEnabled);
    }
}
