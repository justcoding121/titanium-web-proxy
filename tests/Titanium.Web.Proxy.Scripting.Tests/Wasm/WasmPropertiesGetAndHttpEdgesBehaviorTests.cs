using System.Buffers.Binary;
using System.Net;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Abstractions.Plugins;
using Titanium.Web.Proxy.Abstractions.Scripting;
using Titanium.Web.Proxy.Scripting.Wasm;

namespace Titanium.Web.Proxy.Scripting.Tests.Wasm;

/// <summary>
///     Remaining WasmProperties / WasmLimits edges: empty catalog, content-length size, no-phase
///     fallbacks, and upper-bound limit rejects. No native runtime required.
/// </summary>
[TestClass]
public class WasmPropertiesGetAndHttpEdgesBehaviorTests
{
    [TestMethod]
    public void Get_ReturnsEmptyCatalog_AndStoredPropertyWins()
    {
        var context = new WasmHttpContext(null!, 1, 1, null!)
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

        CollectionAssert.AreEqual(Array.Empty<byte>(), WasmProperties.Get(context, "metadata"));
        CollectionAssert.AreEqual(Array.Empty<byte>(), WasmProperties.Get(context, "filter_state"));
        CollectionAssert.AreEqual(Array.Empty<byte>(), WasmProperties.Get(context, "node"));
        CollectionAssert.AreEqual(Array.Empty<byte>(), WasmProperties.Get(context, "request.total_size"));
        CollectionAssert.AreEqual(Array.Empty<byte>(), WasmProperties.Get(context, "response.total_size"));
        CollectionAssert.AreEqual(Array.Empty<byte>(), WasmProperties.Get(context, "connection.termination_details"));
        Assert.IsNull(WasmProperties.Get(context, "not.a.real.attr"));

        var stored = "override"u8.ToArray();
        context.Properties["metadata"] = stored;
        Assert.AreSame(stored, WasmProperties.Get(context, "metadata"));
    }

    [TestMethod]
    public void ForHttp_UsesContentLength_AndHttpsSourceRouteEdges()
    {
        var client = new IPEndPoint(IPAddress.Parse("203.0.113.9"), 4444);
        var phase = new ScriptPhaseContext
        {
            Phase = ScriptPhases.RequestHeaders,
            Method = "POST",
            Path = "/upload",
            Host = "api.example",
            IsHttps = true,
            ClientEndPoint = client,
            Headers = [],
            Chunk = ReadOnlyMemory<byte>.Empty,
            Items = new Dictionary<string, object?>(),
            RouteId = "edge-route",
            ClusterId = "edge-cluster",
            Elapsed = TimeSpan.FromMilliseconds(12),
        };
        var context = new WasmHttpContext(null!, 3, 1, null!) { Last = phase };
        context.BeginHeaders(ScriptPhases.RequestHeaders, new WasmHeaderMap(
        [
            new(":method", "POST"),
            new(":path", "/upload"),
            new(":authority", "api.example"),
            new(":scheme", "https"),
            new("content-length", "42"),
            new("user-agent", "cov"),
            new("referer", "https://ref"),
            new("x-request-id", "rid-9"),
        ]));

        Assert.AreEqual("TLS", Utf8(WasmProperties.ForHttp(context, "connection.tls_version")));
        Assert.AreEqual(42L, I64(WasmProperties.ForHttp(context, "request.size")));
        Assert.AreEqual(client.ToString(), Utf8(WasmProperties.ForHttp(context, "source.address")));
        Assert.AreEqual(4444L, I64(WasmProperties.ForHttp(context, "source.port")));
        Assert.AreEqual("edge-cluster", Utf8(WasmProperties.ForHttp(context, "cluster_name")));
        Assert.AreEqual("edge-cluster", Utf8(WasmProperties.ForHttp(context, "xds.cluster_name")));
        Assert.AreEqual("edge-route", Utf8(WasmProperties.ForHttp(context, "route_name")));
        Assert.AreEqual("edge-route", Utf8(WasmProperties.ForHttp(context, "xds.route_name")));
        Assert.AreEqual("cov", Utf8(WasmProperties.ForHttp(context, "request.useragent")));
        Assert.AreEqual("https://ref", Utf8(WasmProperties.ForHttp(context, "request.referer")));
        Assert.AreEqual("rid-9", Utf8(WasmProperties.ForHttp(context, "request.id")));
        Assert.IsTrue(I64(WasmProperties.ForHttp(context, "request.duration")) > 0);
        Assert.IsNull(WasmProperties.ForHttp(context, "response.code"));
    }

    [TestMethod]
    public void ForHttp_WithoutPhase_FallsBackSafely()
    {
        var context = new WasmHttpContext(null!, 1, 1, null!);
        Assert.IsNull(WasmProperties.ForHttp(context, "source.address"));
        Assert.IsNull(WasmProperties.ForHttp(context, "source.port"));
        Assert.AreEqual(string.Empty, Utf8(WasmProperties.ForHttp(context, "cluster_name")));
        Assert.AreEqual(string.Empty, Utf8(WasmProperties.ForHttp(context, "route_name")));
        Assert.AreEqual(string.Empty, Utf8(WasmProperties.ForHttp(context, "connection.tls_version")));
        Assert.AreEqual(0L, I64(WasmProperties.ForHttp(context, "request.size")));
        Assert.AreEqual(0L, I64(WasmProperties.ForHttp(context, "response.size")));
        Assert.AreEqual(0L, I64(WasmProperties.ForHttp(context, "response.flags")));
        Assert.IsNull(WasmProperties.ForHttp(context, "request.headers"));
        Assert.IsNull(WasmProperties.ForHttp(context, "response.headers"));
        Assert.IsNull(WasmProperties.ForHttp(context, "response.trailers"));
    }

    [TestMethod]
    public void WasmLimits_RejectsMemoryPoolAndCalloutUpperBounds()
    {
        Assert.IsFalse(WasmLimits.TryParse(new PluginConfig
        {
            Limits = new PluginLimitsConfig { MemoryMb = WasmLimits.MaxMemoryMb + 1 },
        }, out _, out var memError));
        StringAssert.Contains(memError!, "memoryMb");

        Assert.IsFalse(WasmLimits.TryParse(new PluginConfig
        {
            Limits = new PluginLimitsConfig { PoolSize = 1025 },
        }, out _, out var poolError));
        StringAssert.Contains(poolError!, "poolSize");

        Assert.IsFalse(WasmLimits.TryParse(new PluginConfig
        {
            Limits = new PluginLimitsConfig { CalloutConcurrency = 10_001 },
        }, out _, out var calloutError));
        StringAssert.Contains(calloutError!, "calloutConcurrency");

        Assert.IsFalse(WasmLimits.TryParse(new PluginConfig
        {
            Limits = new PluginLimitsConfig { EpochDeadlineMs = Titanium.Web.Proxy.Abstractions.Plugins.ProxyPluginContract.MaxTimeoutMilliseconds + 1 },
        }, out _, out var epochError));
        StringAssert.Contains(epochError!, "epochDeadlineMs");
    }

    [TestMethod]
    public void HeaderMap_AddGetCount_AndDiffWithoutChanges()
    {
        var map = new WasmHeaderMap([]);
        Assert.AreEqual(0, map.Count);
        map.Add("x-a", "1");
        map.Add("x-a", "2");
        Assert.AreEqual(2, map.Count);
        Assert.AreEqual("1", map.Get("x-a"));
        Assert.IsNull(map.Get("missing"));

        var untouched = new WasmHeaderMap([new("host", "a")]);
        var (set, remove) = untouched.Diff();
        Assert.IsTrue(set is null || set.Count == 0);
        Assert.IsTrue(remove is null || remove.Count == 0);
    }

    private static string Utf8(byte[]? bytes) => Encoding.UTF8.GetString(bytes ?? []);

    private static long I64(byte[]? bytes) => BinaryPrimitives.ReadInt64LittleEndian(bytes!);
}
