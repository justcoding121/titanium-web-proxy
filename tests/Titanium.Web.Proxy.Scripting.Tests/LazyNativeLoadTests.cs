using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Abstractions.Plugins;
using Titanium.Web.Proxy.Scripting.Lua;
using Titanium.Web.Proxy.Scripting.Lua.Native;
using Titanium.Web.Proxy.Scripting.Wasm;

namespace Titanium.Web.Proxy.Scripting.Tests;

/// <summary>
/// N7, engine side: registering the engines, building them, and refusing a bad configuration never ask for a native
/// library. Only a plugin that gets as far as running does. The same process also runs tests that load the natives, so each
/// test compares against the state it found; the process-wide "nothing loaded" check on a default reverse configuration is
/// <c>ScriptingNothingLoadedTests</c> in <c>Titanium.Cli.Tests</c>, which no native-loading test shares a process with.
/// </summary>
[TestClass]
[DoNotParallelize]
public class LazyNativeLoadTests
{
    [TestMethod]
    public void Register_AndEngineConstruction_DoNotRequestEitherNative()
    {
        var lua = LuaNative.LoadAttempted;
        var wasm = WasmRuntime.LoadAttempted;

        ScriptingBootstrap.Register();
        ScriptingBootstrap.Register();
        Assert.IsTrue(PluginEngines.IsRegistered(PluginKinds.Lua));
        Assert.IsTrue(PluginEngines.IsRegistered(PluginKinds.Wasm));

        Assert.IsTrue(PluginEngines.TryGet(PluginKinds.Lua, out var luaEngine));
        Assert.IsTrue(PluginEngines.TryGet(PluginKinds.Wasm, out var wasmEngine));
        Assert.IsInstanceOfType<LuaPluginEngine>(luaEngine);
        Assert.IsInstanceOfType<WasmPluginEngine>(wasmEngine);

        Assert.AreEqual(lua, LuaNative.LoadAttempted, "building the Lua engine asked for lua54");
        Assert.AreEqual(wasm, WasmRuntime.LoadAttempted, "building the Wasm engine asked for wasmtime");
    }

    [TestMethod]
    public void AConfigurationTheEngineRefusesBeforeRunning_NeverRequestsTheNative()
    {
        var lua = LuaNative.LoadAttempted;
        var wasm = WasmRuntime.LoadAttempted;

        Assert.IsFalse(new LuaPluginEngine().TryCreate(
            new PluginConfig { Name = "no-file", Kind = PluginKinds.Lua }, null, out var luaPlugin, out var luaError));
        Assert.IsNull(luaPlugin);
        Assert.IsNotNull(luaError);

        Assert.IsFalse(new WasmPluginEngine().TryCreate(
            new PluginConfig { Name = "no-file", Kind = PluginKinds.Wasm }, null, out var wasmPlugin, out var wasmError));
        Assert.IsNull(wasmPlugin);
        Assert.IsNotNull(wasmError);

        var missing = Path.Combine(Path.GetTempPath(), "twp-n7-missing-" + Guid.NewGuid().ToString("N"));
        Assert.IsFalse(new WasmPluginEngine().TryCreate(
            new PluginConfig { Name = "missing", Kind = PluginKinds.Wasm, File = missing + ".wasm" }, null, out _, out _));

        Assert.AreEqual(lua, LuaNative.LoadAttempted, "a refused Lua configuration asked for lua54");
        Assert.AreEqual(wasm, WasmRuntime.LoadAttempted, "a refused Wasm configuration asked for wasmtime");
    }

    [TestMethod]
    public void TheLoadFlag_ReportsARealRequest()
    {
        // The flags the two tests above rely on must move when a native is genuinely requested.
        _ = WasmRuntime.TryLoadNative(out _);
        Assert.IsTrue(WasmRuntime.LoadAttempted);

        _ = LuaNative.TryLoad(out _);
        Assert.IsTrue(LuaNative.LoadAttempted);
    }
}
