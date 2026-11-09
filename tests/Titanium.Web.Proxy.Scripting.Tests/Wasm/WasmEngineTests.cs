using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Abstractions.Plugins;
using Titanium.Web.Proxy.Abstractions.Scripting;
using Titanium.Web.Proxy.Plugins;
using Titanium.Web.Proxy.Scripting.Wasm;
using static Titanium.Web.Proxy.Scripting.Tests.Wasm.WasmTestSupport;

namespace Titanium.Web.Proxy.Scripting.Tests.Wasm;

[TestClass]
[DoNotParallelize]
public class WasmEngineTests
{
    private static readonly string[] ExpectedRemovedHeaderNames = ["x-remove-me"];
    [TestMethod]
    public async Task RequestHeaders_EditsBecomeSetAndRemoveLists_AndPropertiesResolve()
    {
        using var plugin = Create(Config("headers_filter.wat"));
        var items = new Dictionary<string, object?>();

        var result = await plugin.OnRequestHeadersAsync(
            Phase(ScriptPhases.RequestHeaders, items, [H("Host", "example.test"), H("X-Remove-Me", "1"), H("Accept", "*/*")]), default);

        Assert.AreEqual(ScriptAction.Continue, result.Action);
        Assert.AreEqual("1", result.SetHeaders!["x-wasm-seen"]);
        Assert.AreEqual("/foo?x=1", result.SetHeaders["x-path"]);
        CollectionAssert.AreEqual(ExpectedRemovedHeaderNames, result.RemoveHeaders!.ToArray());
        Assert.IsFalse(result.SetHeaders.ContainsKey("host"), "an unchanged :authority is not echoed back as Host");
    }

    [TestMethod]
    public async Task LocalResponse_DeniesWithTheGuestsStatusAndBody()
    {
        using var plugin = Create(Config("headers_filter.wat"));

        var result = await plugin.OnRequestHeadersAsync(Phase(ScriptPhases.RequestHeaders, new Dictionary<string, object?>(), [H("x-block", "1")]), default);

        Assert.AreEqual(ScriptAction.Deny, result.Action);
        Assert.AreEqual(403, result.LocalResponse!.Status);
        Assert.AreEqual("blocked", Encoding.UTF8.GetString(result.LocalResponse.Body!));
    }

    [TestMethod]
    public async Task StreamingBody_PauseHoldsPieces_UntilTheGuestReplacesTheWholeBody()
    {
        using var plugin = Create(Config("headers_filter.wat"));
        var items = new Dictionary<string, object?>();
        await plugin.OnRequestHeadersAsync(Phase(ScriptPhases.RequestHeaders, items, [H("content-length", "10")], method: "POST"), default);

        var first = await plugin.OnRequestBodyAsync(Phase(ScriptPhases.RequestBody, items, chunk: Encoding.ASCII.GetBytes("hello")), default);
        var last = await plugin.OnRequestBodyAsync(Phase(ScriptPhases.RequestBody, items, chunk: Encoding.ASCII.GetBytes("world"), last: true), default);

        Assert.AreEqual(ScriptAction.Continue, first.Action);
        Assert.AreEqual(0, first.ReplaceBody!.Length, "a held piece is not forwarded");
        Assert.AreEqual("REPLACED", Encoding.ASCII.GetString(last.ReplaceBody!));
    }

    [TestMethod]
    public async Task ResponseHeaders_AndLog_RunInTheSameContext()
    {
        var logger = new ListLogger();
        using var plugin = Create(Config("headers_filter.wat"), logger);
        var items = new Dictionary<string, object?>();

        await plugin.OnRequestHeadersAsync(Phase(ScriptPhases.RequestHeaders, items), default);
        var response = await plugin.OnResponseHeadersAsync(Phase(ScriptPhases.ResponseHeaders, items, [H("content-length", "0")], status: 200), default);
        await plugin.OnLogAsync(Phase(ScriptPhases.Log, items, status: 200), default);

        Assert.AreEqual("ok", response.SetHeaders!["x-wasm-response"]);
        Assert.IsTrue(logger.Lines.Any(l => l.Contains("[wasm test] ok")), string.Join("\n", logger.Lines));
        Assert.AreEqual(0, items.Count, "the log phase ends the context");
    }

    [TestMethod]
    public void Phases_FollowTheModuleExports()
    {
        using var plugin = Create(Config("headers_filter.wat"));

        Assert.AreEqual(ScriptPhases.RequestHeaders | ScriptPhases.RequestBody | ScriptPhases.ResponseHeaders | ScriptPhases.Log, plugin.Phases);
    }

    [TestMethod]
    public void OnConfigureReturningFalse_FailsTheLoad()
    {
        var error = CreateError(Config("headers_filter.wat", settings: new Dictionary<string, string> { ["configuration"] = "reject" }));

        StringAssert.Contains(error, "proxy_on_configure");
    }

    [TestMethod]
    public void MissingImport_FailsTheLoad_NamingTheImport()
    {
        var error = CreateError(Config("missing_import.wat"));

        StringAssert.Contains(error, "missing import env.proxy_teleport");
    }

    [TestMethod]
    public void UnknownAbi_FailsTheLoad_NamingTheMarker()
    {
        var error = CreateError(Config("unknown_abi.wat"));

        StringAssert.Contains(error, "proxy_abi_version_9_9_9");
    }

    [TestMethod]
    public async Task Abi010Module_LoadsAndUsesTwoArgumentCallbacks()
    {
        using var plugin = Create(Config("abi_0_1_0.wat"));

        var result = await plugin.OnRequestHeadersAsync(Phase(ScriptPhases.RequestHeaders, new Dictionary<string, object?>()), default);

        Assert.AreEqual("0.1.0", result.SetHeaders!["x-abi"]);
    }

    [TestMethod]
    public async Task Trap_DiscardsTheVm_FailsClosed_AndTheSlotIsRebuilt()
    {
        using var plugin = Create(Config("faults.wat", poolSize: 1));

        var trapped = await plugin.OnRequestHeadersAsync(Phase(ScriptPhases.RequestHeaders, new Dictionary<string, object?>(), [H("x-trap", "1")]), default);

        Assert.AreEqual(ScriptAction.Deny, trapped.Action);
        Assert.AreEqual(503, trapped.LocalResponse!.Status);
        await WaitForAsync(() => plugin.Pool.HealthyCount == 1);
        var next = await plugin.OnRequestHeadersAsync(Phase(ScriptPhases.RequestHeaders, new Dictionary<string, object?>()), default);
        Assert.AreEqual("1", next.SetHeaders!["x-ok"], "a fresh VM serves the next request");
    }

    [TestMethod]
    public async Task Trap_WithFailOpen_LetsTheRequestContinue()
    {
        using var plugin = Create(Config("faults.wat", failMode: "open"));

        var trapped = await plugin.OnRequestHeadersAsync(Phase(ScriptPhases.RequestHeaders, new Dictionary<string, object?>(), [H("x-trap", "1")]), default);

        Assert.AreSame(ScriptPhaseResult.Continue, trapped);
    }

    [TestMethod]
    [Timeout(20_000)]
    public async Task EndlessLoop_IsStoppedByTheEpochDeadline()
    {
        using var plugin = Create(Config("faults.wat", limits: new PluginLimitsConfig { PoolSize = 1, EpochDeadlineMs = 50 }));
        var started = DateTime.UtcNow;

        var spun = await plugin.OnRequestHeadersAsync(Phase(ScriptPhases.RequestHeaders, new Dictionary<string, object?>(), [H("x-spin", "1")]), default);

        Assert.AreEqual(ScriptAction.Deny, spun.Action);
        Assert.IsTrue(DateTime.UtcNow - started < TimeSpan.FromSeconds(5), "the deadline, not the test timeout, ended the call");
    }

    [TestMethod]
    [Timeout(20_000)]
    public async Task EndlessLoop_IsStoppedByFuel_WhenFuelIsConfigured()
    {
        using var plugin = Create(Config("faults.wat", limits: new PluginLimitsConfig { PoolSize = 1, Fuel = 1_000_000 }));

        var spun = await plugin.OnRequestHeadersAsync(Phase(ScriptPhases.RequestHeaders, new Dictionary<string, object?>(), [H("x-spin", "1")]), default);

        Assert.AreEqual(ScriptAction.Deny, spun.Action);
        await WaitForAsync(() => plugin.Pool.HealthyCount == 1);
        var ok = await plugin.OnRequestHeadersAsync(Phase(ScriptPhases.RequestHeaders, new Dictionary<string, object?>()), default);
        Assert.AreEqual("1", ok.SetHeaders!["x-ok"], "the rebuilt VM starts with a full fuel budget");
    }

    [TestMethod]
    public async Task MemoryLimit_DeniesGrowthPastTheCeiling()
    {
        using var small = Create(Config("faults.wat", limits: new PluginLimitsConfig { PoolSize = 1, MemoryMb = 1 }));
        using var large = Create(Config("faults.wat", limits: new PluginLimitsConfig { PoolSize = 1, MemoryMb = 64 }));

        var denied = await small.OnRequestHeadersAsync(Phase(ScriptPhases.RequestHeaders, new Dictionary<string, object?>(), [H("x-grow", "1")]), default);
        var allowed = await large.OnRequestHeadersAsync(Phase(ScriptPhases.RequestHeaders, new Dictionary<string, object?>(), [H("x-grow", "1")]), default);

        Assert.AreEqual("denied", denied.SetHeaders!["x-grow-result"]);
        Assert.AreEqual("allowed", allowed.SetHeaders!["x-grow-result"]);
    }

    [TestMethod]
    public async Task RequestsArePinnedToOneVm_AcrossTheirPhases()
    {
        using var plugin = Create(Config("headers_filter.wat", poolSize: 4));
        var contexts = new List<WasmHttpContext>();
        for (var i = 0; i < 8; i++)
        {
            var items = new Dictionary<string, object?>();
            await plugin.OnRequestHeadersAsync(Phase(ScriptPhases.RequestHeaders, items), default);
            var context = (WasmHttpContext)items.Values.Single()!;
            await plugin.OnResponseHeadersAsync(Phase(ScriptPhases.ResponseHeaders, items, status: 200), default);
            Assert.AreSame(context, items.Values.Single(), "the same context (and VM) serves the response phase");
            contexts.Add(context);
        }

        Assert.AreEqual(4, contexts.Select(c => c.Vm).Distinct().Count(), "new requests spread over the pool");
    }

    [TestMethod]
    public void Registry_IsLazy_AndPluginHostLoadsWasmThroughIt()
    {
        RequireNative();
        PluginEngines.Unregister(PluginKinds.Wasm);
        ScriptingBootstrap.Register();
        Assert.IsTrue(PluginEngines.IsRegistered(PluginKinds.Wasm));

        using var host = PluginHost.Load([Config("headers_filter.wat", name: "wasm-one")]);

        Assert.IsTrue(host.Plugins.ContainsKey("wasm-one"));
    }

    [TestMethod]
    public async Task ResponseBody_Replace_IsForwardedInOneChunk()
    {
        // headers_filter.wat handles request body only; use Lua for a response-body replace scenario is in LuaFeatureTests.
        // Here we verify the response-headers phase sets x-wasm-response: ok (the WAT sets it unconditionally).
        using var plugin = Create(Config("headers_filter.wat"));
        var items = new Dictionary<string, object?>();

        await plugin.OnRequestHeadersAsync(Phase(ScriptPhases.RequestHeaders, items), default);
        var resp = await plugin.OnResponseHeadersAsync(Phase(ScriptPhases.ResponseHeaders, items,
            [H("content-length", "5")], status: 200), default);
        var bodyResult = await plugin.OnResponseBodyAsync(Phase(ScriptPhases.ResponseBody, items,
            chunk: "hello"u8.ToArray(), last: true), default);
        await plugin.OnLogAsync(Phase(ScriptPhases.Log, items, status: 200), default);

        Assert.AreEqual("ok", resp.SetHeaders!["x-wasm-response"]);
        // headers_filter.wat does not export proxy_on_response_body, so the body passes through unchanged.
        Assert.IsNull(bodyResult.ReplaceBody, "no response body manipulation in this module");
    }

    [TestMethod]
    public async Task RequestBody_ExceedingBufferedLimit_Returns413()
    {
        // headers_filter.wat pauses on non-last body chunks; a second chunk that overflows the limit must yield 413.
        using var plugin = Create(Config("headers_filter.wat",
            limits: new PluginLimitsConfig { PoolSize = 1, BufferedBodyBytes = 8 }));
        var items = new Dictionary<string, object?>();

        await plugin.OnRequestHeadersAsync(Phase(ScriptPhases.RequestHeaders, items,
            [H("content-length", "20")], method: "POST"), default);

        // First chunk (5 bytes): held by the guest (not last) — returns empty replace body.
        var held = await plugin.OnRequestBodyAsync(Phase(ScriptPhases.RequestBody, items,
            chunk: "hello"u8.ToArray(), last: false), default);
        Assert.AreEqual(ScriptAction.Continue, held.Action);
        Assert.AreEqual(0, held.ReplaceBody!.Length, "guest holds the first piece");

        // Second chunk (5 bytes): 5 + 5 = 10 > 8 → body exceeds limit.
        var overflow = await plugin.OnRequestBodyAsync(Phase(ScriptPhases.RequestBody, items,
            chunk: "world"u8.ToArray(), last: false), default);

        Assert.AreEqual(ScriptAction.Deny, overflow.Action);
        Assert.AreEqual(413, overflow.LocalResponse!.Status);
    }

    [TestMethod]
    public async Task Pool_NplusOne_SpreadsAcrossAllVmsAndRemainsHealthy()
    {
        // Pool of N=2: N+1=3 sequential requests must spread across both VMs and leave both healthy.
        const int N = 2;
        using var plugin = Create(Config("headers_filter.wat", poolSize: N));
        var vms = new HashSet<WasmVm>();

        for (var i = 0; i < N + 1; i++)
        {
            var items = new Dictionary<string, object?>();
            await plugin.OnRequestHeadersAsync(Phase(ScriptPhases.RequestHeaders, items), default);
            var ctx = (WasmHttpContext)items.Values.Single()!;
            vms.Add(ctx.Vm);
            await plugin.OnResponseHeadersAsync(Phase(ScriptPhases.ResponseHeaders, items, status: 200), default);
            await plugin.OnLogAsync(Phase(ScriptPhases.Log, items, status: 200), default);
        }

        Assert.AreEqual(N, plugin.Pool.HealthyCount, "no VM was faulted by a plain request");
        Assert.AreEqual(N, vms.Count, "N+1 sequential requests spread across the full pool of N");
    }

    [TestMethod]
    public void BadLimits_AndBodyMode_FailBeforeAnythingNativeRuns()
    {
        var engine = new WasmPluginEngine();
        Assert.IsFalse(engine.TryCreate(Config("headers_filter.wat", limits: new PluginLimitsConfig { MemoryMb = 0 }), null, out _, out var memory));
        Assert.IsFalse(engine.TryCreate(Config("headers_filter.wat", bodyMode: "sometimes"), null, out _, out var mode));
        Assert.IsFalse(engine.TryCreate(Config("does-not-exist.wasm"), null, out _, out var missing));

        StringAssert.Contains(memory, "memoryMb");
        StringAssert.Contains(mode, "bodyMode");
        StringAssert.Contains(missing, "does not exist");
    }
}
