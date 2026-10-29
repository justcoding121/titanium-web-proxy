using Microsoft.Extensions.Logging;
using Titanium.Web.Proxy.Abstractions.Scripting;

namespace Titanium.Web.Proxy.Scripting.Lua;

/// <summary>
/// L4 phases for transparent passthrough listeners (<c>TransparentBaseProxyEndPoint.StreamFilters</c>):
/// <c>on_connect(ctx)</c> at accept (may deny or <c>choose_cluster</c>), <c>on_data(ctx)</c> for each piece with
/// <c>ctx.direction</c> <c>downstream</c> (from the client) or <c>upstream</c> and <c>ctx.data</c> (<c>set_data</c> replaces
/// it; an empty string drops it), and <c>on_close(ctx)</c>. <c>ctx.vars</c> lasts for the connection.
/// </summary>
internal sealed partial class LuaPlugin : IStreamScriptFilter
{
    public async ValueTask<StreamFilterResult> OnNewConnectionAsync(StreamConnectionContext context, CancellationToken cancellationToken)
    {
        if (!Runtime.Defines("on_connect"))
        {
            return StreamFilterResult.Continue;
        }

        var result = await RunStreamAsync("on_connect", context, null, null, cancellationToken).ConfigureAwait(false);
        if (result is null)
        {
            return Runtime.Options.FailOpen ? StreamFilterResult.Continue : StreamFilterResult.Deny;
        }

        if (result.Deny is not null)
        {
            return StreamFilterResult.Deny;
        }

        return result.ClusterId is { } cluster ? new StreamFilterResult { ClusterId = cluster } : StreamFilterResult.Continue;
    }

    public ValueTask<StreamFilterResult> OnDownstreamDataAsync(StreamConnectionContext context, ReadOnlyMemory<byte> data,
        CancellationToken cancellationToken) => DataAsync(context, "downstream", data, cancellationToken);

    public ValueTask<StreamFilterResult> OnUpstreamDataAsync(StreamConnectionContext context, ReadOnlyMemory<byte> data,
        CancellationToken cancellationToken) => DataAsync(context, "upstream", data, cancellationToken);

    public async ValueTask OnCloseAsync(StreamConnectionContext context)
    {
        if (Runtime.Defines("on_close"))
        {
            await RunStreamAsync("on_close", context, null, null, CancellationToken.None).ConfigureAwait(false);
        }

        context.Items.Remove(_varsKey);
    }

    private async ValueTask<StreamFilterResult> DataAsync(StreamConnectionContext context, string direction, ReadOnlyMemory<byte> data,
        CancellationToken cancellationToken)
    {
        if (!Runtime.Defines("on_data"))
        {
            return StreamFilterResult.Continue;
        }

        var result = await RunStreamAsync("on_data", context, direction, data, cancellationToken).ConfigureAwait(false);
        if (result is null)
        {
            return Runtime.Options.FailOpen ? StreamFilterResult.Continue : StreamFilterResult.Deny;
        }

        if (result.Deny is not null)
        {
            return StreamFilterResult.Deny;
        }

        return result.Body is { } replaced ? new StreamFilterResult { ReplaceData = replaced } : StreamFilterResult.Continue;
    }

    /// <summary>Runs one L4 function. Null means it failed (already logged).</summary>
    private async ValueTask<LuaCallResult?> RunStreamAsync(string function, StreamConnectionContext context, string? direction,
        ReadOnlyMemory<byte>? data, CancellationToken cancellationToken)
    {
        var w = new LuaWriter(256 + (data?.Length ?? 0));
        var mark = w.BeginTable();
        var pairs = 2;
        w.Field("phase", function[3..]);
        w.Field("plugin", Name);
        pairs += LuaSnapshot.EndPoint(w, "client", context.ClientEndPoint);
        pairs += LuaSnapshot.EndPoint(w, "local", context.LocalEndPoint);
        if (context.ServerName is not null)
        {
            w.Field("server_name", context.ServerName);
            pairs++;
        }

        if (context.ClusterId is not null)
        {
            w.Field("cluster_id", context.ClusterId);
            pairs++;
        }

        if (direction is not null)
        {
            w.Field("direction", direction);
            w.Field("data", data!.Value.Span);
            pairs += 2;
        }

        if (TakeVars(context.Items) is { } vars)
        {
            w.String("vars");
            w.Raw(vars);
            pairs++;
        }

        w.EndTable(mark, pairs);
        try
        {
            var result = await Runtime.CallAsync(function, w.ToArray(), cancellationToken).ConfigureAwait(false);
            if (result.Vars is not null)
            {
                context.Items[_varsKey] = result.Vars;
            }

            return result;
        }
        catch (LuaScriptException ex)
        {
            Logger?.LogWarning("Lua plugin '{Plugin}' {Function} failed ({Reason}): {Error}", Name, function,
                LuaScriptException.Reason(ex.Failure), ex.Message);
            return null;
        }
    }
}
