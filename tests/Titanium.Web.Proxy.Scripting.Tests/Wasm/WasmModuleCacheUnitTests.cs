using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Scripting.Wasm;

namespace Titanium.Web.Proxy.Scripting.Tests.Wasm;

/// <summary>Disk-cache helpers that do not need the Wasmtime native beside the test host.</summary>
[TestClass]
public class WasmModuleCacheUnitTests
{
    [TestMethod]
    public void PathFor_IsStableForTheSameHashAndFingerprint()
    {
        var cache = new WasmModuleCache(Path.Combine(Path.GetTempPath(), "twp-cache-" + Guid.NewGuid().ToString("N")));
        var first = cache.PathFor("mod", "fp");
        var second = cache.PathFor("mod", "fp");
        Assert.AreEqual(first, second);
        StringAssert.EndsWith(first, ".cwasm");
        Assert.AreNotEqual(cache.PathFor("mod", "fp"), cache.PathFor("other", "fp"));
    }

    [TestMethod]
    public void TryRead_ReturnsNull_WhenEntryIsMissing()
    {
        var cache = new WasmModuleCache(Path.Combine(Path.GetTempPath(), "twp-cache-" + Guid.NewGuid().ToString("N")));
        Assert.IsNull(cache.TryRead("missing", "fp", out var warning));
        Assert.IsNull(warning);
    }

    [TestMethod]
    public void Discard_AndTryWrite_RoundTripOnATrustedDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), "twp-cache-" + Guid.NewGuid().ToString("N"));
        try
        {
            var cache = new WasmModuleCache(directory);
            Assert.IsTrue(cache.TryWrite("mod", "fp", [1, 2, 3, 4], out var writeWarning), writeWarning);
            Assert.IsNull(writeWarning);
            var bytes = cache.TryRead("mod", "fp", out var readWarning);
            Assert.IsNull(readWarning);
            CollectionAssert.AreEqual(new byte[] { 1, 2, 3, 4 }, bytes);

            cache.Discard("mod", "fp");
            Assert.IsNull(cache.TryRead("mod", "fp", out _));
            cache.Discard("mod", "fp"); // second discard is a no-op
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }
}
