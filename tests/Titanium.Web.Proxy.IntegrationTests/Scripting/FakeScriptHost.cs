using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Concurrent;
using Titanium.Web.Proxy.Abstractions.Scripting;

namespace Titanium.Web.Proxy.IntegrationTests.Scripting;

/// <summary>Records every phase it is called for and answers from optional callbacks. Stands in for a Lua or WASM host.</summary>
internal sealed class FakeScriptHost : IScriptPhaseHost
{
    public ScriptPhases Phases { get; set; } = ScriptPhases.All;
    public ScriptBodyMode BodyMode { get; set; } = ScriptBodyMode.Streaming;
    public ConcurrentQueue<ScriptPhaseContext> Calls { get; } = new();
    public ConcurrentQueue<ScriptPhases> Resumes { get; } = new();
    public ConcurrentQueue<byte[]> BodyPieces { get; } = new();
    public ConcurrentQueue<string> Logs { get; } = new();

    public Func<ScriptPhaseContext, ScriptPhaseResult>? RequestHeaders { get; set; }
    public Func<ScriptPhaseContext, ScriptPhaseResult>? RequestBody { get; set; }
    public Func<ScriptPhaseContext, ScriptPhaseResult>? RequestTrailers { get; set; }
    public Func<ScriptPhaseContext, ScriptPhaseResult>? ResponseHeaders { get; set; }
    public Func<ScriptPhaseContext, ScriptPhaseResult>? ResponseBody { get; set; }
    public Func<ScriptPhaseContext, ScriptPhaseResult>? ResponseTrailers { get; set; }
    public Func<ScriptPhaseContext, ScriptPhaseResult>? Resume { get; set; }

    public int Count(ScriptPhases phase) => Calls.Count(c => c.Phase == phase);

    private ValueTask<ScriptPhaseResult> Answer(ScriptPhaseContext context, Func<ScriptPhaseContext, ScriptPhaseResult>? handler)
    {
        Calls.Enqueue(context);
        if (context.Chunk.Length > 0)
        {
            BodyPieces.Enqueue(context.Chunk.ToArray());
        }

        return new ValueTask<ScriptPhaseResult>(handler?.Invoke(context) ?? ScriptPhaseResult.Continue);
    }

    public ValueTask<ScriptPhaseResult> OnRequestHeadersAsync(ScriptPhaseContext context, CancellationToken cancellationToken) =>
        Answer(context, RequestHeaders);

    public ValueTask<ScriptPhaseResult> OnRequestBodyAsync(ScriptPhaseContext context, CancellationToken cancellationToken) =>
        Answer(context, RequestBody);

    public ValueTask<ScriptPhaseResult> OnRequestTrailersAsync(ScriptPhaseContext context, CancellationToken cancellationToken) =>
        Answer(context, RequestTrailers);

    public ValueTask<ScriptPhaseResult> OnResponseHeadersAsync(ScriptPhaseContext context, CancellationToken cancellationToken) =>
        Answer(context, ResponseHeaders);

    public ValueTask<ScriptPhaseResult> OnResponseBodyAsync(ScriptPhaseContext context, CancellationToken cancellationToken) =>
        Answer(context, ResponseBody);

    public ValueTask<ScriptPhaseResult> OnResponseTrailersAsync(ScriptPhaseContext context, CancellationToken cancellationToken) =>
        Answer(context, ResponseTrailers);

    public ValueTask OnLogAsync(ScriptPhaseContext context, CancellationToken cancellationToken)
    {
        Calls.Enqueue(context);
        Logs.Enqueue($"{context.Method} {context.Path} {context.StatusCode}");
        return default;
    }

    public ValueTask<ScriptPhaseResult> OnResumeAsync(ScriptPhaseContext context, CancellationToken cancellationToken)
    {
        Resumes.Enqueue(context.Phase);
        return new ValueTask<ScriptPhaseResult>(Resume?.Invoke(context) ?? ScriptPhaseResult.Continue);
    }
}

/// <summary>Records L4 events and answers from optional callbacks.</summary>
internal sealed class FakeStreamFilter : IStreamScriptFilter
{
    public ConcurrentQueue<string> Events { get; } = new();
    public Func<StreamConnectionContext, StreamFilterResult>? NewConnection { get; set; }
    public Func<byte[], StreamFilterResult>? Downstream { get; set; }
    public Func<byte[], StreamFilterResult>? Upstream { get; set; }

    public ValueTask<StreamFilterResult> OnNewConnectionAsync(StreamConnectionContext context, CancellationToken cancellationToken)
    {
        Events.Enqueue("new");
        return new ValueTask<StreamFilterResult>(NewConnection?.Invoke(context) ?? StreamFilterResult.Continue);
    }

    public ValueTask<StreamFilterResult> OnDownstreamDataAsync(
        StreamConnectionContext context, ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
    {
        var copy = data.ToArray();
        Events.Enqueue("down:" + System.Text.Encoding.ASCII.GetString(copy));
        return new ValueTask<StreamFilterResult>(Downstream?.Invoke(copy) ?? StreamFilterResult.Continue);
    }

    public ValueTask<StreamFilterResult> OnUpstreamDataAsync(
        StreamConnectionContext context, ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
    {
        var copy = data.ToArray();
        Events.Enqueue("up:" + System.Text.Encoding.ASCII.GetString(copy));
        return new ValueTask<StreamFilterResult>(Upstream?.Invoke(copy) ?? StreamFilterResult.Continue);
    }

    public ValueTask OnCloseAsync(StreamConnectionContext context)
    {
        Events.Enqueue("close");
        return default;
    }
}
