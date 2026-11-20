using System;
using System.IO;
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

    [TestMethod]
    public void Stop_RemovesScopeFileWithoutStartingSystemctl()
    {
        var home = Path.Combine(Path.GetTempPath(), "twp-FAIL-failopen-" + Guid.NewGuid().ToString("N"));
        var dir = Path.Combine(home, ".config", "TitaniumInspector");
        Directory.CreateDirectory(dir);
        var scope = Path.Combine(dir, "fail-open-proxy.scope");
        File.WriteAllText(scope, "titanium-fail-open.scope");
        var previous = UnixProcessPath.ResolveOverrideForTests;
        var previousHome = CoverageOs.HomeForTests;
        try
        {
            CoverageOs.HomeForTests = home;
            UnixProcessPath.ResolveOverrideForTests = _ => null;
            LinuxProxyFailOpen.Stop();
            Assert.IsFalse(File.Exists(scope));
            var pidPath = Path.Combine(dir, "fail-open-proxy.pid");
            File.WriteAllText(pidPath, "12345");
            UnixProcessPath.ResolveOverrideForTests = _ => Path.Combine(home, "missing-kill.exe");
            LinuxProxyFailOpen.Stop();
            Assert.IsFalse(File.Exists(pidPath));
            Assert.IsFalse(LinuxProxyFailOpen.CmdlineCoversPort(Array.Empty<byte>(), 80));
            LinuxProxyFailOpen.MarkOwnedByThisProcessForTests();
            Assert.IsFalse(LinuxProxyFailOpen.StopUnlessOwnedByThisProcess());
            LinuxProxyFailOpen.ClearOwnedByThisProcessForTests();
            Assert.IsTrue(LinuxProxyFailOpen.StopUnlessOwnedByThisProcess());
            var failExe = FakeOsExecutable.Ensure("twp-cov-exit1.exe");
            UnixProcessPath.ResolveOverrideForTests = candidates =>
                string.Join(" ", candidates).Contains("python", StringComparison.Ordinal) ? failExe : null;
            LinuxProxyFailOpen.Start("127.0.0.1", 9);
            UnixProcessPath.ResolveOverrideForTests = candidates =>
            {
                var joined = string.Join(" ", candidates);
                return joined.Contains("python", StringComparison.Ordinal) ||
                       joined.Contains("setsid", StringComparison.Ordinal)
                    ? failExe
                    : null;
            };
            LinuxProxyFailOpen.Start("127.0.0.1", 9);
            var previousXdg = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
            var previousDbus = Environment.GetEnvironmentVariable("DBUS_SESSION_BUS_ADDRESS");
            try
            {
                Environment.SetEnvironmentVariable("XDG_RUNTIME_DIR", home);
                Environment.SetEnvironmentVariable("DBUS_SESSION_BUS_ADDRESS", "unix:path=" + Path.Combine(home, "bus"));
                UnixProcessPath.ResolveOverrideForTests = candidates =>
                {
                    var joined = string.Join(" ", candidates);
                    return joined.Contains("python", StringComparison.Ordinal) ||
                           joined.Contains("systemd-run", StringComparison.Ordinal)
                        ? failExe
                        : null;
                };
                LinuxProxyFailOpen.Start("127.0.0.1", 9);
            }
            finally
            {
                Environment.SetEnvironmentVariable("XDG_RUNTIME_DIR", previousXdg);
                Environment.SetEnvironmentVariable("DBUS_SESSION_BUS_ADDRESS", previousDbus);
            }
            LinuxProxyFailOpen.ClearOwnedByThisProcessForTests();
        }
        finally
        {
            UnixProcessPath.ResolveOverrideForTests = previous;
            CoverageOs.HomeForTests = previousHome;
            try { Directory.Delete(home, true); } catch { /* best-effort */ }
        }
    }
}
