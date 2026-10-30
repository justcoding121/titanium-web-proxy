using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Titanium.Web.Proxy.Abstractions.Middleware;
using Titanium.Web.Proxy.Diagnostics;

namespace Titanium.Web.Proxy.Plugins;

/// <summary>
/// Runs one plugin behind a wall. The plugin sees a private copy of the request context, so a plugin that overruns its
/// timeout cannot change the request after the host moved on. An exception or a timeout is handled by the plugin's
/// <c>failMode</c>: closed answers 503, open lets the request continue as if the plugin had called <c>next</c>.
/// </summary>
/// <remarks>
/// The plugin's <c>next</c> only records that the plugin wants the request to continue; the host calls the real
/// <c>next</c> after the plugin returns. That keeps the timeout to the plugin's own work, and it means code a plugin runs
/// after awaiting <c>next</c> runs before the following plugin does.
/// <para>
/// <b>Known limit: the wall cannot preempt a plugin that never yields.</b> The timeout is cooperative. The plugin is
/// invoked on the request's own thread, and the wall only starts waiting once <c>InvokeAsync</c> has returned a task.
/// A plugin that spins, blocks on a lock, or calls blocking I/O before its first real <c>await</c> never returns that
/// task, so it holds the request thread for as long as it runs. <c>timeoutMs</c> and <c>failMode</c> do not apply to it,
/// and <c>failMode: open</c> does not help. Only a plugin that awaits (and honours the cancellation token) is stopped by
/// the timeout. Plugins are in-process, trusted code; a sync spin is an operator risk, not something the host contains.
/// Moving the call to the thread pool would let the wall time out, but the spinning thread would still burn a pool thread
/// and CPU until it ends, so the host does not do that. Engines that can enforce limits themselves (a future Lua or WASM
/// host) will not have this limit.
/// </para>
/// </remarks>
internal sealed class RoutePluginMiddleware : IProxyMiddleware
{
    private readonly string _name;
    private readonly bool _failOpen;
    private readonly TimeSpan _timeout;
    private readonly PluginHost _host;
    private readonly ILogger? _logger;
    private IProxyMiddleware? _inner;

    internal RoutePluginMiddleware(
        string name, IProxyMiddleware inner, bool failOpen, TimeSpan timeout, PluginHost host, ILogger? logger)
    {
        _name = name;
        _inner = inner;
        _failOpen = failOpen;
        _timeout = timeout;
        _host = host;
        _logger = logger;
    }

    /// <summary>Drops the plugin reference so a disposed host keeps nothing alive in the unloaded context.</summary>
    internal void Detach() => Volatile.Write(ref _inner, null);

    public async ValueTask InvokeAsync(
        ProxyMiddlewareContext context, ProxyMiddlewareDelegate next, CancellationToken cancellationToken)
    {
        var inner = Volatile.Read(ref _inner);
        if (inner is null || !_host.TryEnter())
        {
            ProxyMetrics.RecordPluginError(_name, "unloaded");
            await FailAsync(context, next, cancellationToken).ConfigureAwait(false);
            return;
        }

        var started = Stopwatch.GetTimestamp();
        var gate = new NextGate();
        var plugin = Clone(context);
        string? failure = null;
        Exception? error = null;
        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            linked.CancelAfter(_timeout);
            Task run;
            try
            {
                run = inner.InvokeAsync(plugin, gate.Invoke, linked.Token).AsTask();
            }
            catch (Exception ex)
            {
                run = Task.FromException(ex);
            }

            try
            {
                await run.WaitAsync(_timeout, cancellationToken).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                failure = "timeout";
                gate.Close();
                await linked.CancelAsync().ConfigureAwait(false);
                // The abandoned call may still fail later; observe it so it never surfaces as an unobserved exception.
                _ = run.ContinueWith(
                    static t => { _ = t.Exception; },
                    CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted,
                    TaskScheduler.Default);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                gate.Close();
                throw;
            }
            catch (OperationCanceledException ex) when (linked.IsCancellationRequested)
            {
                // Only the timeout cancels the linked token while the request is still alive.
                failure = "timeout";
                error = ex;
                gate.Close();
            }
            catch (Exception ex)
            {
                failure = "exception";
                error = ex;
                gate.Close();
            }
        }
        finally
        {
            ProxyMetrics.RecordPluginCall(_name, Stopwatch.GetElapsedTime(started).TotalSeconds);
            _host.Exit();
        }

        if (failure is not null)
        {
            ProxyMetrics.RecordPluginError(_name, failure);
            _logger?.LogWarning(error, "Plugin '{Plugin}' {Failure}; failMode is {FailMode}.",
                _name, failure == "timeout" ? "timed out" : "threw", _failOpen ? "open" : "closed");
            await FailAsync(context, next, cancellationToken).ConfigureAwait(false);
            return;
        }

        Merge(plugin, context);
        if (gate.Called && !context.IsHandled)
        {
            await next(context, cancellationToken).ConfigureAwait(false);
        }
    }

    private ValueTask FailAsync(ProxyMiddlewareContext context, ProxyMiddlewareDelegate next, CancellationToken cancellationToken)
    {
        if (_failOpen)
        {
            return next(context, cancellationToken);
        }

        context.HandledStatusCode = 503;
        context.HandledBody = string.Empty;
        context.IsHandled = true;
        return ValueTask.CompletedTask;
    }

    private static ProxyMiddlewareContext Clone(ProxyMiddlewareContext source)
    {
        var copy = new ProxyMiddlewareContext
        {
            Session = source.Session,
            IsHandled = source.IsHandled,
            ClientRemoteEndPoint = source.ClientRemoteEndPoint,
            Request = source.Request,
            HandledStatusCode = source.HandledStatusCode,
            HandledBody = source.HandledBody,
            HandledBodyBytes = source.HandledBodyBytes,
            HandledIsCacheHit = source.HandledIsCacheHit,
            HandledHeaders = source.HandledHeaders is null ? null : new List<KeyValuePair<string, string>>(source.HandledHeaders),
        };
        foreach (var item in source.Items)
        {
            copy.Items[item.Key] = item.Value;
        }

        return copy;
    }

    private static void Merge(ProxyMiddlewareContext plugin, ProxyMiddlewareContext target)
    {
        target.IsHandled = plugin.IsHandled;
        target.HandledStatusCode = plugin.HandledStatusCode;
        target.HandledBody = plugin.HandledBody;
        target.HandledBodyBytes = plugin.HandledBodyBytes;
        target.HandledIsCacheHit = plugin.HandledIsCacheHit;
        target.HandledHeaders = plugin.HandledHeaders;
        target.Items.Clear();
        foreach (var item in plugin.Items)
        {
            target.Items[item.Key] = item.Value;
        }
    }

    /// <summary>What the plugin did with <c>next</c>. Closed once the host stops waiting for the plugin.</summary>
    private sealed class NextGate
    {
        private int _called;
        private int _closed;

        public bool Called => Volatile.Read(ref _called) == 1 && Volatile.Read(ref _closed) == 0;

        public void Close() => Volatile.Write(ref _closed, 1);

        public ValueTask Invoke(ProxyMiddlewareContext context, CancellationToken cancellationToken) // NOSONAR S1172 -- Stub middleware only tracks call order; parameters match the delegate shape.
        {
            _ = context;
            _ = cancellationToken;
            if (Volatile.Read(ref _closed) == 0)
            {
                Volatile.Write(ref _called, 1);
            }

            return ValueTask.CompletedTask;
        }
    }
}
