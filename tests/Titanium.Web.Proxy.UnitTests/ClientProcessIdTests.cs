using System;
using System.IO;
using System.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Helpers;

namespace Titanium.Web.Proxy.UnitTests;

[TestClass]
public class ClientProcessIdTests
{
    [TestMethod]
    public void IsSupported_MatchesCurrentOsFamily()
    {
        var expected = OperatingSystem.IsWindows()
            || OperatingSystem.IsLinux()
            || OperatingSystem.IsMacOS();
        Assert.AreEqual(expected, ClientProcessId.IsSupported);
    }

    [TestMethod]
    public void GetProcessIdByLocalPort_ResolvesOwnListeningSocket_WhenSupported()
    {
        if (!ClientProcessId.IsSupported)
        {
            Assert.Inconclusive("Client process id lookup is not supported on this OS.");
            return;
        }

        using var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;

        // Give the OS a moment to publish the socket in the TCP table / libproc.
        var pid = 0;
        for (var attempt = 0; attempt < 20 && pid <= 0; attempt++)
        {
            pid = TcpHelper.GetProcessIdByLocalPort(System.Net.Sockets.AddressFamily.InterNetwork, port);
            if (pid <= 0)
            {
                Thread.Sleep(25);
            }
        }

        Assert.AreEqual(Environment.ProcessId, pid);
    }
}

[TestClass]
public class LinuxProcNetTcpTests
{
    private const string SampleTcpTable = """
        sl  local_address rem_address   st tx_queue rx_queue tr tm->when retrnsmt   uid  timeout inode
         0: 0100007F:1F90 00000000:0000 0A 00000000:00000000 00:00000000 00000000     0        0 12345 1 0000000000000000 100 0 0 10 0
         1: 00000000:0050 0100007F:ABCD 01 00000000:00000000 00:00000000 00000000     0        0 99999 1 0000000000000000 100 0 0 10 0
        """;

    private const string SampleTcp6Table = """
        sl  local_address                         remote_address                        st tx_queue rx_queue tr tm->when retrnsmt   uid  timeout inode
         0: 00000000000000000000000000000001:0050 00000000000000000000000000000000:0000 0A 00000000:00000000 00:00000000 00000000     0        0 54321 1 0000000000000000 100 0 0 10 0
        """;

    [TestMethod]
    public void TryParseLocalPortAndInode_ParsesIpv4Line()
    {
        var line =
            "   0: 0100007F:1F90 00000000:0000 0A 00000000:00000000 00:00000000 00000000     0        0 12345 1 0000000000000000 100 0 0 10 0";
        Assert.IsTrue(LinuxProcNetTcp.TryParseLocalPortAndInode(line, out var port, out var inode));
        Assert.AreEqual(0x1F90, port);
        Assert.AreEqual(12345L, inode);
    }

    [TestMethod]
    public void TryParseLocalPortAndInode_SkipsHeader()
    {
        Assert.IsFalse(LinuxProcNetTcp.TryParseLocalPortAndInode(
            "sl  local_address rem_address   st tx_queue rx_queue tr tm->when retrnsmt   uid  timeout inode",
            out _, out _));
    }

    [TestMethod]
    public void TryFindInodeForLocalPort_FindsMatchAndMiss()
    {
        Assert.IsTrue(LinuxProcNetTcp.TryFindInodeForLocalPort(SampleTcpTable, 8080, out var inode));
        Assert.AreEqual(12345L, inode);

        Assert.IsFalse(LinuxProcNetTcp.TryFindInodeForLocalPort(SampleTcpTable, 443, out _));
    }

    [TestMethod]
    public void TryFindInodeForLocalPort_ParsesIpv6Table()
    {
        Assert.IsTrue(LinuxProcNetTcp.TryFindInodeForLocalPort(SampleTcp6Table, 80, out var inode));
        Assert.AreEqual(54321L, inode);
    }

    [TestMethod]
    public void FindProcessIdByInode_ReturnsZeroForMissingInode()
    {
        Assert.AreEqual(0, LinuxProcNetTcp.FindProcessIdByInode(0));
        Assert.AreEqual(0, LinuxProcNetTcp.FindProcessIdByInode(-1));
    }

    [TestMethod]
    public void FindProcessIdByInode_ResolvesFromFakeProcTree()
    {
        var root = Path.Combine(Path.GetTempPath(), "twp-proc-" + Guid.NewGuid().ToString("N"));
        var pid = 4242;
        var inode = 777001L;
        var fdDir = Path.Combine(root, pid.ToString(), "fd");
        Directory.CreateDirectory(fdDir);

        // Regular file named like an fd; create a symlink to socket:[inode] when the OS allows it.
        var linkPath = Path.Combine(fdDir, "3");
        try
        {
            File.CreateSymbolicLink(linkPath, $"socket:[{inode}]");
        }
        catch (Exception ex) when (ex is IOException or PlatformNotSupportedException or UnauthorizedAccessException)
        {
            Assert.Inconclusive("Symbolic links unavailable: " + ex.Message);
            return;
        }

        try
        {
            Assert.AreEqual(pid, LinuxProcNetTcp.FindProcessIdByInode(inode, root));
            Assert.AreEqual(0, LinuxProcNetTcp.FindProcessIdByInode(inode + 1, root));
        }
        finally
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch
            {
                // best-effort cleanup
            }
        }
    }
}
