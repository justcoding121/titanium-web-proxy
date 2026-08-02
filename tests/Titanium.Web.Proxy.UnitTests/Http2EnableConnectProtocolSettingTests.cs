using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Http2;

namespace Titanium.Web.Proxy.UnitTests;

[TestClass]
public class Http2EnableConnectProtocolSettingTests
{
    [TestMethod]
    public void Validate_AcceptsZeroAndOne()
    {
        Assert.IsNull(Http2OriginConnection.ValidateEnableConnectProtocolSetting(0, previouslyEnabled: false));
        Assert.IsNull(Http2OriginConnection.ValidateEnableConnectProtocolSetting(1, previouslyEnabled: false));
        Assert.IsNull(Http2OriginConnection.ValidateEnableConnectProtocolSetting(1, previouslyEnabled: true));
    }

    [TestMethod]
    public void Validate_RejectsOutOfRangeValues()
    {
        Assert.IsNotNull(Http2OriginConnection.ValidateEnableConnectProtocolSetting(2, previouslyEnabled: false));
        Assert.IsNotNull(Http2OriginConnection.ValidateEnableConnectProtocolSetting(-1, previouslyEnabled: false));
    }

    [TestMethod]
    public void Validate_RejectsForbiddenDowngrade()
    {
        var error = Http2OriginConnection.ValidateEnableConnectProtocolSetting(0, previouslyEnabled: true);
        Assert.IsNotNull(error);
        StringAssert.Contains(error, "downgraded");
    }
}
