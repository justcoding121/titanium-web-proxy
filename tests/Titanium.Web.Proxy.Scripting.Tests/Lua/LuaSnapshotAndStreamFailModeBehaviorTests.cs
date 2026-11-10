using System.Net;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Abstractions.Plugins;
using Titanium.Web.Proxy.Abstractions.Scripting;
using Titanium.Web.Proxy.Scripting.Lua;
using Titanium.Web.Proxy.Scripting.Wasm;
using static Titanium.Web.Proxy.Scripting.Tests.Lua.LuaTestSupport;

namespace Titanium.Web.Proxy.Scripting.Tests.Lua;

/// <summary>
///     Fat leftovers: LuaSnapshot HTTP encoding and L4 fail-open / fail-closed / missing-hook behavior.
/// </summary>
[TestClass]
public class LuaSnapshotAndStreamFailModeBehaviorTests
{
    [TestMethod]
    public void Snapshot_Http_EncodesPhaseFieldsHeadersBodyAndEndPoints()
    {
        var context = new ScriptPhaseContext
        {
            Phase = ScriptPhases.RequestBody,
            Method = "POST",
            Path = "/v1",
            Host = "app.test",
            IsHttps = true,
            StatusCode = 0,
            ClientEndPoint = new IPEndPoint(IPAddress.Loopback, 4444),
            Headers = [new KeyValuePair<string, string>("x-a", "1")],
            Chunk = "hi"u8.ToArray(),
            IsLastChunk = true,
            Items = new Dictionary<string, object?>(),
            RouteId = "r1",
            ClusterId = "c1",
        };

        var encoded = LuaSnapshot.Http("plug", context, vars: null);
        var table = (LuaTable)new LuaReader(encoded).Read()!;
        Assert.AreEqual("request_body", table.Str("phase"));
        Assert.AreEqual("plug", table.Str("plugin"));
        Assert.AreEqual("POST", table.Str("method"));
        Assert.AreEqual("https", table.Str("scheme"));
        Assert.AreEqual("r1", table.Str("route_id"));
        Assert.AreEqual("c1", table.Str("cluster_id"));
        Assert.AreEqual("127.0.0.1", table.Str("client_ip"));
        Assert.AreEqual(4444L, table.Int("client_port"));
        Assert.AreEqual(true, table["is_https"]);
        Assert.AreEqual("hi", Encoding.UTF8.GetString(table.Bytes("body")!));
        Assert.AreEqual(true, table["last"]);
        Assert.IsNotNull(table.Table("headers"));

        var vars = new LuaWriter();
        vars.Nil();
        var log = LuaSnapshot.Http("plug", new ScriptPhaseContext
        {
            Phase = ScriptPhases.Log,
            Method = "GET",
            Path = "/",
            Host = "h",
            Headers = [],
            Chunk = ReadOnlyMemory<byte>.Empty,
            Items = new Dictionary<string, object?>(),
            Elapsed = TimeSpan.FromMilliseconds(12),
            Error = "boom",
        }, vars.ToArray());
        var logTable = (LuaTable)new LuaReader(log).Read()!;
        Assert.AreEqual(12L, logTable.Int("elapsed_ms"));
        Assert.AreEqual("boom", logTable.Str("error"));
        Assert.IsTrue(logTable.Fields.ContainsKey("vars"));
    }

    [TestMethod]
    public void EndPoint_SkipsNonIp_AndMapsIpv4Mapped()
    {
        var w = new LuaWriter();
        Assert.AreEqual(0, LuaSnapshot.EndPoint(w, "x", new DnsEndPoint("example.test", 80)));
        Assert.AreEqual(0, LuaSnapshot.EndPoint(w, "x", null));

        var mapped = new IPEndPoint(IPAddress.Parse("::ffff:10.0.0.7"), 9);
        Assert.AreEqual(2, LuaSnapshot.EndPoint(w, "peer", mapped));
        var encoded = w.ToArray();
        // Field encoding is opaque; ensure the IPv4 form is present rather than the mapped text.
        StringAssert.Contains(Encoding.UTF8.GetString(encoded), "10.0.0.7");
        Assert.IsFalse(Encoding.UTF8.GetString(encoded).Contains("ffff", StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public void ToLogLevel_MapsProxyLevels_AndUnknownPoolKeyIsMissing()
    {
        Assert.AreEqual(Microsoft.Extensions.Logging.LogLevel.Trace, WasmImports.ToLogLevel(ProxyLogLevel.Trace));
        Assert.AreEqual(Microsoft.Extensions.Logging.LogLevel.Debug, WasmImports.ToLogLevel(ProxyLogLevel.Debug));
        Assert.AreEqual(Microsoft.Extensions.Logging.LogLevel.Information, WasmImports.ToLogLevel(ProxyLogLevel.Info));
        Assert.AreEqual(Microsoft.Extensions.Logging.LogLevel.Warning, WasmImports.ToLogLevel(ProxyLogLevel.Warn));
        Assert.AreEqual(Microsoft.Extensions.Logging.LogLevel.Error, WasmImports.ToLogLevel(ProxyLogLevel.Error));
        Assert.AreEqual(Microsoft.Extensions.Logging.LogLevel.Critical, WasmImports.ToLogLevel(ProxyLogLevel.Critical));
        Assert.AreEqual(Microsoft.Extensions.Logging.LogLevel.Critical, WasmImports.ToLogLevel((ProxyLogLevel)99));
        Assert.IsNull(WasmVmPools.Find("missing-" + Guid.NewGuid().ToString("N")));
    }

    [TestMethod]
    public async Task Stream_MissingHookContinues_AndFailModesApplyOnScriptError()
    {
        using var bare = Create(Config("function on_request_headers(ctx) end"));
        var connection = new StreamConnectionContext
        {
            ClientEndPoint = new IPEndPoint(IPAddress.Loopback, 1),
            LocalEndPoint = new IPEndPoint(IPAddress.Loopback, 443),
            Items = new Dictionary<string, object?>(),
        };
        Assert.AreEqual(ScriptAction.Continue, (await bare.OnNewConnectionAsync(connection, default)).Action);
        Assert.AreEqual(ScriptAction.Continue, (await bare.OnDownstreamDataAsync(connection, "x"u8.ToArray(), default)).Action);

        using var closed = Create(Config(
            """
            function on_connect(ctx) error("boom") end
            function on_data(ctx) error("boom") end
            """,
            failMode: ProxyPluginContract.FailClosed));
        Assert.AreEqual(ScriptAction.Deny, (await closed.OnNewConnectionAsync(connection, default)).Action);
        Assert.AreEqual(ScriptAction.Deny, (await closed.OnDownstreamDataAsync(connection, "x"u8.ToArray(), default)).Action);

        using var open = Create(Config(
            """
            function on_connect(ctx) error("boom") end
            function on_data(ctx) error("boom") end
            """,
            failMode: ProxyPluginContract.FailOpen));
        Assert.AreEqual(ScriptAction.Continue, (await open.OnNewConnectionAsync(connection, default)).Action);
        Assert.AreEqual(ScriptAction.Continue, (await open.OnDownstreamDataAsync(connection, "x"u8.ToArray(), default)).Action);
    }
}
