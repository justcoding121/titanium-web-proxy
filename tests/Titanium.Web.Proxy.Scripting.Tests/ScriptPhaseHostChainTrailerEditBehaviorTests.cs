using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Abstractions.Scripting;

namespace Titanium.Web.Proxy.Scripting.Tests;

/// <summary>
///     Remaining ScriptPhaseHostChain leftovers toward 90%: trailer-phase header edits merge
///     across hosts the same way request-header edits do.
/// </summary>
[TestClass]
public class ScriptPhaseHostChainTrailerEditBehaviorTests
{
    private sealed class RecordingHost(ScriptPhases phases = ScriptPhases.All) : IScriptPhaseHost
    {
        public List<ScriptPhaseContext> Contexts { get; } = [];

        public Func<ScriptPhaseContext, ScriptPhaseResult>? Respond { get; init; }

        public ScriptPhases Phases => phases;

        private ValueTask<ScriptPhaseResult> Record(ScriptPhaseContext context)
        {
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

    private static ScriptPhaseContext Context(ScriptPhases phase, IEnumerable<KeyValuePair<string, string>>? headers = null) => new()
    {
        Phase = phase,
        Method = "GET",
        Path = "/p",
        Host = "h.test",
        Headers = (headers ?? []).ToArray(),
        Chunk = ReadOnlyMemory<byte>.Empty,
        Items = new Dictionary<string, object?>(),
        RouteId = "r1",
        ClusterId = "c1",
    };

    [TestMethod]
    public async Task RequestTrailers_LaterHostSeesEarlierTrailerEdits()
    {
        var first = new RecordingHost(ScriptPhases.RequestTrailers)
        {
            Respond = _ => new ScriptPhaseResult
            {
                SetHeaders = new Dictionary<string, string> { ["grpc-timeout"] = "1S", ["x-keep"] = "new" },
                RemoveHeaders = ["x-drop"],
            },
        };
        var second = new RecordingHost(ScriptPhases.RequestTrailers);
        var chain = new ScriptPhaseHostChain([first, second]);
        var context = Context(
            ScriptPhases.RequestTrailers,
            [
                new("x-drop", "gone"),
                new("X-Keep", "old"),
                new("x-other", "o"),
            ]);

        var result = await chain.OnRequestTrailersAsync(context, default);

        var seen = second.Contexts.Single();
        Assert.IsFalse(seen.Headers.Any(h => h.Key == "x-drop"));
        Assert.AreEqual("new", seen.Headers.Single(h => string.Equals(h.Key, "x-keep", StringComparison.OrdinalIgnoreCase)).Value);
        Assert.AreEqual("1S", seen.Headers.Single(h => h.Key == "grpc-timeout").Value);
        Assert.AreEqual("o", seen.Headers.Single(h => h.Key == "x-other").Value);
        Assert.AreEqual("1S", result.SetHeaders!["grpc-timeout"]);
        CollectionAssert.AreEqual(new[] { "x-drop" }, result.RemoveHeaders!.ToArray());
    }

    [TestMethod]
    public async Task ResponseTrailers_RemovedThenSetAgain_EndsUpSet()
    {
        var chain = new ScriptPhaseHostChain(
        [
            new RecordingHost(ScriptPhases.ResponseTrailers)
            {
                Respond = _ => new ScriptPhaseResult { RemoveHeaders = ["grpc-status"] },
            },
            new RecordingHost(ScriptPhases.ResponseTrailers)
            {
                Respond = _ => new ScriptPhaseResult
                {
                    SetHeaders = new Dictionary<string, string> { ["Grpc-Status"] = "0" },
                },
            },
        ]);

        var result = await chain.OnResponseTrailersAsync(
            Context(ScriptPhases.ResponseTrailers, [new("grpc-status", "14")]),
            default);

        Assert.AreEqual("0", result.SetHeaders!["grpc-status"]);
        Assert.AreEqual(0, result.RemoveHeaders!.Count);
    }
}
