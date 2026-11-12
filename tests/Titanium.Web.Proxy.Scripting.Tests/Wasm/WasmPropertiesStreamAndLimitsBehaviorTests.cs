using System.Buffers.Binary;
using System.Net;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Abstractions.Plugins;
using Titanium.Web.Proxy.Abstractions.Scripting;
using Titanium.Web.Proxy.Scripting.Lua;
using Titanium.Web.Proxy.Scripting.Wasm;

namespace Titanium.Web.Proxy.Scripting.Tests.Wasm;

/// <summary>
///     Fat leftovers: WasmProperties.ForStream catalog and LuaOptions sharedDicts / remaining limit ranges.
/// </summary>
[TestClass]
public class WasmPropertiesStreamAndLimitsBehaviorTests
{
    [TestMethod]
    public void ForStream_ReturnsConnectionAddressesSniClusterAndCounters()
    {
        var connection = new StreamConnectionContext
        {
            ClientEndPoint = new IPEndPoint(IPAddress.Loopback, 5555),
            LocalEndPoint = new IPEndPoint(IPAddress.Parse("10.0.0.1"), 443),
            ServerName = "sni.example",
            ClusterId = "fallback",
            Items = new Dictionary<string, object?>(),
        };
        var context = new WasmStreamContext(null!, 9, 9, null!, connection);

        Assert.AreEqual(connection.ClientEndPoint.ToString(), Utf8(WasmProperties.ForStream(context, "source.address")));
        Assert.AreEqual(5555L, I64(WasmProperties.ForStream(context, "source.port")));
        Assert.AreEqual(connection.LocalEndPoint.ToString(), Utf8(WasmProperties.ForStream(context, "destination.address")));
        Assert.AreEqual(443L, I64(WasmProperties.ForStream(context, "destination.port")));
        Assert.AreEqual("sni.example", Utf8(WasmProperties.ForStream(context, "connection.requested_server_name")));
        Assert.AreEqual(9L, I64(WasmProperties.ForStream(context, "connection.id")));
        CollectionAssert.AreEqual(new byte[] { 0 }, WasmProperties.ForStream(context, "connection.mtls"));
        Assert.AreEqual(string.Empty, Utf8(WasmProperties.ForStream(context, "connection.tls_version")));
        Assert.IsNotNull(WasmProperties.ForStream(context, "connection.time"));
        Assert.IsNotNull(WasmProperties.ForStream(context, "connection.duration"));
        Assert.AreEqual(0L, I64(WasmProperties.ForStream(context, "connection.received_bytes")));
        Assert.AreEqual(0L, I64(WasmProperties.ForStream(context, "connection.sent_bytes")));
        Assert.AreEqual("fallback", Utf8(WasmProperties.ForStream(context, "cluster_name")));
        Assert.AreEqual("fallback", Utf8(WasmProperties.ForStream(context, "xds.cluster_name")));
        Assert.IsNull(WasmProperties.ForStream(context, "unknown.stream.attr"));
    }

    [TestMethod]
    public void ForHttp_UsesPhaseFallbacks_WhenPseudoHeadersMissing()
    {
        var phase = new ScriptPhaseContext
        {
            Phase = ScriptPhases.RequestHeaders,
            Method = "PUT",
            Path = "/plain?x=1",
            Host = "phase.host",
            IsHttps = true,
            ClientEndPoint = new IPEndPoint(IPAddress.Loopback, 9),
            Headers = [],
            Chunk = ReadOnlyMemory<byte>.Empty,
            Items = new Dictionary<string, object?>(),
            RouteId = "r1",
            ClusterId = "c1",
        };
        var context = new WasmHttpContext(null!, 1, 1, null!) { Last = phase };
        context.BeginHeaders(ScriptPhases.RequestHeaders, new WasmHeaderMap([]));

        Assert.AreEqual("/plain?x=1", Utf8(WasmProperties.ForHttp(context, "request.path")));
        Assert.AreEqual("/plain", Utf8(WasmProperties.ForHttp(context, "request.url_path")));
        Assert.AreEqual("x=1", Utf8(WasmProperties.ForHttp(context, "request.query")));
        Assert.AreEqual("phase.host", Utf8(WasmProperties.ForHttp(context, "request.host")));
        Assert.AreEqual("https", Utf8(WasmProperties.ForHttp(context, "request.scheme")));
        Assert.AreEqual("PUT", Utf8(WasmProperties.ForHttp(context, "request.method")));
        Assert.AreEqual("TLS", Utf8(WasmProperties.ForHttp(context, "connection.tls_version")));
        Assert.AreEqual(9L, I64(WasmProperties.ForHttp(context, "source.port")));
        Assert.IsNotNull(WasmProperties.Get(context, "metadata"));
        Assert.IsNotNull(WasmProperties.Get(context, "request.total_size"));
    }

    [TestMethod]
    public void LuaOptions_SharedDictsAndRemainingLimitRanges()
    {
        var path = Path.Combine(Path.GetTempPath(), "twp-lua-limits-" + Guid.NewGuid().ToString("N") + ".lua");
        File.WriteAllText(path, "-- empty");
        try
        {
            Assert.IsTrue(LuaOptions.TryParse(new PluginConfig
            {
                Name = "ok",
                File = path,
                Settings = new Dictionary<string, string> { [LuaOptions.SharedDictsSetting] = "cache:100,metrics" },
            }, out var options, out var error));
            Assert.IsNull(error);
            Assert.AreEqual(2, options!.SharedDicts.Count);
            Assert.AreEqual("cache", options.SharedDicts[0].Name);
            Assert.AreEqual(100L, options.SharedDicts[0].MaxItems);
            Assert.AreEqual("metrics", options.SharedDicts[1].Name);
            Assert.AreEqual(10_000L, options.SharedDicts[1].MaxItems);

            Assert.IsFalse(LuaOptions.TryParse(new PluginConfig
            {
                Name = "x",
                File = path,
                Settings = new Dictionary<string, string> { [LuaOptions.SharedDictsSetting] = "cache:0" },
            }, out _, out var badCount));
            StringAssert.Contains(badCount!, "max item count");

            Assert.IsFalse(LuaOptions.TryParse(new PluginConfig
            {
                Name = "x",
                File = path,
                Limits = new PluginLimitsConfig { MaxInstructions = 10 },
            }, out _, out var fuelLike));
            StringAssert.Contains(fuelLike!, "limits.maxInstructions");

            Assert.IsFalse(LuaOptions.TryParse(new PluginConfig
            {
                Name = "x",
                File = path,
                Limits = new PluginLimitsConfig { CalloutConcurrency = 0 },
            }, out _, out var callouts));
            StringAssert.Contains(callouts!, "limits.calloutConcurrency");

            Assert.IsFalse(LuaOptions.TryParse(new PluginConfig
            {
                Name = "x",
                File = path,
                Limits = new PluginLimitsConfig { BufferedBodyBytes = 10 },
            }, out _, out var body));
            StringAssert.Contains(body!, "limits.bufferedBodyBytes");

            Assert.IsFalse(LuaOptions.TryParse(new PluginConfig
            {
                Name = "x",
                File = path,
                Limits = new PluginLimitsConfig { QueueBytes = 10 },
            }, out _, out var queue));
            StringAssert.Contains(queue!, "limits.queueBytes");

            Assert.IsFalse(LuaOptions.TryParse(new PluginConfig
            {
                Name = "x",
                File = path,
                Limits = new PluginLimitsConfig { SharedDataBytes = 10 },
            }, out _, out var shared));
            StringAssert.Contains(shared!, "limits.sharedDataBytes");
        }
        finally
        {
            try { File.Delete(path); } catch { /* ignore */ }
        }
    }

    private static string Utf8(byte[]? bytes) => Encoding.UTF8.GetString(bytes ?? []);

    private static long I64(byte[]? bytes) => BinaryPrimitives.ReadInt64LittleEndian(bytes!);
}
