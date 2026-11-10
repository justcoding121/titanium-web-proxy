using System.Net;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Abstractions.Scripting;
using Titanium.Web.Proxy.Scripting.Wasm;

namespace Titanium.Web.Proxy.Scripting.Tests.Wasm;

/// <summary>
///     Fat WasmHttpContext leftovers: Finish pause/local, SendLocalResponse, Continue/CloseStream, Resolve.
/// </summary>
[TestClass]
public class WasmHttpContextFinishBehaviorTests
{
    [TestMethod]
    public void Finish_Continue_EmitsHeaderDiffAndClusterOverride()
    {
        var context = HeadersContext();
        context.RequestHeaders!.Replace("x-a", "1");
        context.RequestHeaders.Remove("user-agent");
        context.Properties[WasmHttpContext.UpstreamClusterProperty] = Encoding.UTF8.GetBytes("chosen");

        var result = context.Finish(ProxyAction.Continue);
        Assert.AreEqual(ScriptAction.Continue, result.Action);
        Assert.AreEqual("chosen", result.UpstreamClusterId);
        Assert.IsTrue(result.SetHeaders!.ContainsKey("x-a"));
        Assert.IsTrue(result.RemoveHeaders!.Contains("user-agent"));
    }

    [TestMethod]
    public void Finish_PauseOnBody_HoldsChunkWithoutForwarding()
    {
        var context = new WasmHttpContext(null!, 1, 1, null!);
        Assert.IsTrue(context.BeginBody(ScriptPhases.RequestBody, "chunk"u8.ToArray(), end: false));
        var held = context.Finish(ProxyAction.Pause);
        Assert.AreEqual(ScriptAction.Continue, held.Action);
        CollectionAssert.AreEqual(Array.Empty<byte>(), held.ReplaceBody);
        Assert.IsFalse(context.Paused);
    }

    [TestMethod]
    public void SendLocalResponse_WinsOverContinue_AndGrpcStatusHeader()
    {
        var context = HeadersContext();
        var headers = new List<KeyValuePair<string, string>> { new("x-local", "1") };
        Assert.AreEqual(WasmResult.Ok, context.SendLocalResponse(418, "nope", "body"u8.ToArray(), headers, grpcStatus: 14));
        Assert.IsTrue(headers.Any(h => h.Key == "grpc-status" && h.Value == "14"));

        var result = context.Finish(ProxyAction.Continue);
        Assert.AreEqual(ScriptAction.Deny, result.Action);
        Assert.AreEqual(418, result.LocalResponse!.Status);
        CollectionAssert.AreEqual("body"u8.ToArray(), result.LocalResponse.Body);
    }

    [TestMethod]
    public void SendLocalResponse_ClampsBadStatus_AndCloseStreamDefaults503()
    {
        var context = HeadersContext();
        Assert.AreEqual(WasmResult.Ok, context.SendLocalResponse(12, "", [], [], grpcStatus: -1));
        Assert.AreEqual(500, context.LocalResponse!.Status);

        var other = HeadersContext();
        Assert.AreEqual(WasmResult.BadArgument, other.ContinueStream(ProxyStreamType.Downstream));
        Assert.AreEqual(WasmResult.BadArgument, other.CloseStream(ProxyStreamType.Upstream));
        Assert.AreEqual(WasmResult.Ok, other.CloseStream(ProxyStreamType.HttpRequest));
        Assert.AreEqual(503, other.LocalResponse!.Status);
        var denied = other.Finish(ProxyAction.Continue);
        Assert.AreEqual(ScriptAction.Deny, denied.Action);
        Assert.AreEqual(503, denied.LocalResponse!.Status);
    }

    [TestMethod]
    public async Task Pause_ContinueStream_Resumes_AndResolveTimeoutDenies504()
    {
        var context = HeadersContext();
        Assert.AreEqual(ScriptAction.Pause, context.Finish(ProxyAction.Pause).Action);
        Assert.IsTrue(context.Paused);

        var wait = context.WaitForResumeAsync(TimeSpan.FromSeconds(2), default);
        Assert.AreEqual(WasmResult.Ok, context.ContinueStream(ProxyStreamType.HttpResponse));
        Assert.IsTrue(await wait);
        Assert.IsFalse(context.Paused);

        var timedOut = HeadersContext();
        Assert.AreEqual(ScriptAction.Pause, timedOut.Finish(ProxyAction.Pause).Action);
        Assert.IsFalse(await timedOut.WaitForResumeAsync(TimeSpan.FromMilliseconds(20), default));
        var deny = timedOut.Resolve(resumed: false);
        Assert.AreEqual(ScriptAction.Deny, deny.Action);
        Assert.AreEqual(504, deny.LocalResponse!.Status);
    }

    [TestMethod]
    public void SetBuffer_OnlyInBodyPhase_AndGetBufferMatches()
    {
        var context = HeadersContext();
        Assert.AreEqual(WasmResult.NotFound, context.SetBuffer(ProxyBufferType.HttpRequestBody, 0, 0, "x"u8.ToArray()));

        Assert.IsTrue(context.BeginBody(ScriptPhases.RequestBody, "ab"u8.ToArray(), end: true));
        Assert.AreEqual(WasmResult.Ok, context.SetBuffer(ProxyBufferType.HttpRequestBody, 0, 2, "XY"u8.ToArray()));
        CollectionAssert.AreEqual("XY"u8.ToArray(), context.GetBuffer(ProxyBufferType.HttpRequestBody));
        var result = context.Finish(ProxyAction.Continue);
        CollectionAssert.AreEqual("XY"u8.ToArray(), result.ReplaceBody);
    }

    private static WasmHttpContext HeadersContext()
    {
        var context = new WasmHttpContext(null!, 1, 1, null!)
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
            new("user-agent", "t"),
        ]));
        return context;
    }
}
