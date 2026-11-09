using System.Net;
using System.Reflection;
using System.Runtime.CompilerServices;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Abstractions.Scripting;
using Titanium.Web.Proxy.Scripting.Lua;
using Titanium.Web.Proxy.Scripting.Wasm;

namespace Titanium.Web.Proxy.Scripting.Tests.Wasm;

/// <summary>
///     Fat WasmHttpContext / WasmStreamContext leftovers plus LuaCodec Value/AsInt/ReadRaw edges that remain
///     thin on develop Sonar — no native Wasmtime required.
/// </summary>
[TestClass]
public class WasmHttpAndStreamContextBehaviorTests
{
    private static void SetBacking<T>(object target, string propertyName, T value)
    {
        var field = target.GetType().GetField(
            $"<{propertyName}>k__BackingField",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.IsNotNull(field, propertyName);
        field!.SetValue(target, value);
    }

    private static WasmPlugin PluginWithBodyLimit(long bufferedBodyBytes)
    {
        var plugin = (WasmPlugin)RuntimeHelpers.GetUninitializedObject(typeof(WasmPlugin));
        SetBacking(plugin, nameof(WasmPlugin.Limits), new WasmLimits { BufferedBodyBytes = bufferedBodyBytes });
        SetBacking(plugin, nameof(WasmPlugin.Configuration), "cfg"u8.ToArray());
        SetBacking(plugin, nameof(WasmPlugin.VmConfiguration), "vm"u8.ToArray());
        SetBacking(plugin, nameof(WasmPlugin.Name), "test-plugin");
        return plugin;
    }

    private static WasmHttpContext Http(WasmPlugin? plugin = null)
    {
        var context = new WasmHttpContext(null!, 1, 1, plugin!)
        {
            Last = new ScriptPhaseContext
            {
                Phase = ScriptPhases.RequestHeaders,
                Method = "GET",
                Path = "/",
                Host = "app.test",
                ClientEndPoint = new IPEndPoint(IPAddress.Loopback, 9),
                Headers = [],
                Chunk = ReadOnlyMemory<byte>.Empty,
                Items = new Dictionary<string, object?>(),
            },
        };
        context.BeginHeaders(ScriptPhases.RequestHeaders, new WasmHeaderMap(
        [
            new(":method", "GET"),
            new(":path", "/"),
            new(":authority", "app.test"),
        ]));
        return context;
    }

    private static WasmStreamContext Stream(WasmPlugin? plugin = null)
    {
        var connection = new StreamConnectionContext
        {
            ClientEndPoint = new IPEndPoint(IPAddress.Loopback, 1),
            LocalEndPoint = new IPEndPoint(IPAddress.Loopback, 2),
            Items = new Dictionary<string, object?>(),
        };
        return new WasmStreamContext(null!, 3, 3, plugin!, connection);
    }

    [TestMethod]
    public void Http_FinishPauseHoldsBody_AndOverLimitBeginBodyFails()
    {
        var plugin = PluginWithBodyLimit(8);
        var context = Http(plugin);
        Assert.IsTrue(context.BeginBody(ScriptPhases.RequestBody, "12345678"u8.ToArray(), end: false));
        var held = context.Finish(ProxyAction.Pause);
        CollectionAssert.AreEqual(Array.Empty<byte>(), held.ReplaceBody);
        Assert.IsFalse(context.Paused);

        Assert.IsFalse(context.BeginBody(ScriptPhases.RequestBody, "too-much"u8.ToArray(), end: true));
        // Held length already equals the limit; only an empty follow-up can merge.
        Assert.IsTrue(context.BeginBody(ScriptPhases.RequestBody, ReadOnlyMemory<byte>.Empty, end: true));
        Assert.IsTrue(context.BodyEnd);
        Assert.AreEqual(WasmResult.Ok, context.SetBuffer(ProxyBufferType.HttpRequestBody, 0, 8, "ABCDEFGH"u8.ToArray()));
        CollectionAssert.AreEqual("ABCDEFGH"u8.ToArray(), context.GetBuffer(ProxyBufferType.HttpRequestBody));
        var done = context.Finish(ProxyAction.Continue);
        CollectionAssert.AreEqual("ABCDEFGH"u8.ToArray(), done.ReplaceBody);
    }

    [TestMethod]
    public async Task Http_PauseResumeTimeout_AndCloseStreamDefaults()
    {
        var context = Http();
        Assert.AreEqual(ScriptAction.Pause, context.Finish(ProxyAction.Pause).Action);
        Assert.IsTrue(context.Paused);
        Assert.AreEqual(WasmResult.BadArgument, context.ContinueStream(ProxyStreamType.Downstream));
        Assert.AreEqual(WasmResult.Ok, context.ContinueStream(ProxyStreamType.HttpRequest));
        Assert.IsFalse(context.Paused);

        var timed = Http();
        Assert.AreEqual(ScriptAction.Pause, timed.Finish(ProxyAction.Pause).Action);
        Assert.IsFalse(await timed.WaitForResumeAsync(TimeSpan.FromMilliseconds(15), default));
        var deny = timed.Resolve(resumed: false);
        Assert.AreEqual(504, deny.LocalResponse!.Status);

        var closed = Http();
        Assert.AreEqual(WasmResult.Ok, closed.CloseStream(ProxyStreamType.HttpResponse));
        Assert.AreEqual(503, closed.LocalResponse!.Status);
        Assert.AreEqual(WasmResult.BadArgument, closed.CloseStream(ProxyStreamType.Downstream));
        Assert.IsNotNull(closed.GetMap(ProxyMapType.HttpRequestHeaders));
        Assert.IsNull(closed.GetMap(ProxyMapType.HttpResponseTrailers));
        var withPlugin = Http(PluginWithBodyLimit(1));
        CollectionAssert.AreEqual("cfg"u8.ToArray(), withPlugin.GetBuffer(ProxyBufferType.PluginConfiguration));
        CollectionAssert.AreEqual("vm"u8.ToArray(), withPlugin.GetBuffer(ProxyBufferType.VmConfiguration));
    }

    [TestMethod]
    public async Task Stream_PauseHoldContinueClose_AndClusterChoice()
    {
        var plugin = PluginWithBodyLimit(16);
        var context = Stream(plugin);
        Assert.IsTrue(context.BeginData(ProxyBufferType.DownstreamData, "abcd"u8.ToArray()));
        var held = context.Finish(ProxyAction.Pause, isData: true);
        Assert.IsNotNull(held);
        CollectionAssert.AreEqual(Array.Empty<byte>(), held!.ReplaceData);

        Assert.IsTrue(context.BeginData(ProxyBufferType.DownstreamData, "ef"u8.ToArray()));
        Assert.AreEqual(WasmResult.Ok, context.SetBuffer(ProxyBufferType.DownstreamData, 0, 6, "ABCDEF"u8.ToArray()));
        var replaced = context.Finish(ProxyAction.Continue, isData: true);
        CollectionAssert.AreEqual("ABCDEF"u8.ToArray(), replaced!.ReplaceData);
        context.EndData();

        Assert.IsTrue(context.BeginData(ProxyBufferType.UpstreamData, "ABCDEFGH"u8.ToArray()));
        context.Finish(ProxyAction.Pause, isData: true);
        Assert.IsFalse(context.BeginData(ProxyBufferType.UpstreamData, new byte[32]));
        Assert.IsTrue(context.BeginData(ProxyBufferType.UpstreamData, "up"u8.ToArray()));
        context.Properties[WasmHttpContext.UpstreamClusterProperty] = "picked"u8.ToArray();
        context.EndData();
        var newConn = context.Finish(ProxyAction.Continue, isData: false);
        Assert.AreEqual("picked", newConn!.ClusterId);

        var pauseWait = Stream();
        Assert.IsNull(pauseWait.Finish(ProxyAction.Pause, isData: false));
        Assert.IsTrue(pauseWait.Paused);
        Assert.AreEqual(WasmResult.BadArgument, pauseWait.ContinueStream(ProxyStreamType.HttpRequest));
        Assert.AreEqual(WasmResult.Ok, pauseWait.ContinueStream(ProxyStreamType.Downstream));
        Assert.IsTrue(await pauseWait.WaitForResumeAsync(TimeSpan.FromSeconds(1), default));

        var closed = Stream();
        Assert.AreEqual(WasmResult.Ok, closed.CloseStream(ProxyStreamType.Upstream));
        Assert.AreSame(StreamFilterResult.Deny, closed.Finish(ProxyAction.Continue, isData: true));
        Assert.AreEqual(StreamFilterResult.Deny, closed.Resolve(resumed: true, isData: true));
    }

    [TestMethod]
    public void LuaCodec_ValueAsIntReadRaw_AndDepthGuards()
    {
        var writer = new LuaWriter();
        writer.Value(null);
        writer.Value(true);
        writer.Value(3);
        writer.Value(4L);
        writer.Value(1.5);
        writer.Value("hi");
        writer.Value("bytes"u8.ToArray());
        writer.Value(Guid.NewGuid()); // default ToString branch
        var table = new LuaTable();
        table.Array.Add(null);
        table.Array.Add(9L);
        table.Fields["k"] = "v"u8.ToArray();
        writer.Value(table);
        var encoded = writer.ToArray();

        var reader = new LuaReader(encoded);
        Assert.IsNull(reader.Read());
        Assert.AreEqual(true, reader.Read());
        Assert.AreEqual(3L, reader.Read());
        Assert.AreEqual(4L, reader.Read());
        Assert.AreEqual(1.5, reader.Read());
        CollectionAssert.AreEqual("hi"u8.ToArray(), (byte[]?)reader.Read());
        CollectionAssert.AreEqual("bytes"u8.ToArray(), (byte[]?)reader.Read());
        Assert.IsInstanceOfType<byte[]>(reader.Read());
        Assert.IsInstanceOfType<LuaTable>(reader.Read());

        Assert.AreEqual(7L, LuaReader.AsInt(7L));
        Assert.AreEqual(8L, LuaReader.AsInt(8.0));
        Assert.AreEqual(9L, LuaReader.AsInt("9"u8.ToArray()));
        Assert.IsNull(LuaReader.AsInt(true));
        Assert.AreEqual("true", LuaReader.AsString(true));
        Assert.AreEqual("1.5", LuaReader.AsString(1.5));
        Assert.AreEqual("7", LuaReader.AsString(7L));

        var rawWriter = new LuaWriter();
        rawWriter.String("raw");
        var rawBytes = rawWriter.ToArray();
        var rawReader = new LuaReader(rawBytes);
        var span = rawReader.ReadRaw();
        CollectionAssert.AreEqual(rawBytes, span.ToArray());
        Assert.AreEqual(-1, new LuaReader("n"u8.ToArray()).ReadTableHeader());
        Assert.ThrowsExactly<FormatException>(() => new LuaReader([(byte)'z']).Read());
        Assert.ThrowsExactly<InvalidOperationException>(() =>
        {
            var deep = new LuaWriter();
            deep.Value(1, depth: 64);
        });
    }
}
