using System;
using System.Diagnostics;
using System.IO;
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

            var removed = Http3NativeBootstrap.RemoveStaleBundledOpenSsl(dir);

            Assert.AreEqual(2, removed);
            Assert.IsFalse(File.Exists(Path.Combine(dir, "libssl.so.3")));
            Assert.IsFalse(File.Exists(Path.Combine(dir, "libcrypto.so.3")));
            Assert.IsTrue(File.Exists(Path.Combine(dir, "libmsquic.so.2")));
            Assert.IsTrue(File.Exists(Path.Combine(dir, "titanium")));
            Assert.AreEqual(0, Http3NativeBootstrap.RemoveStaleBundledOpenSsl(dir));
            Assert.AreEqual(0, Http3NativeBootstrap.RemoveStaleBundledOpenSsl(null));
            Assert.AreEqual(0, Http3NativeBootstrap.RemoveStaleBundledOpenSsl("  "));
            Assert.AreEqual(0, Http3NativeBootstrap.RemoveStaleBundledOpenSsl(Path.Combine(dir, "missing")));
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
        var previous = Console.Error;
        var error = new StringWriter();
        try
        {
            var locked = Path.Combine(dir, "libssl.so.3");
            var removable = Path.Combine(dir, "libcrypto.so");
            File.WriteAllText(locked, "stale");
            File.WriteAllText(removable, "stale");
            Console.SetError(error);

            var removed = Http3NativeBootstrap.RemoveStaleBundledOpenSsl(dir, path =>
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

            Assert.AreEqual(0, removed);
            Assert.IsTrue(File.Exists(locked));
            Assert.IsTrue(File.Exists(removable));
            var text = error.ToString();
            StringAssert.Contains(text, "libssl.so.3");
            StringAssert.Contains(text, "libcrypto.so");
            StringAssert.Contains(text, "busy");
            StringAssert.Contains(text, "denied");
        }
        finally
        {
            Console.SetError(previous);
            Directory.Delete(dir, recursive: true);
        }
    }
}
