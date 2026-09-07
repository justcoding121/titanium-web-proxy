using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Http3;

namespace Titanium.Web.Proxy.UnitTests;

[TestClass]
public class Http3NativeBootstrapTests
{
    [TestMethod]
    public void ForwardUnixSignalsToChild_ReturnsDisposableLease()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            Assert.Inconclusive("POSIX signal forwarding is Unix-only.");
        }

        using var self = Process.GetCurrentProcess();
        using var lease = Http3NativeBootstrap.ForwardUnixSignalsToChild(self);
        Assert.IsNotNull(lease);
    }
}
