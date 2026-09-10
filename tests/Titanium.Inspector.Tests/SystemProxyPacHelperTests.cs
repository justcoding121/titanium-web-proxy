using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Inspector.Services;

namespace Titanium.Inspector.Tests;

[TestClass]
public class SystemProxyPacHelperTests
{
    [TestMethod]
    public void ScutilIndicatesPacOrWpad_DetectsPacEnable()
    {
        const string pac = """
            <dictionary> {
              HTTPEnable : 1
              HTTPPort : 8866
              HTTPProxy : 127.0.0.1
              ProxyAutoConfigEnable : 1
            }
            """;
        Assert.IsTrue(SystemProxyPacHelper.ScutilIndicatesPacOrWpad(pac));
    }

    [TestMethod]
    public void ScutilIndicatesPacOrWpad_DetectsWpad()
    {
        const string wpad = """
            ProxyAutoDiscoveryEnable : 1
            HTTPEnable : 0
            """;
        Assert.IsTrue(SystemProxyPacHelper.ScutilIndicatesPacOrWpad(wpad));
    }

    [TestMethod]
    public void ScutilIndicatesPacOrWpad_ManualProxyOnly_IsFalse()
    {
        const string manual = """
            HTTPEnable : 1
            HTTPPort : 8866
            HTTPProxy : 127.0.0.1
            ProxyAutoConfigEnable : 0
            SOCKSEnable : 0
            """;
        Assert.IsFalse(SystemProxyPacHelper.ScutilIndicatesPacOrWpad(manual));
        Assert.IsFalse(SystemProxyPacHelper.ScutilIndicatesPacOrWpad(""));
        Assert.IsFalse(SystemProxyPacHelper.ScutilIndicatesPacOrWpad(null));
    }
}
