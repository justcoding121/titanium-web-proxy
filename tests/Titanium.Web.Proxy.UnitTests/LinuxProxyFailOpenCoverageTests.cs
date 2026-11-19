using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Helpers;

namespace Titanium.Web.Proxy.UnitTests;

[TestClass]
public class LinuxProxyFailOpenCoverageTests
{
    [TestMethod]
    public void CmdlineCoversPort_ExactPortToken_Matches()
    {
        var cmdline = "/usr/bin/python3\0/home/u/.config/TitaniumInspector/fail-open-proxy.py\0127.0.0.1\08080\0/tmp/pid\0";
        Assert.IsTrue(LinuxProxyFailOpen.CmdlineCoversPort(cmdline, 8080));
    }

    [TestMethod]
    public void CmdlineCoversPort_SubstringPort_DoesNotMatch()
    {
        var cmdline = "/usr/bin/python3\0fail-open-proxy.py\0127.0.0.1\08080\0/tmp/pid\0";
        Assert.IsFalse(LinuxProxyFailOpen.CmdlineCoversPort(cmdline, 80),
            "Port 80 must not match a helper bound to 8080.");
        Assert.IsFalse(LinuxProxyFailOpen.CmdlineCoversPort(cmdline, 808),
            "Port 808 must not match a helper bound to 8080.");
    }

    [TestMethod]
    public void CmdlineCoversPort_HostPortToken_Matches()
    {
        var cmdline = "python3\0fail-open-proxy.py\0127.0.0.1:8866\0/tmp/pid\0";
        Assert.IsTrue(LinuxProxyFailOpen.CmdlineCoversPort(cmdline, 8866));
        Assert.IsFalse(LinuxProxyFailOpen.CmdlineCoversPort(cmdline, 886));
    }

    [TestMethod]
    public void CmdlineCoversPort_NonHelper_IsFalse()
    {
        var cmdline = "/opt/titanium-inspector/TitaniumInspector\0--port\08080\0";
        Assert.IsFalse(LinuxProxyFailOpen.CmdlineCoversPort(cmdline, 8080));
    }
}
