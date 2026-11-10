using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Scripting.Wasm;

namespace Titanium.Web.Proxy.Scripting.Tests.Wasm;

/// <summary>
///     Pure shared-data CAS / budget / release for one vm_id (no wasmtime).
/// </summary>
[TestClass]
public class WasmSharedStateBehaviorTests
{
    [TestMethod]
    public void Set_CasMismatch_AndOverBudget_Fail_ReleaseDropsScope()
    {
        var vmId = "shared-" + Guid.NewGuid().ToString("N");
        var limits = new WasmLimits { SharedDataBytes = 64, QueueBytes = 1024 };
        var scope = WasmSharedState.Acquire(vmId, limits);
        try
        {
            Assert.AreEqual(WasmResult.Ok, scope.Set("k", "one"u8.ToArray(), cas: 0));
            Assert.AreEqual(WasmResult.Ok, scope.Get("k", out var value, out var cas));
            CollectionAssert.AreEqual("one"u8.ToArray(), value);
            Assert.AreNotEqual(0u, cas);

            Assert.AreEqual(WasmResult.CasMismatch, scope.Set("k", "two"u8.ToArray(), cas: cas + 1));
            Assert.AreEqual(WasmResult.Ok, scope.Get("k", out var still, out _));
            CollectionAssert.AreEqual("one"u8.ToArray(), still);

            Assert.AreEqual(WasmResult.Ok, scope.Set("k", "two"u8.ToArray(), cas));
            Assert.AreEqual(WasmResult.InternalFailure, scope.Set("huge", Encoding.UTF8.GetBytes(new string('x', 80)), cas: 0));
        }
        finally
        {
            scope.Release();
        }

        var again = WasmSharedState.Acquire(vmId, limits);
        try
        {
            Assert.AreEqual(WasmResult.NotFound, again.Get("k", out _, out _));
        }
        finally
        {
            again.Release();
        }
    }
}
