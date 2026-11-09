using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Abstractions.Scripting;
using Titanium.Web.Proxy.Scripting.Wasm;

namespace Titanium.Web.Proxy.Scripting.Tests.Wasm;

/// <summary>
///     Catalog coverage for <see cref="WasmProperties"/> HTTP attributes.
///     Bug class: wrong path/method/host values or NotFound for documented attributes.
/// </summary>
[TestClass]
public class WasmPropertiesCatalogTests
{
    [TestMethod]
    public void NormalizePath_ConvertsNulSeparatedAndTrimsTrailingDot()
    {
        Assert.AreEqual("request.path", WasmProperties.NormalizePath("request\0path"));
        Assert.AreEqual("a.b", WasmProperties.NormalizePath("a.b."));
        Assert.AreEqual("x", WasmProperties.NormalizePath("x"));
    }

    [TestMethod]
    public void ForHttp_ReturnsDocumentedRequestAttributes()
    {
        var context = new WasmHttpContext(null!, 1, 1, null!)
        {
            Last = WasmTestSupport.Phase(ScriptPhases.RequestHeaders, new Dictionary<string, object?>(),
                method: "POST", path: "/v1/echo?q=1", status: 201),
        };
        context.BeginHeaders(ScriptPhases.RequestHeaders, new WasmHeaderMap(
        [
            new KeyValuePair<string, string>(":path", "/v1/echo?q=1"),
            new KeyValuePair<string, string>(":method", "POST"),
            new KeyValuePair<string, string>(":scheme", "https"),
            new KeyValuePair<string, string>(":authority", "app.example"),
            new KeyValuePair<string, string>("user-agent", "ua"),
            new KeyValuePair<string, string>("referer", "https://ref"),
            new KeyValuePair<string, string>("x-request-id", "rid"),
            new KeyValuePair<string, string>("content-length", "3"),
        ]));

        Assert.AreEqual("/v1/echo?q=1", Utf8(WasmProperties.ForHttp(context, "request.path")));
        Assert.AreEqual("/v1/echo", Utf8(WasmProperties.ForHttp(context, "request.url_path")));
        Assert.AreEqual("q=1", Utf8(WasmProperties.ForHttp(context, "request.query")));
        Assert.AreEqual("app.example", Utf8(WasmProperties.ForHttp(context, "request.host")));
        Assert.AreEqual("https", Utf8(WasmProperties.ForHttp(context, "request.scheme")));
        Assert.AreEqual("POST", Utf8(WasmProperties.ForHttp(context, "request.method")));
        Assert.AreEqual("ua", Utf8(WasmProperties.ForHttp(context, "request.useragent")));
        Assert.AreEqual("https://ref", Utf8(WasmProperties.ForHttp(context, "request.referer")));
        Assert.AreEqual("rid", Utf8(WasmProperties.ForHttp(context, "request.id")));
        Assert.AreEqual("HTTP/1.1", Utf8(WasmProperties.ForHttp(context, "request.protocol")));
        Assert.IsNotNull(WasmProperties.ForHttp(context, "request.headers"));
        Assert.IsNotNull(WasmProperties.ForHttp(context, "request.time"));
        Assert.IsNotNull(WasmProperties.ForHttp(context, "request.size"));
        Assert.IsNull(WasmProperties.ForHttp(context, "totally.unknown.attribute"));
        Assert.IsNotNull(WasmProperties.Get(context, "node")); // empty attribute set
        Assert.IsNull(WasmProperties.Get(context, "unknown.plugin.attr"));
    }

    [TestMethod]
    public void ForHttp_ReturnsDocumentedResponseAttributes()
    {
        var context = new WasmHttpContext(null!, 1, 1, null!)
        {
            Last = WasmTestSupport.Phase(ScriptPhases.ResponseHeaders, new Dictionary<string, object?>(), status: 201),
        };
        context.BeginHeaders(ScriptPhases.ResponseHeaders, new WasmHeaderMap(
        [
            new KeyValuePair<string, string>("content-type", "application/json"),
            new KeyValuePair<string, string>("x-custom", "val"),
        ]));

        Assert.AreEqual(201L, System.Buffers.Binary.BinaryPrimitives.ReadInt64LittleEndian(WasmProperties.ForHttp(context, "response.code")!));
        Assert.AreEqual(0L, System.Buffers.Binary.BinaryPrimitives.ReadInt64LittleEndian(WasmProperties.ForHttp(context, "response.flags")!));
        Assert.IsNotNull(WasmProperties.ForHttp(context, "response.headers"), "serialized header map must be non-null");
        Assert.AreEqual(0L, System.Buffers.Binary.BinaryPrimitives.ReadInt64LittleEndian(WasmProperties.ForHttp(context, "response.size")!), "no body streamed yet");
        Assert.IsNull(WasmProperties.ForHttp(context, "response.grpc_status"), "no trailers set yet");
        Assert.IsNull(WasmProperties.ForHttp(context, "response.trailers"), "no trailers map set yet");
    }

    [TestMethod]
    public void ForHttp_ReturnsNullForUnknownPaths()
    {
        var context = new WasmHttpContext(null!, 1, 1, null!)
        {
            Last = WasmTestSupport.Phase(ScriptPhases.RequestHeaders, new Dictionary<string, object?>()),
        };
        context.BeginHeaders(ScriptPhases.RequestHeaders, new WasmHeaderMap([]));

        Assert.IsNull(WasmProperties.ForHttp(context, "unknown.vendor.ext"), "unregistered path must be null");
        Assert.IsNull(WasmProperties.ForHttp(context, "response.code"), "status not yet received");
    }

    [TestMethod]
    public void Properties_StoredBySetProperty_AreReturnedByGet()
    {
        var context = new WasmHttpContext(null!, 1, 1, null!)
        {
            Last = WasmTestSupport.Phase(ScriptPhases.RequestHeaders, new Dictionary<string, object?>()),
        };
        var value = "overridden"u8.ToArray();
        context.Properties["my.ext.key"] = value;

        Assert.AreSame(value, WasmProperties.Get(context, "my.ext.key"), "stored value must shadow everything else");
        Assert.IsNull(WasmProperties.Get(context, "my.ext.other"), "an unstored sibling path is not found");
    }

    private static string Utf8(byte[]? bytes) => Encoding.UTF8.GetString(bytes ?? []);
}
