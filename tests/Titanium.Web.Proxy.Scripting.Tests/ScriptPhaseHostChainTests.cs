using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Abstractions.Scripting;
using Titanium.Web.Proxy.Scripting;

namespace Titanium.Web.Proxy.Scripting.Tests;

/// <summary>
/// Unit tests for <see cref="ScriptPhaseHostChain"/> ordering, deny propagation, body-edit forwarding,
/// exception isolation in the log phase, and exception propagation for all other phases.
/// These tests use lightweight in-process <see cref="IScriptPhaseHost"/> stubs and require no native plugin runtime.
/// </summary>
[TestClass]
public class ScriptPhaseHostChainTests
{
    // -------------------------------------------------------------------------------------
    // Stub helpers
    // -------------------------------------------------------------------------------------

    /// <summary>A host that appends its marker to x-order and optionally denies or throws.</summary>
    private sealed class MarkerHost(string mark, bool deny = false, bool throwOnRequest = false, bool throwOnLog = false) : IScriptPhaseHost
    {
        public ScriptPhases Phases => ScriptPhases.RequestHeaders | ScriptPhases.Log;

        public ValueTask<ScriptPhaseResult> OnRequestHeadersAsync(ScriptPhaseContext context, CancellationToken cancellationToken)
        {
            if (throwOnRequest) throw new InvalidOperationException($"host '{mark}' threw");
            if (deny) return new ValueTask<ScriptPhaseResult>(ScriptPhaseResult.Deny(418, mark));
            return new ValueTask<ScriptPhaseResult>(new ScriptPhaseResult
            {
                SetHeaders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["x-order"] = (context.Headers.FirstOrDefault(h => h.Key == "x-order").Value ?? "") + mark,
                },
            });
        }

        public async ValueTask OnLogAsync(ScriptPhaseContext context, CancellationToken cancellationToken)
        {
            if (throwOnLog) throw new InvalidOperationException($"host '{mark}' threw in log");
            await Task.CompletedTask;
        }
    }

    /// <summary>A host that replaces the request body with a fixed payload.</summary>
    private sealed class BodyReplaceHost(byte[] replacement) : IScriptPhaseHost
    {
        public ScriptPhases Phases => ScriptPhases.RequestBody;

        public ValueTask<ScriptPhaseResult> OnRequestBodyAsync(ScriptPhaseContext context, CancellationToken cancellationToken) =>
            new(new ScriptPhaseResult { ReplaceBody = replacement });
    }

    /// <summary>A host that captures whatever body the chain forwarded to it.</summary>
    private sealed class BodyCaptureHost : IScriptPhaseHost
    {
        public byte[]? CapturedBody;

        public ScriptPhases Phases => ScriptPhases.RequestBody;

        public ValueTask<ScriptPhaseResult> OnRequestBodyAsync(ScriptPhaseContext context, CancellationToken cancellationToken)
        {
            CapturedBody = context.Chunk.ToArray();
            return new ValueTask<ScriptPhaseResult>(ScriptPhaseResult.Continue);
        }
    }

    private static ScriptPhaseContext RequestHeadersContext(IEnumerable<KeyValuePair<string, string>>? headers = null) => new()
    {
        Phase = ScriptPhases.RequestHeaders,
        Method = "GET",
        Path = "/",
        Host = "test",
        Headers = (headers ?? []).ToArray(),
        Items = new Dictionary<string, object?>(),
        RouteId = "r1",
        ClusterId = "c1",
    };

    private static ScriptPhaseContext BodyContext(byte[] chunk, bool last = true) => new()
    {
        Phase = ScriptPhases.RequestBody,
        Method = "POST",
        Path = "/",
        Host = "test",
        Headers = [],
        Chunk = chunk,
        IsLastChunk = last,
        Items = new Dictionary<string, object?>(),
        RouteId = "r1",
        ClusterId = "c1",
    };

    private static ScriptPhaseContext LogContext() => new()
    {
        Phase = ScriptPhases.Log,
        Method = "GET",
        Path = "/",
        Host = "test",
        Headers = [],
        Items = new Dictionary<string, object?>(),
        RouteId = "r1",
        ClusterId = "c1",
    };

    // -------------------------------------------------------------------------------------
    // Tests
    // -------------------------------------------------------------------------------------

    [TestMethod]
    public async Task Chain_RunsHostsInOrder_MergingHeaderEdits()
    {
        var chain = new ScriptPhaseHostChain([
            new MarkerHost("a"),
            new MarkerHost("b"),
            new MarkerHost("c"),
        ]);

        var result = await chain.OnRequestHeadersAsync(RequestHeadersContext(), default);

        Assert.AreEqual(ScriptAction.Continue, result.Action);
        Assert.AreEqual("abc", result.SetHeaders!["x-order"], "markers must appear in declaration order");
    }

    [TestMethod]
    public async Task Chain_FirstDeny_StopsRemainingHosts()
    {
        var chain = new ScriptPhaseHostChain([
            new MarkerHost("a"),
            new MarkerHost("DENY", deny: true),
            new MarkerHost("c"), // must NOT run
        ]);

        var result = await chain.OnRequestHeadersAsync(RequestHeadersContext(), default);

        Assert.AreEqual(ScriptAction.Deny, result.Action);
        Assert.AreEqual(418, result.LocalResponse!.Status);
        // "c" must not have added its marker.
        Assert.IsFalse(result.SetHeaders?.ContainsKey("x-order") ?? false,
            "deny result must not carry markers from subsequent hosts");
    }

    [TestMethod]
    public async Task Chain_BodyEdit_FromEarlierHost_IsVisibleToLaterHost()
    {
        var replacement = "replaced"u8.ToArray();
        var capture = new BodyCaptureHost();
        var chain = new ScriptPhaseHostChain([
            new BodyReplaceHost(replacement),
            capture,
        ]);

        await chain.OnRequestBodyAsync(BodyContext("original"u8.ToArray()), default);

        CollectionAssert.AreEqual(replacement, capture.CapturedBody,
            "a body edit from host N must be the chunk host N+1 receives");
    }

    [TestMethod]
    public async Task Chain_LogPhase_ThrowingHost_ContinuesToNextHost()
    {
        int secondHostCalled = 0;
        var second = new MarkerHost("ok", throwOnLog: false);
        // Wrap second in a counting adaptor via a local stub.
        var counter = new CountingLogHost(() => Interlocked.Increment(ref secondHostCalled));

        var chain = new ScriptPhaseHostChain([
            new MarkerHost("throws", throwOnLog: true),
            counter,
        ]);

        // Must not throw; the exception from the first host must be swallowed.
        await chain.OnLogAsync(LogContext(), default);

        Assert.AreEqual(1, secondHostCalled, "the second log host must run even when the first one throws");
    }

    [TestMethod]
    public async Task Chain_NonLogPhase_ThrowingHost_PropagatesException()
    {
        var chain = new ScriptPhaseHostChain([new MarkerHost("boom", throwOnRequest: true)]);

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => chain.OnRequestHeadersAsync(RequestHeadersContext(), default).AsTask());
    }

    [TestMethod]
    public void Chain_Phases_IsUnionOfAllHostPhases()
    {
        var a = new MarkerHost("a"); // RequestHeaders | Log
        var b = new BodyReplaceHost([]); // RequestBody

        var chain = new ScriptPhaseHostChain([a, b]);

        Assert.IsTrue(chain.Phases.HasFlag(ScriptPhases.RequestHeaders));
        Assert.IsTrue(chain.Phases.HasFlag(ScriptPhases.RequestBody));
        Assert.IsTrue(chain.Phases.HasFlag(ScriptPhases.Log));
    }

    // -------------------------------------------------------------------------------------
    // Helper stubs
    // -------------------------------------------------------------------------------------

    private sealed class CountingLogHost(Action onLog) : IScriptPhaseHost
    {
        public ScriptPhases Phases => ScriptPhases.Log;

        public ValueTask OnLogAsync(ScriptPhaseContext context, CancellationToken cancellationToken)
        {
            onLog();
            return default;
        }
    }
}
