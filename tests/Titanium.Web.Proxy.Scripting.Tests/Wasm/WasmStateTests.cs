using System.Diagnostics.Metrics;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Abstractions.Plugins;
using Titanium.Web.Proxy.Abstractions.Scripting;
using Titanium.Web.Proxy.Scripting.Wasm;
using static Titanium.Web.Proxy.Scripting.Tests.Wasm.WasmTestSupport;

namespace Titanium.Web.Proxy.Scripting.Tests.Wasm;

[TestClass]
[DoNotParallelize]
public class WasmStateTests
{
    [TestMethod]
    public async Task HostState_SharedDataCas_Queue_Metrics_ForeignFunctions_AndWasi_Work()
    {
        var logger = new ListLogger();
        using var plugin = Create(Config("state.wat"), logger);

        var result = await Request(plugin);

        var set = result.SetHeaders!;
        Assert.AreEqual("v2", set["x-shared"]);
        Assert.AreEqual("1", set["x-cas-mismatch"], "a stale CAS must not write");
        Assert.AreEqual("1", set["x-resolve"]);
        Assert.AreEqual("1", set["x-metric"]);
        Assert.AreEqual("ok", set["x-ff"]);
        Assert.AreEqual("1", set["x-clock"]);
        Assert.AreEqual("1", set["x-random"]);
        Assert.AreEqual("1", set["x-stdout"]);
        await WaitForAsync(() => logger.Lines.Any(l => l.EndsWith("[wasm test] hello", StringComparison.Ordinal)));
        await WaitForAsync(() => logger.Lines.Any(l => l.EndsWith("[wasm test] tick", StringComparison.Ordinal)));
        Assert.IsTrue(logger.Lines.Any(l => l.StartsWith("Information:", StringComparison.Ordinal) && l.EndsWith("stdout-line", StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task VmId_SharesOnePool_AndSharedDataAcrossPools()
    {
        using var first = Create(Config("state.wat", name: "a", vmId: "shared"));
        using var second = Create(Config("state.wat", name: "b", vmId: "shared"));
        using var otherLimits = Create(Config("state.wat", name: "c", vmId: "shared", poolSize: 2));
        using var otherVm = Create(Config("state.wat", name: "d", vmId: "other"));

        Assert.AreSame(first.Pool, second.Pool, "same vm_id, module and limits share VMs");
        Assert.AreNotEqual(first.RootId, second.RootId);
        Assert.AreNotSame(first.Pool, otherLimits.Pool);

        Assert.IsFalse((await Request(first)).SetHeaders!.ContainsKey("x-shared-found"));
        Assert.AreEqual("1", (await Request(second)).SetHeaders!["x-shared-found"]);
        Assert.AreEqual("1", (await Request(otherLimits)).SetHeaders!["x-shared-found"], "shared data follows vm_id, not the pool");
        Assert.IsFalse((await Request(otherVm)).SetHeaders!.ContainsKey("x-shared-found"), "another vm_id has its own data");

        first.Dispose();
        Assert.AreEqual("v2", (await Request(second)).SetHeaders!["x-shared"], "the pool outlives one of its plugins");
    }

    [TestMethod]
    public async Task GuestMetrics_ArePublishedOnTheScriptingMeter()
    {
        using var plugin = Create(Config("state.wat"));
        await Request(plugin);
        long counter = 0;
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == "Titanium.Web.Proxy.Scripting" && instrument.Name == "twp.scripting.wasm.guest.counter")
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((_, value, tags, _) =>
        {
            foreach (var tag in tags)
            {
                if (tag.Key == "metric" && Equals(tag.Value, "requests"))
                {
                    counter = value;
                }
            }
        });
        listener.Start();
        listener.RecordObservableInstruments();

        Assert.IsTrue(counter >= 1, $"requests counter was {counter}");
    }

    [TestMethod]
    public void ModuleCache_StoresAndReloadsCompiledModules_AndRecoversFromACorruptEntry()
    {
        var directory = Path.Combine(Path.GetTempPath(), "twp-wasm-cache-" + Guid.NewGuid().ToString("N"));
        try
        {
            var cache = new WasmModuleCache(directory);
            using (var cold = Create(Config("headers_filter.wat"), cache: cache))
            {
                Assert.IsFalse(cold.Pool.Module.FromCache);
            }

            var entry = Directory.GetFiles(directory, "*.cwasm").Single();
            using (var warm = Create(Config("headers_filter.wat"), cache: cache))
            {
                Assert.IsTrue(warm.Pool.Module.FromCache);
            }

            File.WriteAllBytes(entry, [1, 2, 3]);
            using (var recovered = Create(Config("headers_filter.wat"), cache: cache))
            {
                Assert.IsFalse(recovered.Pool.Module.FromCache);
            }

            Assert.IsTrue(new FileInfo(entry).Length > 3, "a refused entry is rewritten");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [TestMethod]
    public void ModuleCache_IsNotUsedInADirectoryOthersCanWrite()
    {
        var directory = Path.Combine(Path.GetTempPath(), "twp-wasm-cache-open-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            if (OperatingSystem.IsWindows())
            {
                var info = new DirectoryInfo(directory);
                var security = info.GetAccessControl();
                security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.WorldSid, null),
                    FileSystemRights.Modify, AccessControlType.Allow));
                info.SetAccessControl(security);
            }
            else
            {
                File.SetUnixFileMode(directory, (UnixFileMode)0b111_111_111);
            }

            Assert.IsFalse(WasmModuleCache.IsTrusted(directory, out var reason));
            StringAssert.Contains(reason, "module cache is not used");

            using var plugin = Create(Config("headers_filter.wat"), cache: new WasmModuleCache(directory));
            Assert.IsFalse(plugin.Pool.Module.FromCache);
            Assert.AreEqual(0, Directory.GetFiles(directory).Length, "nothing is written to an untrusted directory");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [TestMethod]
    public void WasiCommand_ThatExitsZero_Loads_AndOneThatExitsNonZero_Fails()
    {
        using (Create(Config("wasi_exit0.wat")))
        {
        }

        StringAssert.Contains(CreateError(Config("wasi_exit1.wat")), "proc_exit(3)");
    }

    [TestMethod]
    public void ForeignFunctions_CanBeRegisteredAndRemoved()
    {
        WasmForeignFunctions.Register("twp.test.reverse", static input =>
        {
            var output = input.ToArray();
            Array.Reverse(output);
            return output;
        });
        try
        {
            Assert.IsTrue(WasmForeignFunctions.TryGet("twp.test.reverse", out var function));
            CollectionAssert.AreEqual(new byte[] { 3, 2, 1 }, function([1, 2, 3]));
        }
        finally
        {
            Assert.IsTrue(WasmForeignFunctions.Unregister("twp.test.reverse"));
        }

        Assert.IsFalse(WasmForeignFunctions.TryGet("twp.test.reverse", out _));
        Assert.IsTrue(WasmForeignFunctions.TryGet("compress", out _));
    }

    private static async Task<ScriptPhaseResult> Request(WasmPlugin plugin)
    {
        var items = new Dictionary<string, object?>();
        var result = await plugin.OnRequestHeadersAsync(Phase(ScriptPhases.RequestHeaders, items), default);
        await plugin.OnLogAsync(Phase(ScriptPhases.Log, items), default);
        Assert.AreEqual(ScriptAction.Continue, result.Action);
        return result;
    }
}
