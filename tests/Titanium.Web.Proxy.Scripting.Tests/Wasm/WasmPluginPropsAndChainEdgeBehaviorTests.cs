using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Abstractions.Plugins;
using Titanium.Web.Proxy.Abstractions.Scripting;
using Titanium.Web.Proxy.Scripting.Wasm;

namespace Titanium.Web.Proxy.Scripting.Tests.Wasm;

/// <summary>
///     Scripting leftovers toward 90%: WasmProperties plugin metadata / ChosenCluster, and
///     ScriptPhaseHostChain empty/remove-only/immediate-deny edges.
/// </summary>
[TestClass]
public class WasmPluginPropsAndChainEdgeBehaviorTests
{
    private static void SetBacking<T>(object target, string propertyName, T value)
    {
        var field = target.GetType().GetField($"<{propertyName}>k__BackingField",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        Assert.IsNotNull(field, propertyName);
        field!.SetValue(target, value);
    }

    private static string Utf8(byte[]? bytes) => bytes is null ? "<null>" : Encoding.UTF8.GetString(bytes);

    [TestMethod]
    public void Get_ReturnsPluginMetadata_AndForStreamPrefersChosenCluster()
    {
        var plugin = (WasmPlugin)RuntimeHelpers.GetUninitializedObject(typeof(WasmPlugin));
        SetBacking(plugin, nameof(WasmPlugin.Name), "edge-filter");
        SetBacking(plugin, nameof(WasmPlugin.VmId), "vm-9");
        SetBacking(plugin, nameof(WasmPlugin.Limits), new WasmLimits());
        SetBacking(plugin, nameof(WasmPlugin.Configuration), Array.Empty<byte>());
        SetBacking(plugin, nameof(WasmPlugin.VmConfiguration), Array.Empty<byte>());

        var http = new WasmHttpContext(null!, 1, 1, plugin)
        {
            Last = new ScriptPhaseContext
            {
                Phase = ScriptPhases.RequestHeaders,
                Method = "GET",
                Path = "/",
                Host = "h",
                Headers = [],
                Chunk = ReadOnlyMemory<byte>.Empty,
                Items = new Dictionary<string, object?>(),
            },
        };

        Assert.AreEqual("edge-filter", Utf8(WasmProperties.Get(http, "plugin_name")));
        Assert.AreEqual("edge-filter", Utf8(WasmProperties.Get(http, "xds.filter_chain_name")));
        Assert.AreEqual("edge-filter", Utf8(WasmProperties.Get(http, "plugin_root_id")));
        Assert.AreEqual("vm-9", Utf8(WasmProperties.Get(http, "plugin_vm_id")));

        var connection = new StreamConnectionContext
        {
            ClientEndPoint = new IPEndPoint(IPAddress.Loopback, 1),
            LocalEndPoint = new IPEndPoint(IPAddress.Loopback, 2),
            ClusterId = "connection-cluster",
            Items = new Dictionary<string, object?>(),
        };
        var stream = new WasmStreamContext(null!, 3, 3, plugin, connection);
        stream.Properties[WasmHttpContext.UpstreamClusterProperty] = Encoding.UTF8.GetBytes("chosen-cluster");
        Assert.AreEqual("chosen-cluster", Utf8(WasmProperties.ForStream(stream, "cluster_name")));
        Assert.AreEqual("chosen-cluster", Utf8(WasmProperties.ForStream(stream, "xds.cluster_name")));
    }

    private sealed class RecordingHost(
        ScriptPhases phases,
        Func<ScriptPhaseContext, ScriptPhaseResult>? respond = null) : IScriptPhaseHost
    {
        public int Calls { get; private set; }

        public ScriptPhases Phases => phases;

        public ScriptBodyMode BodyMode => ScriptBodyMode.Streaming;

        public ValueTask<ScriptPhaseResult> OnRequestHeadersAsync(ScriptPhaseContext context, CancellationToken cancellationToken)
        {
            Calls++;
            return new(respond?.Invoke(context) ?? ScriptPhaseResult.Continue);
        }

        public ValueTask<ScriptPhaseResult> OnRequestBodyAsync(ScriptPhaseContext context, CancellationToken cancellationToken) =>
            new(ScriptPhaseResult.Continue);

        public ValueTask<ScriptPhaseResult> OnRequestTrailersAsync(ScriptPhaseContext context, CancellationToken cancellationToken) =>
            new(ScriptPhaseResult.Continue);

        public ValueTask<ScriptPhaseResult> OnResponseHeadersAsync(ScriptPhaseContext context, CancellationToken cancellationToken) =>
            new(ScriptPhaseResult.Continue);

        public ValueTask<ScriptPhaseResult> OnResponseBodyAsync(ScriptPhaseContext context, CancellationToken cancellationToken) =>
            new(ScriptPhaseResult.Continue);

        public ValueTask<ScriptPhaseResult> OnResponseTrailersAsync(ScriptPhaseContext context, CancellationToken cancellationToken) =>
            new(ScriptPhaseResult.Continue);
    }

    [TestMethod]
    public async Task ScriptPhaseHostChain_EmptyRemoveOnlyAndImmediateDeny()
    {
        var empty = new ScriptPhaseHostChain([]);
        Assert.AreEqual((ScriptPhases)0, empty.Phases);
        Assert.AreEqual(ScriptBodyMode.Streaming, empty.BodyMode);
        Assert.AreSame(
            ScriptPhaseResult.Continue,
            await empty.OnRequestHeadersAsync(
                new ScriptPhaseContext
                {
                    Phase = ScriptPhases.RequestHeaders,
                    Method = "GET",
                    Path = "/",
                    Host = "h",
                    Headers = [new KeyValuePair<string, string>("A", "1")],
                    Chunk = ReadOnlyMemory<byte>.Empty,
                    Items = new Dictionary<string, object?>(),
                },
                default));

        var remover = new RecordingHost(
            ScriptPhases.RequestHeaders,
            _ => new ScriptPhaseResult { RemoveHeaders = ["A"] });
        var chain = new ScriptPhaseHostChain([remover]);
        var removed = await chain.OnRequestHeadersAsync(
            new ScriptPhaseContext
            {
                Phase = ScriptPhases.RequestHeaders,
                Method = "GET",
                Path = "/",
                Host = "h",
                Headers = [new KeyValuePair<string, string>("A", "1"), new KeyValuePair<string, string>("B", "2")],
                Chunk = ReadOnlyMemory<byte>.Empty,
                Items = new Dictionary<string, object?>(),
            },
            default);
        Assert.IsNotNull(removed.RemoveHeaders);
        CollectionAssert.Contains(removed.RemoveHeaders!.ToList(), "A");
        Assert.IsTrue(removed.SetHeaders is null || removed.SetHeaders.Count == 0);

        var first = new RecordingHost(
            ScriptPhases.RequestHeaders,
            _ => ScriptPhaseResult.Deny(403, "nope"));
        var second = new RecordingHost(ScriptPhases.RequestHeaders);
        var denyChain = new ScriptPhaseHostChain([first, second]);
        var denied = await denyChain.OnRequestHeadersAsync(
            new ScriptPhaseContext
            {
                Phase = ScriptPhases.RequestHeaders,
                Method = "GET",
                Path = "/",
                Host = "h",
                Headers = [],
                Chunk = ReadOnlyMemory<byte>.Empty,
                Items = new Dictionary<string, object?>(),
            },
            default);
        Assert.AreEqual(ScriptAction.Deny, denied.Action);
        Assert.AreEqual(1, first.Calls);
        Assert.AreEqual(0, second.Calls, "immediate deny must stop the chain");
    }
}
