using Microsoft.Extensions.Logging;
using Titanium.Web.Proxy.Abstractions.Middleware;
using Titanium.Web.Proxy.Abstractions.Scripting;
using Titanium.Web.Proxy.EventArguments;

namespace Titanium.Web.Proxy.Scripting.Lua;

/// <summary>
/// One configured <c>kind: lua</c> plugin. As a route plugin (<see cref="IProxyMiddleware"/>) it runs
/// <c>on_request_headers</c> (and, with <c>bodyMode: buffered</c>, <c>on_request_body</c>); set as
/// <c>ReverseProxyOptions.ScriptPipeline</c> (<see cref="IScriptPhaseHost"/>) it runs every HTTP phase the script defines.
/// A call never pauses the host: waiting (callouts, sleeps) happens inside the phase call.
/// </summary>
internal sealed partial class LuaPlugin : IProxyMiddleware, IScriptPhaseHost, IRequiresSessionPathMiddleware, IDisposable
{
    private static readonly ScriptPhases[] AllPhases =
    [
        ScriptPhases.RequestHeaders, ScriptPhases.RequestBody, ScriptPhases.RequestTrailers, ScriptPhases.ResponseHeaders,
        ScriptPhases.ResponseBody, ScriptPhases.ResponseTrailers, ScriptPhases.Log,
    ];

    private readonly string _varsKey;
    private int _disposed;

    internal LuaPlugin(LuaRuntime runtime)
    {
        Runtime = runtime;
        _varsKey = "twp.lua.vars:" + runtime.Name;
        Phases = PhasesOf(runtime);
    }

    public LuaRuntime Runtime { get; }

    public string Name => Runtime.Name;

    public ScriptPhases Phases { get; }

    public ScriptBodyMode BodyMode => Runtime.Options.BodyMode;

    private ILogger? Logger => Runtime.Logger;

    private static ScriptPhases PhasesOf(LuaRuntime runtime)
    {
        var phases = ScriptPhases.None;
        foreach (var phase in AllPhases.Where(p => runtime.Defines(LuaSnapshot.FunctionName(p))))
        {
            phases |= phase;
        }

        return phases;
    }

    // IScriptPhaseHost.

    public ValueTask<ScriptPhaseResult> OnRequestHeadersAsync(ScriptPhaseContext context, CancellationToken cancellationToken) =>
        RunPhaseAsync(context, cancellationToken);

    public ValueTask<ScriptPhaseResult> OnRequestBodyAsync(ScriptPhaseContext context, CancellationToken cancellationToken) =>
        RunPhaseAsync(context, cancellationToken);

    public ValueTask<ScriptPhaseResult> OnRequestTrailersAsync(ScriptPhaseContext context, CancellationToken cancellationToken) =>
        RunPhaseAsync(context, cancellationToken);

    public ValueTask<ScriptPhaseResult> OnResponseHeadersAsync(ScriptPhaseContext context, CancellationToken cancellationToken) =>
        RunPhaseAsync(context, cancellationToken);

    public ValueTask<ScriptPhaseResult> OnResponseBodyAsync(ScriptPhaseContext context, CancellationToken cancellationToken) =>
        RunPhaseAsync(context, cancellationToken);

    public ValueTask<ScriptPhaseResult> OnResponseTrailersAsync(ScriptPhaseContext context, CancellationToken cancellationToken) =>
        RunPhaseAsync(context, cancellationToken);

    /// <summary>Lua phases finish inside the phase call, so there is never anything to resume.</summary>
    public ValueTask<ScriptPhaseResult> OnResumeAsync(ScriptPhaseContext context, CancellationToken cancellationToken) =>
        new(ScriptPhaseResult.Continue);

    public async ValueTask OnLogAsync(ScriptPhaseContext context, CancellationToken cancellationToken)
    {
        var vars = TakeVars(context.Items);
        context.Items.Remove(_varsKey);
        if (!Runtime.Defines("on_log"))
        {
            return;
        }

        try
        {
            await Runtime.CallAsync("on_log", LuaSnapshot.Http(Name, context, vars), cancellationToken).ConfigureAwait(false);
        }
        catch (LuaScriptException ex)
        {
            Logger?.LogWarning(ex, "Lua plugin '{Plugin}' on_log failed.", Name);
        }
    }

    private async ValueTask<ScriptPhaseResult> RunPhaseAsync(ScriptPhaseContext context, CancellationToken cancellationToken)
    {
        var function = LuaSnapshot.FunctionName(context.Phase);
        if (!Runtime.Defines(function))
        {
            return ScriptPhaseResult.Continue;
        }

        LuaCallResult result;
        try
        {
            result = await Runtime.CallAsync(function, LuaSnapshot.Http(Name, context, TakeVars(context.Items)), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (LuaScriptException ex)
        {
            return Failed(function, ex);
        }

        if (result.Vars is not null)
        {
            context.Items[_varsKey] = result.Vars;
        }

        return ToPhaseResult(result, context.Phase);
    }

    private ScriptPhaseResult Failed(string function, LuaScriptException ex)
    {
        Logger?.LogWarning("Lua plugin '{Plugin}' {Function} failed ({Reason}): {Error}", Name, function,
            LuaScriptException.Reason(ex.Failure), ex.Message);
        return Runtime.Options.FailOpen ? ScriptPhaseResult.Continue : ScriptPhaseResult.Deny(503, "Script failed.");
    }

    private byte[]? TakeVars(IDictionary<string, object?> items) =>
        items.TryGetValue(_varsKey, out var value) ? value as byte[] : null;

    internal static ScriptPhaseResult ToPhaseResult(LuaCallResult result, ScriptPhases phase)
    {
        if (result.Deny is { } deny)
        {
            return new ScriptPhaseResult
            {
                Action = ScriptAction.Deny,
                LocalResponse = new ScriptLocalResponse { Status = deny.Status, Body = deny.Body, Headers = deny.Headers },
            };
        }

        if (result.HeaderEdits is null && result.Body is null && result.ClusterId is null)
        {
            return ScriptPhaseResult.Continue;
        }

        Dictionary<string, string>? set = null;
        List<string>? remove = null;
        if (result.HeaderEdits is { } edits)
        {
            // Replay in order so the last edit of a name wins.
            var final = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            foreach (var edit in edits)
            {
                final[edit.Key] = edit.Value;
            }

            foreach (var pair in final)
            {
                if (pair.Value is null)
                {
                    (remove ??= []).Add(pair.Key);
                }
                else
                {
                    (set ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase))[pair.Key] = pair.Value;
                }
            }
        }

        return new ScriptPhaseResult
        {
            SetHeaders = set,
            RemoveHeaders = remove,
            ReplaceBody = result.Body,
            UpstreamClusterId = phase == ScriptPhases.RequestHeaders ? result.ClusterId : null,
        };
    }

    // IProxyMiddleware: request headers (and a buffered request body), applied straight to the session.

    public async ValueTask InvokeAsync(ProxyMiddlewareContext context, ProxyMiddlewareDelegate next, CancellationToken cancellationToken)
    {
        if (context.Session is not SessionEventArgs session)
        {
            await next(context, cancellationToken).ConfigureAwait(false);
            return;
        }

        var request = session.HttpClient.Request;
        var phase = new ScriptPhaseContext
        {
            Phase = ScriptPhases.RequestHeaders,
            RouteId = session.MatchedRoute?.Id,
            ClusterId = session.MatchedRoute?.ClusterId,
            Method = request.Method ?? string.Empty,
            Path = request.RequestUriString,
            Host = request.Host ?? string.Empty,
            IsHttps = request.IsHttps,
            ClientEndPoint = session.ClientRemoteEndPoint,
            Headers = Snapshot(request.Headers),
            Items = context.Items,
        };

        // Failures throw to RoutePluginMiddleware, which applies failMode and its wall timeout.
        if (Runtime.Defines("on_request_headers"))
        {
            var result = await Runtime.CallAsync("on_request_headers", LuaSnapshot.Http(Name, phase, null), cancellationToken)
                .ConfigureAwait(false);
            if (result.Vars is not null)
            {
                context.Items[_varsKey] = result.Vars;
            }

            if (Handled(context, result))
            {
                return;
            }

            Apply(request.Headers, result);
        }

        if (BodyMode == ScriptBodyMode.Buffered && Runtime.Defines("on_request_body") && request.HasBody)
        {
            var body = await session.GetRequestBody(cancellationToken).ConfigureAwait(false);
            if (body.Length > Runtime.Options.BufferedBodyBytes)
            {
                context.IsHandled = true;
                context.HandledStatusCode = 413;
                context.HandledBody = "Request body exceeds the Lua plugin's buffered-body limit.";
                return;
            }

            var bodyPhase = new ScriptPhaseContext
            {
                Phase = ScriptPhases.RequestBody,
                RouteId = phase.RouteId,
                ClusterId = phase.ClusterId,
                Method = phase.Method,
                Path = phase.Path,
                Host = phase.Host,
                IsHttps = phase.IsHttps,
                ClientEndPoint = phase.ClientEndPoint,
                Headers = Snapshot(request.Headers),
                Chunk = body,
                IsLastChunk = true,
                Items = context.Items,
            };
            var result = await Runtime.CallAsync("on_request_body", LuaSnapshot.Http(Name, bodyPhase, TakeVars(context.Items)),
                cancellationToken).ConfigureAwait(false);
            if (Handled(context, result))
            {
                return;
            }

            Apply(request.Headers, result);
            if (result.Body is { } replaced)
            {
                session.SetRequestBody(replaced);
            }
        }

        context.Items.Remove(_varsKey);
        await next(context, cancellationToken).ConfigureAwait(false);
    }

    private static bool Handled(ProxyMiddlewareContext context, LuaCallResult result)
    {
        if (result.Deny is not { } deny)
        {
            return false;
        }

        context.IsHandled = true;
        context.HandledStatusCode = deny.Status;
        context.HandledBody = string.Empty;
        context.HandledBodyBytes = deny.Body;
        if (deny.Headers is { Count: > 0 } headers)
        {
            context.HandledHeaders = new List<KeyValuePair<string, string>>(headers);
        }

        return true;
    }

    private static void Apply(Http.HeaderCollection headers, LuaCallResult result)
    {
        if (result.HeaderEdits is not { } edits)
        {
            return;
        }

        foreach (var edit in edits)
        {
            headers.RemoveHeader(edit.Key);
            if (edit.Value is not null)
            {
                headers.AddHeader(edit.Key, edit.Value);
            }
        }
    }

    private static KeyValuePair<string, string>[] Snapshot(Http.HeaderCollection headers)
    {
        var all = headers.GetAllHeaders();
        var copy = new KeyValuePair<string, string>[all.Count];
        for (var i = 0; i < copy.Length; i++)
        {
            copy[i] = new(all[i].Name, all[i].Value);
        }

        return copy;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            Runtime.Dispose();
        }
    }
}
