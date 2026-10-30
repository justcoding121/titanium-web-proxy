using Titanium.Web.Proxy.Abstractions.Scripting;

namespace Titanium.Web.Proxy.Scripting;

/// <summary>
/// Runs several script hosts as one <c>ReverseProxyOptions.ScriptPipeline</c>, in list order. Each host sees the header and
/// body edits of the hosts before it; the first deny wins and stops the chain. A host that pauses is resumed here, so the
/// chain itself never pauses. The cluster choice of the last host that made one wins.
/// </summary>
public sealed class ScriptPhaseHostChain : IScriptPhaseHost
{
    private const int MaxResumes = 64;
    private readonly IScriptPhaseHost[] _hosts;

    public ScriptPhaseHostChain(IEnumerable<IScriptPhaseHost> hosts)
    {
        ArgumentNullException.ThrowIfNull(hosts);
        _hosts = hosts.ToArray();
        foreach (var host in _hosts)
        {
            Phases |= host.Phases;
            if (host.BodyMode == ScriptBodyMode.Buffered)
            {
                BodyMode = ScriptBodyMode.Buffered;
            }
        }
    }

    public IReadOnlyList<IScriptPhaseHost> Hosts => _hosts;

    public ScriptPhases Phases { get; }

    /// <summary>Buffered when any host buffers, so a buffering host always sees whole bodies.</summary>
    public ScriptBodyMode BodyMode { get; }

    public ValueTask<ScriptPhaseResult> OnRequestHeadersAsync(ScriptPhaseContext context, CancellationToken cancellationToken) =>
        RunAsync(context, cancellationToken);

    public ValueTask<ScriptPhaseResult> OnRequestBodyAsync(ScriptPhaseContext context, CancellationToken cancellationToken) =>
        RunAsync(context, cancellationToken);

    public ValueTask<ScriptPhaseResult> OnRequestTrailersAsync(ScriptPhaseContext context, CancellationToken cancellationToken) =>
        RunAsync(context, cancellationToken);

    public ValueTask<ScriptPhaseResult> OnResponseHeadersAsync(ScriptPhaseContext context, CancellationToken cancellationToken) =>
        RunAsync(context, cancellationToken);

    public ValueTask<ScriptPhaseResult> OnResponseBodyAsync(ScriptPhaseContext context, CancellationToken cancellationToken) =>
        RunAsync(context, cancellationToken);

    public ValueTask<ScriptPhaseResult> OnResponseTrailersAsync(ScriptPhaseContext context, CancellationToken cancellationToken) =>
        RunAsync(context, cancellationToken);

    public ValueTask<ScriptPhaseResult> OnResumeAsync(ScriptPhaseContext context, CancellationToken cancellationToken) =>
        new(ScriptPhaseResult.Continue);

    public async ValueTask OnLogAsync(ScriptPhaseContext context, CancellationToken cancellationToken)
    {
        foreach (var host in _hosts)
        {
            if ((host.Phases & ScriptPhases.Log) == 0)
            {
                continue;
            }

            try
            {
                await host.OnLogAsync(context, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception) when (!cancellationToken.IsCancellationRequested)
            {
                // One host's log hook never stops the next one.
            }
        }
    }

    private async ValueTask<ScriptPhaseResult> RunAsync(ScriptPhaseContext context, CancellationToken cancellationToken)
    {
        var headers = context.Headers;
        var chunk = context.Chunk;
        Dictionary<string, string?>? edits = null;
        byte[]? body = null;
        string? cluster = null;

        foreach (var host in _hosts)
        {
            if ((host.Phases & context.Phase) == 0)
            {
                continue;
            }

            var view = ReferenceEquals(headers, context.Headers) && chunk.Equals(context.Chunk) ? context : new ScriptPhaseContext
            {
                Phase = context.Phase,
                RouteId = context.RouteId,
                ClusterId = cluster ?? context.ClusterId,
                Method = context.Method,
                Path = context.Path,
                Host = context.Host,
                IsHttps = context.IsHttps,
                ClientEndPoint = context.ClientEndPoint,
                StatusCode = context.StatusCode,
                Headers = headers,
                Chunk = chunk,
                IsLastChunk = context.IsLastChunk,
                Elapsed = context.Elapsed,
                Error = context.Error,
                Items = context.Items,
            };

            var result = await InvokeAsync(host, view, cancellationToken).ConfigureAwait(false);
            if (result.Action == ScriptAction.Deny)
            {
                return result;
            }

            if (result.RemoveHeaders is { Count: > 0 } || result.SetHeaders is { Count: > 0 })
            {
                edits ??= new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
                var list = new List<KeyValuePair<string, string>>(headers);
                foreach (var name in result.RemoveHeaders ?? [])
                {
                    edits[name] = null;
                    list.RemoveAll(h => string.Equals(h.Key, name, StringComparison.OrdinalIgnoreCase));
                }

                foreach (var pair in result.SetHeaders ?? new Dictionary<string, string>())
                {
                    edits[pair.Key] = pair.Value;
                    list.RemoveAll(h => string.Equals(h.Key, pair.Key, StringComparison.OrdinalIgnoreCase));
                    list.Add(pair);
                }

                headers = list;
            }

            if (result.ReplaceBody is { } replaced)
            {
                body = replaced;
                chunk = replaced;
            }

            cluster = result.UpstreamClusterId ?? cluster;
        }

        if (edits is null && body is null && cluster is null)
        {
            return ScriptPhaseResult.Continue;
        }

        return new ScriptPhaseResult
        {
            SetHeaders = edits?.Where(e => e.Value is not null).ToDictionary(e => e.Key, e => e.Value!, StringComparer.OrdinalIgnoreCase),
            RemoveHeaders = edits?.Where(e => e.Value is null).Select(e => e.Key).ToList(),
            ReplaceBody = body,
            UpstreamClusterId = cluster,
        };
    }

    private static async ValueTask<ScriptPhaseResult> InvokeAsync(IScriptPhaseHost host, ScriptPhaseContext context,
        CancellationToken cancellationToken)
    {
        var result = await (context.Phase switch
        {
            ScriptPhases.RequestHeaders => host.OnRequestHeadersAsync(context, cancellationToken),
            ScriptPhases.RequestBody => host.OnRequestBodyAsync(context, cancellationToken),
            ScriptPhases.RequestTrailers => host.OnRequestTrailersAsync(context, cancellationToken),
            ScriptPhases.ResponseHeaders => host.OnResponseHeadersAsync(context, cancellationToken),
            ScriptPhases.ResponseBody => host.OnResponseBodyAsync(context, cancellationToken),
            ScriptPhases.ResponseTrailers => host.OnResponseTrailersAsync(context, cancellationToken),
            _ => new ValueTask<ScriptPhaseResult>(ScriptPhaseResult.Continue),
        }).ConfigureAwait(false);

        for (var resumes = 1; result.Action == ScriptAction.Pause; resumes++)
        {
            if (resumes > MaxResumes)
            {
                return ScriptPhaseResult.Deny(504, "Script did not finish.");
            }

            context.ResumeCount = resumes;
            result = await host.OnResumeAsync(context, cancellationToken).ConfigureAwait(false);
        }

        return result;
    }
}
