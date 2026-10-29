using System;
using System.Threading;
using System.Threading.Tasks;
using Titanium.Web.Proxy.Abstractions.Scripting;

namespace Titanium.Web.Proxy.Plugins;

/// <summary>
/// A script plugin seen as one <see cref="IScriptPhaseHost"/> of the listener-wide pipeline. Every call is counted against
/// the <see cref="PluginHost"/> that owns the plugin, so a reload retires that host only after the calls in flight finish.
/// A call that arrives after the host was disposed is refused (or let through, with <c>failMode: open</c>), never run on a
/// disposed script state.
/// </summary>
internal sealed class LeasedScriptHost : IScriptPhaseHost
{
    private readonly PluginHost _host;
    private readonly IScriptPhaseHost _inner;
    private readonly bool _failOpen;

    internal LeasedScriptHost(PluginHost host, IScriptPhaseHost inner, bool failOpen)
    {
        _host = host;
        _inner = inner;
        _failOpen = failOpen;
    }

    public ScriptPhases Phases => _inner.Phases;

    public ScriptBodyMode BodyMode => _inner.BodyMode;

    public ValueTask<ScriptPhaseResult> OnRequestHeadersAsync(ScriptPhaseContext context, CancellationToken cancellationToken) =>
        RunAsync(static (host, c, t) => host.OnRequestHeadersAsync(c, t), context, cancellationToken);

    public ValueTask<ScriptPhaseResult> OnRequestBodyAsync(ScriptPhaseContext context, CancellationToken cancellationToken) =>
        RunAsync(static (host, c, t) => host.OnRequestBodyAsync(c, t), context, cancellationToken);

    public ValueTask<ScriptPhaseResult> OnRequestTrailersAsync(ScriptPhaseContext context, CancellationToken cancellationToken) =>
        RunAsync(static (host, c, t) => host.OnRequestTrailersAsync(c, t), context, cancellationToken);

    public ValueTask<ScriptPhaseResult> OnResponseHeadersAsync(ScriptPhaseContext context, CancellationToken cancellationToken) =>
        RunAsync(static (host, c, t) => host.OnResponseHeadersAsync(c, t), context, cancellationToken);

    public ValueTask<ScriptPhaseResult> OnResponseBodyAsync(ScriptPhaseContext context, CancellationToken cancellationToken) =>
        RunAsync(static (host, c, t) => host.OnResponseBodyAsync(c, t), context, cancellationToken);

    public ValueTask<ScriptPhaseResult> OnResponseTrailersAsync(ScriptPhaseContext context, CancellationToken cancellationToken) =>
        RunAsync(static (host, c, t) => host.OnResponseTrailersAsync(c, t), context, cancellationToken);

    public ValueTask<ScriptPhaseResult> OnResumeAsync(ScriptPhaseContext context, CancellationToken cancellationToken) =>
        RunAsync(static (host, c, t) => host.OnResumeAsync(c, t), context, cancellationToken);

    public async ValueTask OnLogAsync(ScriptPhaseContext context, CancellationToken cancellationToken)
    {
        if (!_host.TryEnter())
        {
            return;
        }

        try
        {
            await _inner.OnLogAsync(context, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _host.Exit();
        }
    }

    private async ValueTask<ScriptPhaseResult> RunAsync(
        Func<IScriptPhaseHost, ScriptPhaseContext, CancellationToken, ValueTask<ScriptPhaseResult>> call,
        ScriptPhaseContext context,
        CancellationToken cancellationToken)
    {
        if (!_host.TryEnter())
        {
            return _failOpen ? ScriptPhaseResult.Continue : ScriptPhaseResult.Deny(503, "Script unavailable.");
        }

        try
        {
            return await call(_inner, context, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _host.Exit();
        }
    }
}

/// <summary>
/// A script plugin seen as one <see cref="IStreamScriptFilter"/> of a passthrough listener. A listener keeps its filters for
/// the life of the process, so this one asks <paramref name="current"/> for the plugin of the configuration that is in force
/// when a connection arrives, and pins that plugin to the connection: every later event of the connection goes to the same
/// plugin, as its per-connection state lives there. Calls are counted against the owning <see cref="PluginHost"/>.
/// </summary>
public sealed class ReloadableStreamFilter : IStreamScriptFilter
{
    private readonly string _itemKey;
    private readonly Func<PluginHost?> _current;
    private readonly string _name;

    public ReloadableStreamFilter(string name, Func<PluginHost?> current)
    {
        _name = name;
        _current = current;
        _itemKey = "twp.streamfilter.pin:" + name;
    }

    public async ValueTask<StreamFilterResult> OnNewConnectionAsync(StreamConnectionContext context, CancellationToken cancellationToken)
    {
        var pinned = _current()?.OpenStreamFilter(_name);
        if (pinned is null)
        {
            // The listener names this script, and the plugin set in force no longer has it. A connection that skipped the
            // filter would bypass what the operator put there, so it is refused.
            return StreamFilterResult.Deny;
        }

        context.Items[_itemKey] = pinned;
        return await pinned.OnNewConnectionAsync(context, cancellationToken).ConfigureAwait(false);
    }

    public ValueTask<StreamFilterResult> OnDownstreamDataAsync(
        StreamConnectionContext context, ReadOnlyMemory<byte> data, CancellationToken cancellationToken) =>
        Pinned(context) is { } filter
            ? filter.OnDownstreamDataAsync(context, data, cancellationToken)
            : new ValueTask<StreamFilterResult>(StreamFilterResult.Continue);

    public ValueTask<StreamFilterResult> OnUpstreamDataAsync(
        StreamConnectionContext context, ReadOnlyMemory<byte> data, CancellationToken cancellationToken) =>
        Pinned(context) is { } filter
            ? filter.OnUpstreamDataAsync(context, data, cancellationToken)
            : new ValueTask<StreamFilterResult>(StreamFilterResult.Continue);

    public ValueTask OnCloseAsync(StreamConnectionContext context)
    {
        if (Pinned(context) is not { } filter)
        {
            return default;
        }

        context.Items.Remove(_itemKey);
        return filter.OnCloseAsync(context);
    }

    private IStreamScriptFilter? Pinned(StreamConnectionContext context) =>
        context.Items.TryGetValue(_itemKey, out var value) ? value as IStreamScriptFilter : null;
}

/// <summary>One plugin's <see cref="IStreamScriptFilter"/> with every call counted against the <see cref="PluginHost"/> that owns it.</summary>
internal sealed class LeasedStreamFilter : IStreamScriptFilter
{
    private readonly PluginHost _host;
    private readonly IStreamScriptFilter _inner;
    private readonly bool _failOpen;

    internal LeasedStreamFilter(PluginHost host, IStreamScriptFilter inner, bool failOpen)
    {
        _host = host;
        _inner = inner;
        _failOpen = failOpen;
    }

    public ValueTask<StreamFilterResult> OnNewConnectionAsync(StreamConnectionContext context, CancellationToken cancellationToken) =>
        RunAsync(static (filter, c, _, t) => filter.OnNewConnectionAsync(c, t), context, default, _failOpen ? StreamFilterResult.Continue : StreamFilterResult.Deny, cancellationToken);

    public ValueTask<StreamFilterResult> OnDownstreamDataAsync(
        StreamConnectionContext context, ReadOnlyMemory<byte> data, CancellationToken cancellationToken) =>
        RunAsync(static (filter, c, d, t) => filter.OnDownstreamDataAsync(c, d, t), context, data, StreamFilterResult.Continue, cancellationToken);

    public ValueTask<StreamFilterResult> OnUpstreamDataAsync(
        StreamConnectionContext context, ReadOnlyMemory<byte> data, CancellationToken cancellationToken) =>
        RunAsync(static (filter, c, d, t) => filter.OnUpstreamDataAsync(c, d, t), context, data, StreamFilterResult.Continue, cancellationToken);

    public async ValueTask OnCloseAsync(StreamConnectionContext context)
    {
        if (!_host.TryEnter())
        {
            return;
        }

        try
        {
            await _inner.OnCloseAsync(context).ConfigureAwait(false);
        }
        finally
        {
            _host.Exit();
        }
    }

    private async ValueTask<StreamFilterResult> RunAsync(
        Func<IStreamScriptFilter, StreamConnectionContext, ReadOnlyMemory<byte>, CancellationToken, ValueTask<StreamFilterResult>> call,
        StreamConnectionContext context,
        ReadOnlyMemory<byte> data,
        StreamFilterResult whenGone,
        CancellationToken cancellationToken)
    {
        // Only the admission of a connection is refused. A connection that was already let in keeps flowing when its plugin
        // set was replaced by a reload: the script state it needed went with the old host.
        if (!_host.TryEnter())
        {
            return whenGone;
        }

        try
        {
            return await call(_inner, context, data, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _host.Exit();
        }
    }
}
