using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Titanium.Web.Proxy.Abstractions.Middleware;
using Titanium.Web.Proxy.Abstractions.Scripting;
using Titanium.Web.Proxy.EventArguments;

namespace Titanium.Web.Proxy.Scripting.Wasm;

/// <summary>
/// One configured <c>kind: wasm</c> plugin. As a route plugin (<see cref="IProxyMiddleware"/>) it runs the request-headers
/// phase; set as <c>ReverseProxyOptions.ScriptPipeline</c> (<see cref="IScriptPhaseHost"/>) it runs every HTTP phase; set
/// on a passthrough listener's <c>StreamFilters</c> (<see cref="IStreamScriptFilter"/>) it runs as a proxy-wasm network
/// filter. Each request or connection gets its own context in one VM for its whole life.
/// </summary>
internal sealed class WasmPlugin : IProxyMiddleware, IScriptPhaseHost, IStreamScriptFilter, IRequiresSessionPathMiddleware, IDisposable
{
    /// <summary>How often (in new contexts) a VM is swept for contexts whose request never reached the log phase.</summary>
    private const int SweepEvery = 1024;

    private static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(10);

    private readonly string _itemKey;
    private int _disposed;

    internal WasmPlugin( // NOSONAR S107 -- Plugin wiring keeps VM limits and config explicit without a context bag.
        string name, string? vmId, WasmVmPool pool, WasmLimits limits, ScriptBodyMode bodyMode, bool failOpen,
        byte[] configuration, byte[] vmConfiguration, ILogger? logger, IReadOnlyList<string>? calloutHosts = null)
    {
        Name = name;
        VmId = vmId;
        CalloutHosts = calloutHosts ?? [];
        Pool = pool;
        Limits = limits;
        BodyMode = bodyMode;
        FailOpen = failOpen;
        Configuration = configuration;
        VmConfiguration = vmConfiguration;
        Logger = logger;
        _itemKey = "twp.wasm:" + name;
        Phases = PhasesOf(pool.Module);
    }

    public string Name { get; }

    public string? VmId { get; }

    /// <summary>Hosts this plugin's callouts may reach (<c>calloutHosts</c>). Empty allows none.</summary>
    public IReadOnlyList<string> CalloutHosts { get; }

    public WasmVmPool Pool { get; }

    public WasmLimits Limits { get; }

    public bool FailOpen { get; }

    public byte[] Configuration { get; }

    public byte[] VmConfiguration { get; }

    public ILogger? Logger { get; }

    /// <summary>Root context id of this plugin; the same in every VM of the pool.</summary>
    public int RootId { get; set; }

    /// <summary>The <c>plugin_root_id</c> property: the plugin name.</summary>
    public string RootIdName => Name;

    public ScriptPhases Phases { get; }

    public ScriptBodyMode BodyMode { get; }

    // IScriptPhaseHost.

    public ValueTask<ScriptPhaseResult> OnRequestHeadersAsync(ScriptPhaseContext context, CancellationToken cancellationToken) =>
        new(Run(context, ScriptPhases.RequestHeaders));

    public ValueTask<ScriptPhaseResult> OnRequestBodyAsync(ScriptPhaseContext context, CancellationToken cancellationToken) =>
        new(Run(context, ScriptPhases.RequestBody));

    public ValueTask<ScriptPhaseResult> OnRequestTrailersAsync(ScriptPhaseContext context, CancellationToken cancellationToken) =>
        new(Run(context, ScriptPhases.RequestTrailers));

    public ValueTask<ScriptPhaseResult> OnResponseHeadersAsync(ScriptPhaseContext context, CancellationToken cancellationToken) =>
        new(Run(context, ScriptPhases.ResponseHeaders));

    public ValueTask<ScriptPhaseResult> OnResponseBodyAsync(ScriptPhaseContext context, CancellationToken cancellationToken) =>
        new(Run(context, ScriptPhases.ResponseBody));

    public ValueTask<ScriptPhaseResult> OnResponseTrailersAsync(ScriptPhaseContext context, CancellationToken cancellationToken) =>
        new(Run(context, ScriptPhases.ResponseTrailers));

    public async ValueTask<ScriptPhaseResult> OnResumeAsync(ScriptPhaseContext context, CancellationToken cancellationToken)
    {
        if (Find(context) is not { } http)
        {
            return ScriptPhaseResult.Continue;
        }

        // Waiting happens with no lock held; the guest's callbacks (callout responses, timers) take the gate themselves.
        var resumed = await http.WaitForResumeAsync(TimeSpan.FromMilliseconds(Limits.PauseTimeoutMs), cancellationToken).ConfigureAwait(false);
        var vm = http.Vm;
        lock (vm.Gate)
        {
            if (vm.Faulted || vm.IsDisposed)
            {
                Forget(context);
                return Fail(vm.FaultReason ?? "the VM was discarded", null);
            }

            http.Last = context;
            return http.Resolve(resumed);
        }
    }

    public ValueTask OnLogAsync(ScriptPhaseContext context, CancellationToken cancellationToken)
    {
        if (Find(context) is { } http)
        {
            Forget(context);
            Finish(http, context, log: true);
        }

        return default;
    }

    // IStreamScriptFilter: proxy-wasm stream contexts on passthrough connections.

    public async ValueTask<StreamFilterResult> OnNewConnectionAsync(StreamConnectionContext context, CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        try
        {
            var vm = Pool.Acquire();
            var stream = new WasmStreamContext(vm, Pool.NextContextId(), RootId, this, context);
            StreamFilterResult? result;
            lock (vm.Gate)
            {
                vm.Register(stream);
                vm.Effective = stream;
                vm.Call("proxy_on_context_create", [stream.Id, RootId]);
                context.Items[_itemKey] = stream;
                result = stream.Finish(vm.Call("proxy_on_new_connection", [stream.Id], ProxyAction.Continue), isData: false);
            }

            return result ?? await ResumeStreamAsync(stream, isData: false, cancellationToken).ConfigureAwait(false);
        }
        catch (WasmVmFaultException ex)
        {
            ForgetStream(context);
            return FailStream(ex.Message, ex);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            ForgetStream(context);
            return FailStream("host failure", ex);
        }
        finally
        {
            WasmPluginMetrics.RecordCall(Name, "NewConnection", Stopwatch.GetElapsedTime(started).TotalSeconds);
        }
    }

    public ValueTask<StreamFilterResult> OnDownstreamDataAsync(StreamConnectionContext context, ReadOnlyMemory<byte> data,
        CancellationToken cancellationToken) => StreamDataAsync(context, data, ProxyBufferType.DownstreamData, "proxy_on_downstream_data", cancellationToken);

    public ValueTask<StreamFilterResult> OnUpstreamDataAsync(StreamConnectionContext context, ReadOnlyMemory<byte> data,
        CancellationToken cancellationToken) => StreamDataAsync(context, data, ProxyBufferType.UpstreamData, "proxy_on_upstream_data", cancellationToken);

    /// <summary>Ends the stream context: both connection-close callbacks, then <c>on_log</c>, <c>on_done</c> and <c>on_delete</c>.</summary>
    public ValueTask OnCloseAsync(StreamConnectionContext context)
    {
        if (FindStream(context) is not { } stream)
        {
            return default;
        }

        ForgetStream(context);
        var vm = stream.Vm;
        lock (vm.Gate)
        {
            try
            {
                if (vm.Faulted || vm.IsDisposed)
                {
                    return default;
                }

                vm.Effective = stream;
                vm.Call("proxy_on_downstream_connection_close", [stream.Id, 0]);
                vm.Call("proxy_on_upstream_connection_close", [stream.Id, 0]);
                vm.Call("proxy_on_log", [stream.Id]);
                vm.Call("proxy_on_done", [stream.Id], 1);
                vm.Call("proxy_on_delete", [stream.Id]);
            }
            catch (WasmVmFaultException ex)
            {
                if (Logger?.IsEnabled(LogLevel.Debug) == true)
                {
                    Logger.LogDebug(ex, "WASM plugin '{Plugin}' failed while it closed a connection: {Reason}", Name, ex.Message);
                }
            }
            finally
            {
                vm.Unregister(stream.Id);
                vm.Effective = null;
            }
        }

        return default;
    }

    private async ValueTask<StreamFilterResult> StreamDataAsync(StreamConnectionContext context, ReadOnlyMemory<byte> data,
        ProxyBufferType direction, string export, CancellationToken cancellationToken)
    {
        if (FindStream(context) is not { } stream)
        {
            // The connection's context was dropped after a fault, which already answered by failMode.
            return FailOpen ? StreamFilterResult.Continue : StreamFilterResult.Deny;
        }

        var started = Stopwatch.GetTimestamp();
        try
        {
            var vm = stream.Vm;
            StreamFilterResult? result;
            lock (vm.Gate)
            {
                vm.Effective = stream;
                if (!stream.BeginData(direction, data))
                {
                    Logger?.LogWarning("WASM plugin '{Plugin}' closed a connection that held more than {Limit} bytes.", Name, Limits.BufferedBodyBytes);
                    return StreamFilterResult.Deny;
                }

                result = stream.Finish(vm.Call(export, [stream.Id, stream.DataLength, 0]), isData: true);
                if (result is not null)
                {
                    stream.EndData();
                }
            }

            return result ?? await ResumeStreamAsync(stream, isData: true, cancellationToken).ConfigureAwait(false);
        }
        catch (WasmVmFaultException ex)
        {
            ForgetStream(context);
            return FailStream(ex.Message, ex);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            ForgetStream(context);
            return FailStream("host failure", ex);
        }
        finally
        {
            WasmPluginMetrics.RecordCall(Name, "StreamData", Stopwatch.GetElapsedTime(started).TotalSeconds);
        }
    }

    /// <summary>Waits, with no lock held, for a paused stream to be continued or closed, then answers.</summary>
    private async ValueTask<StreamFilterResult> ResumeStreamAsync(WasmStreamContext stream, bool isData, CancellationToken cancellationToken)
    {
        var resumed = await stream.WaitForResumeAsync(TimeSpan.FromMilliseconds(Limits.PauseTimeoutMs), cancellationToken).ConfigureAwait(false);
        var vm = stream.Vm;
        lock (vm.Gate)
        {
            if (vm.Faulted || vm.IsDisposed)
            {
                ForgetStream(stream.Connection);
                return FailStream(vm.FaultReason ?? "the VM was discarded", null);
            }

            var result = stream.Resolve(resumed, isData);
            stream.EndData();
            return result;
        }
    }

    private WasmStreamContext? FindStream(StreamConnectionContext context) =>
        context.Items.TryGetValue(_itemKey, out var value) ? value as WasmStreamContext : null;

    private void ForgetStream(StreamConnectionContext context) => context.Items.Remove(_itemKey);

    private StreamFilterResult FailStream(string reason, Exception? ex)
    {
        WasmPluginMetrics.RecordError(Name, "fault");
        Logger?.LogWarning(ex is WasmVmFaultException ? null : ex,
            "WASM plugin '{Plugin}' failed on a connection: {Reason}; failMode is {FailMode}.", Name, reason, FailOpen ? "open" : "closed");
        return FailOpen ? StreamFilterResult.Continue : StreamFilterResult.Deny;
    }

    // IProxyMiddleware: the request-headers phase only, applied straight to the session.

    public async ValueTask InvokeAsync(ProxyMiddlewareContext context, ProxyMiddlewareDelegate next, CancellationToken cancellationToken)
    {
        if (context.Session is not SessionEventArgs session)
        {
            await next(context, cancellationToken).ConfigureAwait(false);
            return;
        }

        var request = session.HttpClient.Request;
        var headers = request.Headers.GetAllHeaders();
        var snapshot = new KeyValuePair<string, string>[headers.Count];
        for (var i = 0; i < snapshot.Length; i++)
        {
            snapshot[i] = new(headers[i].Name, headers[i].Value);
        }

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
            Headers = snapshot,
            Items = new Dictionary<string, object?>(),
        };

        ScriptPhaseResult result;
        try
        {
            result = await OnRequestHeadersAsync(phase, cancellationToken).ConfigureAwait(false);
            for (var resumes = 0; resumes < 64 && result.Action == ScriptAction.Pause; resumes++)
            {
                phase.ResumeCount = resumes + 1;
                result = await OnResumeAsync(phase, cancellationToken).ConfigureAwait(false);
            }

            if (result.Action == ScriptAction.Pause)
            {
                result = ScriptPhaseResult.Deny(504, "WASM plugin did not finish.");
            }
        }
        finally
        {
            if (Find(phase) is { } http)
            {
                Forget(phase);
                Finish(http, phase, log: false);
            }
        }

        if (result.Action == ScriptAction.Deny)
        {
            var local = result.LocalResponse;
            context.IsHandled = true;
            context.HandledStatusCode = local?.Status ?? 403;
            context.HandledBody = string.Empty;
            context.HandledBodyBytes = local?.Body;
            if (local?.Headers is { Count: > 0 } extra)
            {
                context.HandledHeaders = new List<KeyValuePair<string, string>>(extra);
            }

            return;
        }

        if (result.RemoveHeaders is { } remove)
        {
            foreach (var name in remove)
            {
                request.Headers.RemoveHeader(name);
            }
        }

        if (result.SetHeaders is { } set)
        {
            foreach (var pair in set)
            {
                request.Headers.RemoveHeader(pair.Key);
                request.Headers.AddHeader(pair.Key, pair.Value);
            }
        }

        await next(context, cancellationToken).ConfigureAwait(false);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        if (Pool.Detach(this))
        {
            Pool.Dispose();
        }
    }

    private ScriptPhaseResult Run(ScriptPhaseContext context, ScriptPhases phase)
    {
        WasmHttpContext? http = null;
        var started = Stopwatch.GetTimestamp();
        try
        {
            http = Find(context) ?? Create(context);
            var vm = http.Vm;
            lock (vm.Gate)
            {
                vm.Effective = http;
                http.Last = context;
                var action = phase switch
                {
                    ScriptPhases.RequestHeaders => Headers(http, phase, RequestMap(context), "proxy_on_request_headers", EndOfStream(context)),
                    ScriptPhases.ResponseHeaders => Headers(http, phase, ResponseMap(context), "proxy_on_response_headers", EndOfStream(context)),
                    ScriptPhases.RequestTrailers => Trailers(http, phase, context, "proxy_on_request_trailers"),
                    ScriptPhases.ResponseTrailers => Trailers(http, phase, context, "proxy_on_response_trailers"),
                    ScriptPhases.RequestBody => Body(http, phase, context, "proxy_on_request_body"),
                    _ => Body(http, phase, context, "proxy_on_response_body"),
                };

                if (action == TooLarge)
                {
                    return ScriptPhaseResult.Deny(413, "Body exceeds the WASM plugin's buffered-body limit.");
                }

                return http.Finish(action);
            }
        }
        catch (WasmVmFaultException ex)
        {
            Forget(context);
            return Fail(ex.Message, ex);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Forget(context);
            return Fail("host failure", ex);
        }
        finally
        {
            WasmPluginMetrics.RecordCall(Name, phase, Stopwatch.GetElapsedTime(started).TotalSeconds);
        }
    }

    private const int TooLarge = -1;

    private static int Headers(WasmHttpContext http, ScriptPhases phase, WasmHeaderMap map, string export, bool endOfStream)
    {
        http.BeginHeaders(phase, map);
        return http.Vm.Call(export, [http.Id, map.Count, endOfStream ? 1 : 0]);
    }

    private static int Trailers(WasmHttpContext http, ScriptPhases phase, ScriptPhaseContext context, string export)
    {
        var map = new WasmHeaderMap(context.Headers);
        http.BeginHeaders(phase, map);
        return http.Vm.Call(export, [http.Id, map.Count]);
    }

    private static int Body(WasmHttpContext http, ScriptPhases phase, ScriptPhaseContext context, string export)
    {
        if (!http.BeginBody(phase, context.Chunk, context.IsLastChunk))
        {
            return TooLarge;
        }

        return http.Vm.Call(export, [http.Id, http.BodyLength, context.IsLastChunk ? 1 : 0]);
    }

    private WasmHttpContext Create(ScriptPhaseContext context)
    {
        var vm = Pool.Acquire();
        var id = Pool.NextContextId();
        var http = new WasmHttpContext(vm, id, RootId, this);
        lock (vm.Gate)
        {
            if (id % SweepEvery == 0)
            {
                Sweep(vm);
            }

            vm.Register(http);
            vm.Effective = http;
            vm.Call("proxy_on_context_create", [id, RootId]);
        }

        context.Items[_itemKey] = http;
        return http;
    }

    private WasmHttpContext? Find(ScriptPhaseContext context) =>
        context.Items.TryGetValue(_itemKey, out var value) ? value as WasmHttpContext : null;

    private void Forget(ScriptPhaseContext context) => context.Items.Remove(_itemKey);

    /// <summary>Ends a context: <c>on_log</c> (when the request finished), then <c>on_done</c> and <c>on_delete</c>.</summary>
    private void Finish(WasmHttpContext http, ScriptPhaseContext? context, bool log)
    {
        var vm = http.Vm;
        lock (vm.Gate)
        {
            try
            {
                if (vm.Faulted || vm.IsDisposed)
                {
                    return;
                }

                vm.Effective = http;
                if (context is not null)
                {
                    http.Last = context;
                }

                if (log)
                {
                    vm.Call("proxy_on_log", [http.Id]);
                }

                vm.Call("proxy_on_done", [http.Id], 1);
                vm.Call("proxy_on_delete", [http.Id]);
            }
            catch (WasmVmFaultException ex)
            {
                if (Logger?.IsEnabled(LogLevel.Debug) == true)
                {
                    Logger.LogDebug(ex, "WASM plugin '{Plugin}' failed while it closed a request: {Reason}", Name, ex.Message);
                }
            }
            finally
            {
                vm.Unregister(http.Id);
                if (ReferenceEquals(vm.Effective, http))
                {
                    vm.Effective = null;
                }
            }
        }
    }

    /// <summary>Ends contexts whose request never reached the log phase (an aborted connection), so a VM does not leak them.</summary>
    private static void Sweep(WasmVm vm)
    {
        var cutoff = Environment.TickCount64 - (long)StaleAfter.TotalMilliseconds;
        foreach (var stale in vm.Contexts.OfType<WasmHttpContext>().Where(c => c.CreatedTicks < cutoff && !c.Paused).ToArray())
        {
            stale.Plugin.Finish(stale, null, log: false);
        }
    }

    private ScriptPhaseResult Fail(string reason, Exception? ex)
    {
        WasmPluginMetrics.RecordError(Name, "fault");
        Logger?.LogWarning(ex is WasmVmFaultException ? null : ex,
            "WASM plugin '{Plugin}' failed: {Reason}; failMode is {FailMode}.", Name, reason, FailOpen ? "open" : "closed");
        return FailOpen ? ScriptPhaseResult.Continue : ScriptPhaseResult.Deny(503);
    }

    /// <summary>The proxy-wasm request map: pseudo-headers first, then the headers, with <c>host</c> carried as <c>:authority</c>.</summary>
    private static WasmHeaderMap RequestMap(ScriptPhaseContext context)
    {
        var pairs = new List<KeyValuePair<string, string>>(context.Headers.Count + 4)
        {
            new(":method", context.Method),
            new(":path", context.Path),
            new(":authority", context.Host),
            new(":scheme", context.IsHttps ? "https" : "http"),
        };
        pairs.AddRange(context.Headers.Where(h => !string.Equals(h.Key, "host", StringComparison.OrdinalIgnoreCase)));

        return new WasmHeaderMap(pairs);
    }

    private static WasmHeaderMap ResponseMap(ScriptPhaseContext context)
    {
        var pairs = new List<KeyValuePair<string, string>>(context.Headers.Count + 1)
        {
            new(":status", context.StatusCode.ToString(System.Globalization.CultureInfo.InvariantCulture)),
        };
        pairs.AddRange(context.Headers);
        return new WasmHeaderMap(pairs);
    }

    /// <summary>True when the headers say no body follows: no content-length above zero and no transfer-encoding.</summary>
    private static bool EndOfStream(ScriptPhaseContext context)
    {
        if (context.Phase == ScriptPhases.ResponseHeaders && context.StatusCode is 204 or 304 || string.Equals(context.Method, "HEAD", StringComparison.OrdinalIgnoreCase) && context.Phase == ScriptPhases.ResponseHeaders)
        {
            return true;
        }

        foreach (var header in context.Headers)
        {
            if (string.Equals(header.Key, "transfer-encoding", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (string.Equals(header.Key, "content-length", StringComparison.OrdinalIgnoreCase))
            {
                return header.Value.Trim() == "0";
            }
        }

        // A response with neither header is read to close; a request with neither has no body.
        return context.Phase == ScriptPhases.RequestHeaders;
    }

    private static ScriptPhases PhasesOf(WasmModule module)
    {
        var phases = ScriptPhases.Log;
        if (module.HasExport("proxy_on_request_headers")) phases |= ScriptPhases.RequestHeaders;
        if (module.HasExport("proxy_on_request_body")) phases |= ScriptPhases.RequestBody;
        if (module.HasExport("proxy_on_request_trailers")) phases |= ScriptPhases.RequestTrailers;
        if (module.HasExport("proxy_on_response_headers")) phases |= ScriptPhases.ResponseHeaders;
        if (module.HasExport("proxy_on_response_body")) phases |= ScriptPhases.ResponseBody;
        if (module.HasExport("proxy_on_response_trailers")) phases |= ScriptPhases.ResponseTrailers;
        return phases;
    }
}
