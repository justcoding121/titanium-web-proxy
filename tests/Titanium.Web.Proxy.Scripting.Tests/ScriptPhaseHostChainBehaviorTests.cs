using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Abstractions.Scripting;

namespace Titanium.Web.Proxy.Scripting.Tests;

/// <summary>Chain behavior the first suite leaves open: every phase entry point, pause/resume, edit merging, and body mode.</summary>
[TestClass]
public class ScriptPhaseHostChainBehaviorTests
{
    private static readonly int[] ExpectedResumeCounts = [1, 2, 3];
    private static readonly string[] ExpectedRemovedHeaderNames = ["x-drop"];

    private sealed class RecordingHost(ScriptPhases phases = ScriptPhases.All, ScriptBodyMode mode = ScriptBodyMode.Streaming) : IScriptPhaseHost
    {
        public List<ScriptPhases> Seen { get; } = [];

        public List<ScriptPhaseContext> Contexts { get; } = [];

        public Func<ScriptPhaseContext, ScriptPhaseResult>? Respond { get; init; }

        public ScriptPhases Phases => phases;

        public ScriptBodyMode BodyMode => mode;

        private ValueTask<ScriptPhaseResult> Record(ScriptPhaseContext context)
        {
            Seen.Add(context.Phase);
            Contexts.Add(context);
            return new(Respond?.Invoke(context) ?? ScriptPhaseResult.Continue);
        }

        public ValueTask<ScriptPhaseResult> OnRequestHeadersAsync(ScriptPhaseContext context, CancellationToken cancellationToken) => Record(context);

        public ValueTask<ScriptPhaseResult> OnRequestBodyAsync(ScriptPhaseContext context, CancellationToken cancellationToken) => Record(context);

        public ValueTask<ScriptPhaseResult> OnRequestTrailersAsync(ScriptPhaseContext context, CancellationToken cancellationToken) => Record(context);

        public ValueTask<ScriptPhaseResult> OnResponseHeadersAsync(ScriptPhaseContext context, CancellationToken cancellationToken) => Record(context);

        public ValueTask<ScriptPhaseResult> OnResponseBodyAsync(ScriptPhaseContext context, CancellationToken cancellationToken) => Record(context);

        public ValueTask<ScriptPhaseResult> OnResponseTrailersAsync(ScriptPhaseContext context, CancellationToken cancellationToken) => Record(context);
    }

    /// <summary>Pauses a fixed number of times, then answers; records the resume counter it was handed.</summary>
    private sealed class PausingHost(int pauses, ScriptPhaseResult final) : IScriptPhaseHost
    {
        public List<int> ResumeCounts { get; } = [];

        public ValueTask<ScriptPhaseResult> OnRequestHeadersAsync(ScriptPhaseContext context, CancellationToken cancellationToken) =>
            new(pauses > 0 ? ScriptPhaseResult.Pause : final);

        public ValueTask<ScriptPhaseResult> OnResumeAsync(ScriptPhaseContext context, CancellationToken cancellationToken)
        {
            ResumeCounts.Add(context.ResumeCount);
            return new(ResumeCounts.Count >= pauses ? final : ScriptPhaseResult.Pause);
        }
    }

    private static ScriptPhaseContext Context(ScriptPhases phase, IEnumerable<KeyValuePair<string, string>>? headers = null,
        byte[]? chunk = null) => new()
    {
        Phase = phase,
        Method = "GET",
        Path = "/p",
        Host = "h.test",
        Headers = (headers ?? []).ToArray(),
        Chunk = chunk ?? [],
        Items = new Dictionary<string, object?>(),
        RouteId = "r1",
        ClusterId = "c1",
    };

    [TestMethod]
    public async Task EveryPhase_ReachesTheMatchingHostCallback()
    {
        var host = new RecordingHost();
        var chain = new ScriptPhaseHostChain([host]);
        var phases = new[]
        {
            ScriptPhases.RequestHeaders, ScriptPhases.RequestBody, ScriptPhases.RequestTrailers,
            ScriptPhases.ResponseHeaders, ScriptPhases.ResponseBody, ScriptPhases.ResponseTrailers,
        };

        foreach (var phase in phases)
        {
            var context = Context(phase);
            var result = phase switch
            {
                ScriptPhases.RequestHeaders => await chain.OnRequestHeadersAsync(context, default),
                ScriptPhases.RequestBody => await chain.OnRequestBodyAsync(context, default),
                ScriptPhases.RequestTrailers => await chain.OnRequestTrailersAsync(context, default),
                ScriptPhases.ResponseHeaders => await chain.OnResponseHeadersAsync(context, default),
                ScriptPhases.ResponseBody => await chain.OnResponseBodyAsync(context, default),
                _ => await chain.OnResponseTrailersAsync(context, default),
            };

            Assert.AreSame(ScriptPhaseResult.Continue, result, $"{phase} with no edits must stay the shared Continue");
        }

        CollectionAssert.AreEqual(phases, host.Seen);
    }

    [TestMethod]
    public async Task HostThatDoesNotListThePhase_IsNeverCalled()
    {
        var headersOnly = new RecordingHost(ScriptPhases.RequestHeaders);
        var bodyHost = new RecordingHost(ScriptPhases.ResponseBody);
        var chain = new ScriptPhaseHostChain([headersOnly, bodyHost]);

        await chain.OnResponseBodyAsync(Context(ScriptPhases.ResponseBody), default);

        Assert.AreEqual(0, headersOnly.Seen.Count);
        Assert.AreEqual(1, bodyHost.Seen.Count);
    }

    [TestMethod]
    public void BodyMode_IsBufferedWhenAnyHostBuffers()
    {
        var streaming = new ScriptPhaseHostChain([new RecordingHost(), new RecordingHost()]);
        var mixed = new ScriptPhaseHostChain([new RecordingHost(), new RecordingHost(mode: ScriptBodyMode.Buffered)]);

        Assert.AreEqual(ScriptBodyMode.Streaming, streaming.BodyMode);
        Assert.AreEqual(ScriptBodyMode.Buffered, mixed.BodyMode);
        Assert.AreEqual(2, mixed.Hosts.Count);
    }

    [TestMethod]
    public void NullHostList_IsRejected()
    {
        Assert.ThrowsExactly<ArgumentNullException>(() => new ScriptPhaseHostChain(null!));
    }

    [TestMethod]
    public async Task OnResume_OnTheChainItself_Continues()
    {
        var chain = new ScriptPhaseHostChain([new RecordingHost()]);

        var result = await chain.OnResumeAsync(Context(ScriptPhases.RequestHeaders), default);

        Assert.AreEqual(ScriptAction.Continue, result.Action);
    }

    [TestMethod]
    public async Task LogPhase_SkipsHostsThatDidNotAskForIt()
    {
        var logging = new LogCountingHost(ScriptPhases.Log);
        var notLogging = new LogCountingHost(ScriptPhases.RequestHeaders);
        var chain = new ScriptPhaseHostChain([notLogging, logging]);

        await chain.OnLogAsync(Context(ScriptPhases.Log), default);

        Assert.AreEqual(0, notLogging.Calls);
        Assert.AreEqual(1, logging.Calls);
    }

    [TestMethod]
    public async Task LogPhase_ThrowWhileCancelled_Propagates()
    {
        // A host fault is swallowed, but a cancelled request must still unwind the log phase.
        using var cts = new CancellationTokenSource();
        var chain = new ScriptPhaseHostChain([new LogCountingHost(ScriptPhases.Log, () =>
        {
            cts.Cancel();
            throw new InvalidOperationException("cancelled mid-log");
        })]);

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => chain.OnLogAsync(Context(ScriptPhases.Log), cts.Token).AsTask());
    }

    [TestMethod]
    public async Task PausingHost_IsResumedWithAnAdvancingCounter_UntilItAnswers()
    {
        var host = new PausingHost(3, new ScriptPhaseResult { UpstreamClusterId = "c9" });
        var chain = new ScriptPhaseHostChain([host]);

        var result = await chain.OnRequestHeadersAsync(Context(ScriptPhases.RequestHeaders), default);

        CollectionAssert.AreEqual(ExpectedResumeCounts, host.ResumeCounts);
        Assert.AreEqual(ScriptAction.Continue, result.Action, "the chain itself never pauses");
        Assert.AreEqual("c9", result.UpstreamClusterId);
    }

    [TestMethod]
    public async Task PausingHost_ThatNeverFinishes_IsDeniedWith504AfterTheResumeBudget()
    {
        var host = new PausingHost(int.MaxValue, ScriptPhaseResult.Continue);
        var chain = new ScriptPhaseHostChain([host, new RecordingHost()]);

        var result = await chain.OnRequestHeadersAsync(Context(ScriptPhases.RequestHeaders), default);

        Assert.AreEqual(ScriptAction.Deny, result.Action);
        Assert.AreEqual(504, result.LocalResponse!.Status);
        Assert.AreEqual(64, host.ResumeCounts.Count);
    }

    [TestMethod]
    public async Task PausingHost_ThatEndsInDeny_StopsTheChain()
    {
        var later = new RecordingHost();
        var chain = new ScriptPhaseHostChain([new PausingHost(1, ScriptPhaseResult.Deny(451, "legal")), later]);

        var result = await chain.OnRequestHeadersAsync(Context(ScriptPhases.RequestHeaders), default);

        Assert.AreEqual(451, result.LocalResponse!.Status);
        Assert.AreEqual(0, later.Seen.Count);
    }

    [TestMethod]
    public async Task LaterHost_SeesEarlierHeaderEdits_AndTheChosenCluster()
    {
        var first = new RecordingHost(ScriptPhases.RequestHeaders)
        {
            Respond = _ => new ScriptPhaseResult
            {
                SetHeaders = new Dictionary<string, string> { ["x-added"] = "1", ["x-keep"] = "new" },
                RemoveHeaders = ["x-drop"],
                UpstreamClusterId = "c-first",
            },
        };
        var second = new RecordingHost(ScriptPhases.RequestHeaders);
        var chain = new ScriptPhaseHostChain([first, second]);
        var context = Context(ScriptPhases.RequestHeaders,
        [
            new("x-drop", "gone"),
            new("X-Keep", "old"),
            new("x-other", "o"),
        ]);

        var result = await chain.OnRequestHeadersAsync(context, default);

        var seen = second.Contexts.Single();
        Assert.AreEqual("c-first", seen.ClusterId);
        Assert.IsFalse(seen.Headers.Any(h => h.Key == "x-drop"));
        Assert.AreEqual("new", seen.Headers.Single(h => string.Equals(h.Key, "x-keep", StringComparison.OrdinalIgnoreCase)).Value);
        Assert.AreEqual("1", seen.Headers.Single(h => h.Key == "x-added").Value);
        Assert.AreEqual("o", seen.Headers.Single(h => h.Key == "x-other").Value);

        Assert.AreEqual("1", result.SetHeaders!["x-added"]);
        CollectionAssert.AreEqual(ExpectedRemovedHeaderNames, result.RemoveHeaders!.ToArray());
        Assert.AreEqual("c-first", result.UpstreamClusterId);
    }

    [TestMethod]
    public async Task LastHostToChooseACluster_Wins_AndAHostThatDoesNotChooseKeepsTheEarlierChoice()
    {
        var chain = new ScriptPhaseHostChain(
        [
            new RecordingHost { Respond = _ => new ScriptPhaseResult { UpstreamClusterId = "a" } },
            new RecordingHost { Respond = _ => new ScriptPhaseResult { UpstreamClusterId = "b" } },
            new RecordingHost(),
        ]);

        var result = await chain.OnRequestHeadersAsync(Context(ScriptPhases.RequestHeaders), default);

        Assert.AreEqual("b", result.UpstreamClusterId);
    }

    [TestMethod]
    public async Task HeaderRemovedByOneHost_AndSetAgainByTheNext_EndsUpSet()
    {
        var chain = new ScriptPhaseHostChain(
        [
            new RecordingHost { Respond = _ => new ScriptPhaseResult { RemoveHeaders = ["x-a"] } },
            new RecordingHost { Respond = _ => new ScriptPhaseResult { SetHeaders = new Dictionary<string, string> { ["X-A"] = "back" } } },
        ]);

        var result = await chain.OnRequestHeadersAsync(Context(ScriptPhases.RequestHeaders, [new("x-a", "1")]), default);

        Assert.AreEqual("back", result.SetHeaders!["x-a"]);
        Assert.AreEqual(0, result.RemoveHeaders!.Count);
    }

    [TestMethod]
    public async Task ReplacedResponseBody_FlowsToTheNextHost_AndOutOfTheChain()
    {
        var capture = new RecordingHost(ScriptPhases.ResponseBody);
        var chain = new ScriptPhaseHostChain(
        [
            new RecordingHost(ScriptPhases.ResponseBody) { Respond = _ => new ScriptPhaseResult { ReplaceBody = "two"u8.ToArray() } },
            capture,
        ]);

        var result = await chain.OnResponseBodyAsync(Context(ScriptPhases.ResponseBody, chunk: "one"u8.ToArray()), default);

        CollectionAssert.AreEqual("two"u8.ToArray(), capture.Contexts.Single().Chunk.ToArray());
        CollectionAssert.AreEqual("two"u8.ToArray(), result.ReplaceBody);
    }

    private sealed class LogCountingHost(ScriptPhases phases, Action? onLog = null) : IScriptPhaseHost
    {
        public int Calls { get; private set; }

        public ScriptPhases Phases => phases;

        public ValueTask OnLogAsync(ScriptPhaseContext context, CancellationToken cancellationToken)
        {
            Calls++;
            onLog?.Invoke();
            return default;
        }
    }
}
