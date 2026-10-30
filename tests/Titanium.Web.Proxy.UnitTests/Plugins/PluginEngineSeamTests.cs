using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Abstractions.Middleware;
using Titanium.Web.Proxy.Abstractions.Plugins;
using Titanium.Web.Proxy.Plugins;

namespace Titanium.Web.Proxy.UnitTests.Plugins;

/// <summary><c>plugins[].kind</c> picks the loader; a script kind goes to a registered <see cref="IPluginEngine"/> or fails by name.</summary>
[TestClass]
[DoNotParallelize]
public class PluginEngineSeamTests
{
    [TestCleanup]
    public void Cleanup()
    {
        PluginEngines.Unregister(PluginKinds.Lua);
        PluginEngines.Unregister(PluginKinds.Wasm);
    }

    [TestMethod]
    public void AKindThatIsNotSet_IsDotNet()
    {
        Assert.AreEqual(PluginKinds.DotNet, PluginKinds.Normalize(null));
        Assert.AreEqual(PluginKinds.DotNet, PluginKinds.Normalize(""));
        Assert.AreEqual(PluginKinds.DotNet, PluginKinds.Normalize("  "));
        Assert.AreEqual(PluginKinds.Lua, PluginKinds.Normalize(" Lua "));
        Assert.IsNull(new PluginConfig().Kind, "the property stays null so an old config round-trips unchanged");
        Assert.IsFalse(PluginKinds.IsScript(null));
        Assert.IsTrue(PluginKinds.IsScript("WASM"));
        Assert.IsFalse(PluginKinds.IsKnown("python"));
    }

    [TestMethod]
    public void ADotNetPluginWithoutAKind_StillLoads()
    {
        using var host = PluginHost.Load([new PluginConfig { Name = "rec", Assembly = TestPluginsPath, TypeName = "Titanium.Web.Proxy.TestPlugins.RecordingPlugin" }]);
        Assert.IsTrue(host.Plugins.ContainsKey("rec"));

        using var explicitKind = PluginHost.Load(
            [new PluginConfig { Name = "rec", Kind = "dotnet", Assembly = TestPluginsPath, TypeName = "Titanium.Web.Proxy.TestPlugins.RecordingPlugin" }]);
        Assert.IsTrue(explicitKind.Plugins.ContainsKey("rec"));
        Assert.AreEqual(1, explicitKind.LoadContexts.Count);
    }

    [TestMethod]
    public void AScriptKindWithoutAnEngine_FailsAtLoadAndNamesTheKind()
    {
        Assert.IsFalse(PluginEngines.IsRegistered(PluginKinds.Lua));
        var ex = Assert.ThrowsExactly<InvalidOperationException>(
            () => PluginHost.Load([new PluginConfig { Name = "filter", Kind = "lua", File = "filter.lua" }]));
        StringAssert.Contains(ex.Message, "Plugin 'filter'");
        StringAssert.Contains(ex.Message, "kind 'lua'");
        StringAssert.Contains(ex.Message, "none is registered");

        var wasm = Assert.ThrowsExactly<InvalidOperationException>(
            () => PluginHost.Load([new PluginConfig { Name = "mod", Kind = "WASM", File = "mod.wasm" }]));
        StringAssert.Contains(wasm.Message, "kind 'wasm'");
    }

    [TestMethod]
    public void AnUnknownKind_FailsAtLoad()
    {
        var ex = Assert.ThrowsExactly<InvalidOperationException>(
            () => PluginHost.Load([new PluginConfig { Name = "x", Kind = "python", File = "x.py" }]));
        StringAssert.Contains(ex.Message, "kind 'python'");
    }

    [TestMethod]
    public void ARegisteredEngine_BuildsTheMiddleware_AndTheHostDisposesIt()
    {
        var engine = new FakeEngine(PluginKinds.Lua);
        PluginEngines.Register(engine);

        var host = PluginHost.Load([new PluginConfig { Name = "filter", Kind = "lua", File = "filter.lua" }]);
        Assert.IsTrue(host.Plugins.ContainsKey("filter"));
        Assert.AreEqual("filter.lua", engine.LastFile);
        Assert.AreEqual(0, host.LoadContexts.Count, "a script plugin has no assembly load context");

        host.Dispose();
        Assert.IsTrue(engine.Created!.Disposed);
    }

    [TestMethod]
    public void AnEngineThatCannotCreate_FailsTheLoadWithItsReason_AndDisposesThePluginsLoadedBefore()
    {
        PluginEngines.Register(new FakeEngine(PluginKinds.Lua) { Error = "syntax error near 'end'" });

        var ex = Assert.ThrowsExactly<InvalidOperationException>(() => PluginHost.Load(
        [
            new PluginConfig { Name = "good", Assembly = TestPluginsPath, TypeName = "Titanium.Web.Proxy.TestPlugins.RecordingPlugin" },
            new PluginConfig { Name = "bad", Kind = "lua", File = "bad.lua" },
        ]));
        StringAssert.Contains(ex.Message, "Plugin 'bad'");
        StringAssert.Contains(ex.Message, "syntax error near 'end'");
    }

    [TestMethod]
    public void ARegistrationFactory_RunsOnlyWhenAPluginOfThatKindLoads()
    {
        var created = 0;
        PluginEngines.Register(PluginKinds.Wasm, () =>
        {
            created++;
            return new FakeEngine(PluginKinds.Wasm);
        });
        Assert.IsTrue(PluginEngines.IsRegistered(PluginKinds.Wasm));
        Assert.AreEqual(0, created, "registering does not build the engine");

        using (PluginHost.Load([new PluginConfig { Name = "a", Kind = "wasm", File = "a.wasm" }, new PluginConfig { Name = "b", Kind = "wasm", File = "b.wasm" }]))
        {
            Assert.AreEqual(1, created, "the engine is built once, on first use");
        }

        Assert.ThrowsExactly<ArgumentException>(() => PluginEngines.Register("dotnet", () => new FakeEngine("dotnet")));
    }

    private static string TestPluginsPath => Path.Combine(AppContext.BaseDirectory, "Titanium.Web.Proxy.TestPlugins.dll");

    private sealed class FakeEngine(string kind) : IPluginEngine
    {
        public string Kind { get; } = kind;
        public string? Error { get; init; }
        public string? LastFile { get; private set; }
        public FakeMiddleware? Created { get; private set; }

        public bool TryCreate(PluginConfig config, ILogger? logger, out IProxyMiddleware? middleware, out string? error)
        {
            LastFile = config.File;
            error = Error;
            if (Error is not null)
            {
                middleware = null;
                return false;
            }

            Created = new FakeMiddleware();
            middleware = Created;
            return true;
        }
    }

    private sealed class FakeMiddleware : IProxyMiddleware, IDisposable
    {
        public bool Disposed { get; private set; }

        public ValueTask InvokeAsync(ProxyMiddlewareContext context, ProxyMiddlewareDelegate next, CancellationToken cancellationToken) =>
            next(context, cancellationToken);

        public void Dispose() => Disposed = true;
    }
}