using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Helpers;

namespace Titanium.Web.Proxy.UnitTests;

[TestClass]
public class LinuxInspectorProcessTests
{
    [TestMethod]
    public void IsRunning_FindsTitaniumInspectorExe()
    {
        var root = Path.Combine(Path.GetTempPath(), "ti-proc-" + Path.GetRandomFileName());
        Directory.CreateDirectory(Path.Combine(root, "4242"));
        File.WriteAllText(
            Path.Combine(root, "4242", "cmdline"),
            "/opt/titanium-inspector/TitaniumInspector\0--some-flag\0");
        try
        {
            Assert.IsTrue(LinuxInspectorProcess.IsRunning(root));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* ignore */ }
        }
    }

    [TestMethod]
    public void IsRunning_IgnoresClearSystemProxy()
    {
        var root = Path.Combine(Path.GetTempPath(), "ti-proc-" + Path.GetRandomFileName());
        Directory.CreateDirectory(Path.Combine(root, "99"));
        File.WriteAllText(
            Path.Combine(root, "99", "cmdline"),
            "/opt/titanium-inspector/TitaniumInspector\0--clear-system-proxy\0");
        try
        {
            Assert.IsFalse(LinuxInspectorProcess.IsRunning(root));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* ignore */ }
        }
    }
}
