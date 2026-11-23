using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
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
        using var self = Process.GetCurrentProcess();
        using var lease = Http3NativeBootstrap.ForwardUnixSignalsToChild(self);
        Assert.IsNotNull(lease);
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            lease.Dispose();
            lease.Dispose();
        }
    }

    [TestMethod]
    public void RemoveStaleBundledOpenSsl_DeletesOnlyThoseLibraries()
    {
        var dir = Path.Combine(Path.GetTempPath(), "twp-stale-ssl-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "libssl.so.3"), "stale");
            File.WriteAllText(Path.Combine(dir, "libcrypto.so.3"), "stale");
            File.WriteAllText(Path.Combine(dir, "libmsquic.so.2"), "keep");
            File.WriteAllText(Path.Combine(dir, "titanium"), "keep");

            var result = Http3NativeBootstrap.RemoveStaleBundledOpenSsl(dir);

            Assert.AreEqual(2, result.RemovedCount);
            Assert.AreEqual(0, result.BlockedPaths.Count);
            Assert.IsFalse(File.Exists(Path.Combine(dir, "libssl.so.3")));
            Assert.IsFalse(File.Exists(Path.Combine(dir, "libcrypto.so.3")));
            Assert.IsTrue(File.Exists(Path.Combine(dir, "libmsquic.so.2")));
            Assert.IsTrue(File.Exists(Path.Combine(dir, "titanium")));
            Assert.AreEqual(0, Http3NativeBootstrap.RemoveStaleBundledOpenSsl(dir).RemovedCount);
            Assert.AreEqual(0, Http3NativeBootstrap.RemoveStaleBundledOpenSsl(null).RemovedCount);
            Assert.AreEqual(0, Http3NativeBootstrap.RemoveStaleBundledOpenSsl("  ").RemovedCount);
            Assert.AreEqual(
                0,
                Http3NativeBootstrap.RemoveStaleBundledOpenSsl(Path.Combine(dir, "missing")).RemovedCount);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [TestMethod]
    public void RemoveStaleBundledOpenSsl_ReportsAFileThatCannotBeDeleted()
    {
        var dir = Path.Combine(Path.GetTempPath(), "twp-stale-ssl-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(dir);
        try
        {
            var locked = Path.Combine(dir, "libssl.so.3");
            var removable = Path.Combine(dir, "libcrypto.so");
            File.WriteAllText(locked, "stale");
            File.WriteAllText(removable, "stale");
            File.WriteAllText(Path.Combine(dir, "libmsquic.so.2"), "keep");

            var result = Http3NativeBootstrap.RemoveStaleBundledOpenSsl(dir, path =>
            {
                if (path.EndsWith("libssl.so.3", StringComparison.Ordinal))
                {
                    throw new IOException("busy");
                }

                if (path.EndsWith("libcrypto.so", StringComparison.Ordinal))
                {
                    throw new UnauthorizedAccessException("denied");
                }

                File.Delete(path);
            });

            Assert.AreEqual(0, result.RemovedCount);
            Assert.AreEqual(2, result.BlockedPaths.Count);
            Assert.IsTrue(File.Exists(locked));
            Assert.IsTrue(File.Exists(removable));
            Assert.IsTrue(result.BlockedPaths.Contains(locked));
            Assert.IsTrue(result.BlockedPaths.Contains(removable));

            var message = Http3NativeBootstrap.FormatBlockedOpenSslMessage(result.BlockedPaths);
            StringAssert.Contains(message, "sudo rm -f");
            StringAssert.Contains(message, "libssl.so.3");
            StringAssert.Contains(message, "libcrypto.so");
            StringAssert.Contains(message, dir);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [TestMethod]
    public void FormatBlockedOpenSslMessage_IsEmptyWhenNothingIsBlocked()
    {
        Assert.AreEqual(string.Empty, Http3NativeBootstrap.FormatBlockedOpenSslMessage([]));
        Assert.AreEqual(string.Empty, Http3NativeBootstrap.FormatBlockedOpenSslMessage(null!));
    }

    [TestMethod]
    public void RemoveStaleBundledOpenSsl_LeavesADirectoryThatHasNoMsQuic()
    {
        var dir = Path.Combine(Path.GetTempPath(), "twp-foreign-ssl-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(dir);
        try
        {
            var ssl = Path.Combine(dir, "libssl.so.3");
            var crypto = Path.Combine(dir, "libcrypto.so.3");
            File.WriteAllText(ssl, "theirs");
            File.WriteAllText(crypto, "theirs");

            var result = Http3NativeBootstrap.RemoveStaleBundledOpenSsl(dir);
            Assert.AreEqual(0, result.RemovedCount);
            Assert.AreEqual(0, result.BlockedPaths.Count);
            Assert.IsTrue(File.Exists(ssl));
            Assert.IsTrue(File.Exists(crypto));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [TestMethod]
    public void IsTitaniumOpenSslCleanupHost_IsOnlyTheShippingHosts()
    {
        Assert.IsTrue(Http3NativeBootstrap.IsTitaniumOpenSslCleanupHost("titanium"));
        Assert.IsTrue(Http3NativeBootstrap.IsTitaniumOpenSslCleanupHost("TitaniumInspector"));
        Assert.IsTrue(Http3NativeBootstrap.IsTitaniumOpenSslCleanupHost("RpsLoadProbe"));
        Assert.IsFalse(Http3NativeBootstrap.IsTitaniumOpenSslCleanupHost("SomeOtherApp"));
        Assert.IsFalse(Http3NativeBootstrap.IsTitaniumOpenSslCleanupHost(null));
        Assert.IsFalse(Http3NativeBootstrap.IsTitaniumOpenSslCleanupHost(
            System.Reflection.Assembly.GetEntryAssembly()?.GetName().Name));
    }
}
