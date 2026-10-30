using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Titanium.Web.Proxy.Abstractions;
using Titanium.Web.Proxy.Abstractions.Routing;
using Titanium.Web.Proxy.Abstractions.Scripting;
using Titanium.Web.Proxy.EventArguments;
using Titanium.Web.Proxy.Extensions;
using Titanium.Web.Proxy.Http;

namespace Titanium.Web.Proxy.Routing;

/// <summary>
/// Calls an <see cref="IScriptPhaseHost"/> at the HTTP phase seams. Every entry point is reached only after the caller
/// saw a non-null host, so with no host configured nothing here runs and nothing is allocated. Core never references a
/// scripting engine; the host is whatever the embedding application supplies.
/// </summary>
internal static class ScriptPhaseDispatch
{
    /// <summary>Resumes a paused callback may take before the host gives up with 504.</summary>
    internal const int MaxResumes = 64;

    internal readonly record struct RequestOutcome(bool Denied, string? ClusterId);

    /// <summary>
    /// Request headers, then (buffered mode) the whole request body and request trailers. A deny stages the response and
    /// returns <c>Denied</c>. A cluster id asks the caller to bind the request to that cluster.
    /// </summary>
    internal static async ValueTask<RequestOutcome> RunRequestAsync(
        ReverseProxyOptions options, RouteConfig? route, SessionEventArgs args)
    {
        var host = options.ScriptPipeline;
        if (host is null)
        {
            return default;
        }

        var phases = host.Phases;
        var request = args.HttpClient.Request;
        var token = args.CancellationToken;
        string? clusterId = null;

        if ((phases & ScriptPhases.RequestHeaders) != 0)
        {
            var result = await RunAsync(host, Build(args, ScriptPhases.RequestHeaders, route, request.Headers), token)
                .ConfigureAwait(false);
            if (result.Action == ScriptAction.Deny)
            {
                StageDeny(args, result);
                return new RequestOutcome(true, null);
            }

            ApplyHeaderEdits(request.Headers, result);
            clusterId = result.UpstreamClusterId;
        }

        if (host.BodyMode == ScriptBodyMode.Buffered &&
            (phases & (ScriptPhases.RequestBody | ScriptPhases.RequestTrailers)) != 0 &&
            request.HasBody)
        {
            var body = await args.GetRequestBody(token).ConfigureAwait(false);
            if ((phases & ScriptPhases.RequestBody) != 0)
            {
                var result = await RunAsync(host,
                    Build(args, ScriptPhases.RequestBody, route, request.Headers, body, isLast: true), token).ConfigureAwait(false);
                if (result.Action == ScriptAction.Deny)
                {
                    StageDeny(args, result);
                    return new RequestOutcome(true, null);
                }

                if (result.ReplaceBody is { } replaced)
                {
                    args.SetRequestBody(replaced);
                }
            }

            if ((phases & ScriptPhases.RequestTrailers) != 0)
            {
                var result = await RunAsync(host,
                    Build(args, ScriptPhases.RequestTrailers, route, request.TrailingHeaders, isLast: true), token).ConfigureAwait(false);
                if (result.Action == ScriptAction.Deny)
                {
                    StageDeny(args, result);
                    return new RequestOutcome(true, null);
                }

                ApplyHeaderEdits(request.TrailingHeaders, result);
            }
        }

        return new RequestOutcome(false, clusterId);
    }

    /// <summary>Response headers, then (buffered mode) the whole response body and response trailers.</summary>
    internal static async ValueTask RunResponseAsync(IScriptPhaseHost host, SessionEventArgs args)
    {
        var phases = host.Phases;
        var response = args.HttpClient.Response;
        var token = args.CancellationToken;

        if ((phases & ScriptPhases.ResponseHeaders) != 0)
        {
            var result = await RunAsync(host, Build(args, ScriptPhases.ResponseHeaders, null, response.Headers), token)
                .ConfigureAwait(false);
            if (result.Action == ScriptAction.Deny)
            {
                StageDeny(args, result);
                return;
            }

            ApplyHeaderEdits(response.Headers, result);
        }

        if (host.BodyMode != ScriptBodyMode.Buffered ||
            (phases & (ScriptPhases.ResponseBody | ScriptPhases.ResponseTrailers)) == 0 ||
            response.Locked || response.IsSynthetic || !response.OriginalHasBody)
        {
            return;
        }

        var body = await args.GetResponseBody(token).ConfigureAwait(false);
        if ((phases & ScriptPhases.ResponseBody) != 0)
        {
            var result = await RunAsync(host,
                Build(args, ScriptPhases.ResponseBody, null, response.Headers, body, isLast: true), token).ConfigureAwait(false);
            if (result.Action == ScriptAction.Deny)
            {
                StageDeny(args, result);
                return;
            }

            if (result.ReplaceBody is { } replaced)
            {
                args.SetResponseBody(replaced);
            }
        }

        if ((phases & ScriptPhases.ResponseTrailers) != 0)
        {
            var result = await RunAsync(host,
                Build(args, ScriptPhases.ResponseTrailers, null, response.TrailingHeaders, isLast: true), token).ConfigureAwait(false);
            if (result.Action == ScriptAction.Deny)
            {
                StageDeny(args, result);
                return;
            }

            ApplyHeaderEdits(response.TrailingHeaders, result);
        }
    }

    /// <summary>
    /// Streaming mode: one body piece as it is relayed. Called from the body-write hook, so it covers HTTP/1, HTTP/2 and
    /// HTTP/3 without a seam of its own. The headers are already on the wire, so a deny ends the body instead of answering.
    /// On the last piece the trailer phase runs and its edits join the trailer block that follows.
    /// </summary>
    internal static async Task RunBodyChunkAsync(IScriptPhaseHost host, BeforeBodyWriteEventArgs e, bool isRequest)
    {
        var phases = host.Phases;
        var bodyPhase = isRequest ? ScriptPhases.RequestBody : ScriptPhases.ResponseBody;
        var trailerPhase = isRequest ? ScriptPhases.RequestTrailers : ScriptPhases.ResponseTrailers;
        var session = e.Session;
        var token = session.CancellationToken;
        var message = isRequest
            ? (RequestResponseBase)session.HttpClient.Request
            : session.HttpClient.Response;

        if ((phases & bodyPhase) != 0)
        {
            var result = await RunAsync(host,
                Build(session, bodyPhase, null, message.Headers, e.BodyBytes, e.IsLastChunk), token).ConfigureAwait(false);
            if (result.Action == ScriptAction.Deny)
            {
                e.BodyBytes = Array.Empty<byte>();
                e.IsLastChunk = true;
                session.ForwarderError = Abstractions.Diagnostics.ForwarderError.PolicyDenied;
                return;
            }

            if (result.ReplaceBody is { } replaced)
            {
                e.BodyBytes = replaced;
            }
        }

        if (!e.IsLastChunk || (phases & trailerPhase) == 0 || !e.IsChunked)
        {
            return;
        }

        var trailers = await RunAsync(host,
            Build(session, trailerPhase, null, message.TrailingHeaders, isLast: true), token).ConfigureAwait(false);
        if (trailers.Action != ScriptAction.Deny)
        {
            ApplyHeaderEdits(message.TrailingHeaders, trailers);
        }
    }

    /// <summary>The log phase. Runs after the response, so a failure here is swallowed.</summary>
    internal static async ValueTask RunLogAsync(IScriptPhaseHost host, SessionEventArgs args)
    {
        if ((host.Phases & ScriptPhases.Log) == 0)
        {
            return;
        }

        try
        {
            var context = Build(args, ScriptPhases.Log, null, args.HttpClient.Response.Headers, elapsed: args.Timing?.TotalDuration,
                error: args.Exception?.Message);
            await host.OnLogAsync(context, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // A log hook never changes the outcome of a request that already finished.
        }
    }

    private static async ValueTask<ScriptPhaseResult> RunAsync(
        IScriptPhaseHost host, ScriptPhaseContext context, CancellationToken cancellationToken)
    {
        try
        {
            var result = await Invoke(host, context, cancellationToken).ConfigureAwait(false);
            var resumes = 0;
            while (result.Action == ScriptAction.Pause)
            {
                if (++resumes > MaxResumes)
                {
                    return ScriptPhaseResult.Deny(504, "Script did not finish.");
                }

                context.ResumeCount = resumes;
                result = await host.OnResumeAsync(context, cancellationToken).ConfigureAwait(false);
            }

            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            // A script that throws never opens a route: the request is refused.
            return ScriptPhaseResult.Deny(500, "Script failed.");
        }
    }

    private static ValueTask<ScriptPhaseResult> Invoke(
        IScriptPhaseHost host, ScriptPhaseContext context, CancellationToken cancellationToken) =>
        context.Phase switch
        {
            ScriptPhases.RequestHeaders => host.OnRequestHeadersAsync(context, cancellationToken),
            ScriptPhases.RequestBody => host.OnRequestBodyAsync(context, cancellationToken),
            ScriptPhases.RequestTrailers => host.OnRequestTrailersAsync(context, cancellationToken),
            ScriptPhases.ResponseHeaders => host.OnResponseHeadersAsync(context, cancellationToken),
            ScriptPhases.ResponseBody => host.OnResponseBodyAsync(context, cancellationToken),
            ScriptPhases.ResponseTrailers => host.OnResponseTrailersAsync(context, cancellationToken),
            _ => new ValueTask<ScriptPhaseResult>(ScriptPhaseResult.Continue),
        };

    private static ScriptPhaseContext Build(
        SessionEventArgsBase session,
        ScriptPhases phase,
        RouteConfig? route,
        HeaderCollection headers,
        ReadOnlyMemory<byte> chunk = default,
        bool isLast = false,
        TimeSpan? elapsed = null,
        string? error = null)
    {
        var request = session.HttpClient.Request;
        route ??= session.MatchedRoute;
        var extras = session.Extras ??= new RoutingFeatureState();
        extras.ScriptItems ??= new Dictionary<string, object?>();

        return new ScriptPhaseContext
        {
            Phase = phase,
            RouteId = route?.Id,
            ClusterId = route?.ClusterId,
            Method = request.Method ?? string.Empty,
            Path = request.RequestUriString8.GetString(),
            Host = request.Host ?? string.Empty,
            IsHttps = request.IsHttps,
            ClientEndPoint = session.ClientRemoteEndPoint,
            StatusCode = session.HttpClient.Response?.StatusCode ?? 0,
            Headers = Snapshot(headers),
            Chunk = chunk,
            IsLastChunk = isLast,
            Elapsed = elapsed ?? TimeSpan.Zero,
            Error = error,
            Items = extras.ScriptItems,
        };
    }

    private static IReadOnlyList<KeyValuePair<string, string>> Snapshot(HeaderCollection headers)
    {
        var all = headers.GetAllHeaders();
        if (all.Count == 0)
        {
            return Array.Empty<KeyValuePair<string, string>>();
        }

        var copy = new KeyValuePair<string, string>[all.Count];
        for (var i = 0; i < copy.Length; i++)
        {
            copy[i] = new KeyValuePair<string, string>(all[i].Name, all[i].Value);
        }

        return copy;
    }

    private static void ApplyHeaderEdits(HeaderCollection headers, ScriptPhaseResult result)
    {
        if (result.RemoveHeaders is { Count: > 0 } remove)
        {
            for (var i = 0; i < remove.Count; i++)
            {
                headers.RemoveHeader(remove[i]);
            }
        }

        if (result.SetHeaders is { Count: > 0 } set)
        {
            foreach (var pair in set)
            {
                headers.SetOrAddHeaderValue(pair.Key, pair.Value);
            }
        }
    }

    private static void StageDeny(SessionEventArgs args, ScriptPhaseResult result)
    {
        var local = result.LocalResponse;
        List<KeyValuePair<string, string>>? headers = null;
        if (local?.Headers is { Count: > 0 } extra)
        {
            headers = new List<KeyValuePair<string, string>>(extra);
        }

        if (!string.IsNullOrEmpty(local?.ContentType))
        {
            headers ??= new List<KeyValuePair<string, string>>(1);
            headers.Add(new KeyValuePair<string, string>("Content-Type", local!.ContentType!));
        }

        RoutePluginDispatch.Stage(args, local?.Status ?? 403, string.Empty, local?.Body, headers);
    }
}
