using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Titanium.Web.Proxy.Abstractions.Scripting;

namespace Titanium.Web.Proxy.Network;

/// <summary>
/// The L4 script filters of one passthrough connection, run in list order. Built at accept only when the listener has
/// filters, so a listener without them never allocates one. A filter that throws denies the connection: a broken script
/// never leaves traffic unfiltered.
/// </summary>
internal sealed class StreamFilterChain
{
    private readonly IReadOnlyList<IStreamScriptFilter> filters;
    private readonly StreamConnectionContext context;
    private int closed;

    internal StreamFilterChain(IReadOnlyList<IStreamScriptFilter> filters, StreamConnectionContext context)
    {
        this.filters = filters;
        this.context = context;
    }

    internal static StreamFilterChain Create(
        IReadOnlyList<IStreamScriptFilter> filters, Socket client, string? serverName, string? clusterId) =>
        new(filters, new StreamConnectionContext
        {
            ClientEndPoint = client.RemoteEndPoint as IPEndPoint,
            LocalEndPoint = client.LocalEndPoint as IPEndPoint,
            ServerName = serverName,
            ClusterId = clusterId,
            Items = new Dictionary<string, object?>(),
        });

    /// <summary>Runs every filter's new-connection hook. The first deny wins; the last cluster choice wins.</summary>
    internal async ValueTask<StreamFilterResult> OnNewConnectionAsync(CancellationToken cancellationToken)
    {
        string? clusterId = null;
        for (var i = 0; i < filters.Count; i++)
        {
            StreamFilterResult result;
            try
            {
                result = await filters[i].OnNewConnectionAsync(context, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                result = StreamFilterResult.Deny;
            }

            if (result.Action == ScriptAction.Deny)
            {
                return StreamFilterResult.Deny;
            }

            clusterId = result.ClusterId ?? clusterId;
        }

        return clusterId is null ? StreamFilterResult.Continue : new StreamFilterResult { ClusterId = clusterId };
    }

    /// <summary>
    /// Runs the data hooks for one piece. Each filter sees the bytes the previous one left. <paramref name="fromClient"/>
    /// is true for client-to-upstream data (the downstream hook).
    /// </summary>
    internal async ValueTask<StreamFilterResult> OnDataAsync(
        bool fromClient, ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
    {
        byte[]? replaced = null;
        for (var i = 0; i < filters.Count; i++)
        {
            var current = replaced is null ? data : replaced;
            StreamFilterResult result;
            try
            {
                if (fromClient)
                {
                    result = await filters[i].OnDownstreamDataAsync(context, current, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    result = await filters[i].OnUpstreamDataAsync(context, current, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                result = StreamFilterResult.Deny;
            }

            if (result.Action == ScriptAction.Deny)
            {
                return StreamFilterResult.Deny;
            }

            replaced = result.ReplaceData ?? replaced;
        }

        return replaced is null ? StreamFilterResult.Continue : new StreamFilterResult { ReplaceData = replaced };
    }

    /// <summary>Runs every close hook once. A throwing filter does not stop the others.</summary>
    internal async ValueTask OnCloseAsync()
    {
        if (Interlocked.Exchange(ref closed, 1) != 0)
        {
            return;
        }

        for (var i = 0; i < filters.Count; i++)
        {
            try
            {
                await filters[i].OnCloseAsync(context).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // The connection is already over. There is nothing to refuse.
            }
        }
    }
}
