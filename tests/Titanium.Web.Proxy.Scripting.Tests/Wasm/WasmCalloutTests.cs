using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Abstractions.Plugins;
using Titanium.Web.Proxy.Abstractions.Scripting;
using Titanium.Web.Proxy.Scripting.Wasm;
using static Titanium.Web.Proxy.Scripting.Tests.Wasm.WasmTestSupport;

namespace Titanium.Web.Proxy.Scripting.Tests.Wasm;

[TestClass]
[DoNotParallelize]
public class WasmCalloutTests
{
    private static CalloutOrigin _origin = null!;

    [ClassInitialize]
    public static async Task StartOrigin(TestContext _) => _origin = await CalloutOrigin.StartAsync();

    [ClassCleanup]
    public static async Task StopOrigin() => await _origin.DisposeAsync();

    [TestMethod]
    public async Task HttpCall_PausesTheRequest_AndTheResponseCallbackResumesItWithEdits()
    {
        using var plugin = Create(Config("callouts.wat", calloutHosts: [_origin.HttpTarget]));

        var result = await RunAsync(plugin, H("x-http-call", _origin.HttpTarget));

        Assert.AreEqual(ScriptAction.Continue, result.Action);
        Assert.AreEqual("200", result.SetHeaders!["x-call-status"]);
        Assert.AreEqual("world", result.SetHeaders["x-call-body"]);
        Assert.AreEqual(0, plugin.Pool.Callouts.InFlight);
    }

    [TestMethod]
    public async Task HttpCall_ToAHostOutsideCalloutHosts_IsRefusedWithoutPausing()
    {
        using var plugin = Create(Config("callouts.wat", calloutHosts: ["other.test"]));
        var items = new Dictionary<string, object?>();

        var result = await plugin.OnRequestHeadersAsync(Phase(ScriptPhases.RequestHeaders, items, [H("x-http-call", _origin.HttpTarget)]), default);

        Assert.AreEqual(ScriptAction.Continue, result.Action);
        Assert.AreEqual("1", result.SetHeaders!["x-call-error"]);
    }

    [TestMethod]
    public async Task HttpCall_PastCalloutConcurrency_IsRefused()
    {
        using var plugin = Create(Config("callouts.wat", calloutHosts: [_origin.HttpTarget],
            limits: new PluginLimitsConfig { PoolSize = 1, CalloutConcurrency = 0 }));

        var result = await plugin.OnRequestHeadersAsync(
            Phase(ScriptPhases.RequestHeaders, new Dictionary<string, object?>(), [H("x-http-call", _origin.HttpTarget)]), default);

        Assert.AreEqual("1", result.SetHeaders!["x-call-error"]);
    }

    [TestMethod]
    public async Task HttpCall_ThatCannotConnect_IsAnsweredWithNoHeaders()
    {
        var closed = "127.0.0.1:" + CalloutOrigin.FreePort();
        using var plugin = Create(Config("callouts.wat", calloutHosts: [closed]));

        var result = await RunAsync(plugin, H("x-http-call", closed));

        Assert.AreEqual(ScriptAction.Continue, result.Action);
        Assert.IsFalse(result.SetHeaders?.ContainsKey("x-call-status") ?? false, "a failed callout has no :status");
    }

    [TestMethod]
    public async Task GrpcCall_DeliversTheReply()
    {
        using var plugin = Create(Config("callouts.wat", calloutHosts: [_origin.GrpcTarget]));

        var result = await RunAsync(plugin, H("x-grpc-call", _origin.GrpcTarget));

        Assert.AreEqual("pong:ping", result.SetHeaders!["x-grpc-msg"]);
    }

    [TestMethod]
    public async Task GrpcCall_ThatFails_ClosesWithTheServersStatus()
    {
        using var plugin = Create(Config("callouts.wat", calloutHosts: [_origin.GrpcTarget]));

        var result = await RunAsync(plugin, H("x-grpc-bad", _origin.GrpcTarget));

        Assert.AreEqual("12", result.SetHeaders!["x-grpc-status"]);
    }

    [TestMethod]
    public async Task GrpcStream_DeliversMetadata_EachMessage_TrailersAndClose()
    {
        using var plugin = Create(Config("callouts.wat", calloutHosts: [_origin.GrpcTarget]));

        var result = await RunAsync(plugin, H("x-grpc-stream", _origin.GrpcTarget));

        var set = result.SetHeaders!;
        Assert.AreEqual("1", set["x-grpc-initial"]);
        Assert.AreEqual("echo:a, echo:b", set["x-grpc-msg"]);
        Assert.AreEqual("1", set["x-grpc-trailers"]);
        Assert.AreEqual("0", set["x-grpc-status"]);
    }

    /// <summary>Runs request headers, then resumes as Core would until the guest answers.</summary>
    private static async Task<ScriptPhaseResult> RunAsync(WasmPlugin plugin, KeyValuePair<string, string> header)
    {
        var items = new Dictionary<string, object?>();
        var phase = Phase(ScriptPhases.RequestHeaders, items, [header]);
        var result = await plugin.OnRequestHeadersAsync(phase, default);
        Assert.AreEqual(ScriptAction.Pause, result.Action, "the guest pauses on its callout");
        for (var resumes = 1; result.Action == ScriptAction.Pause && resumes <= 64; resumes++)
        {
            phase.ResumeCount = resumes;
            result = await plugin.OnResumeAsync(phase, default);
        }

        await plugin.OnLogAsync(Phase(ScriptPhases.Log, items), default);
        return result;
    }
}
