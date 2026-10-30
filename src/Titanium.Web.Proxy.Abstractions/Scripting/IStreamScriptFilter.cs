using System.Net;

namespace Titanium.Web.Proxy.Abstractions.Scripting;

/// <summary>What an L4 filter knows about a passthrough connection.</summary>
public sealed class StreamConnectionContext
{
    public IPEndPoint? ClientEndPoint { get; init; }
    public IPEndPoint? LocalEndPoint { get; init; }

    /// <summary>TLS server name read from the ClientHello when the listener routes by SNI; otherwise null.</summary>
    public string? ServerName { get; init; }

    /// <summary>Cluster the listener picked before filters ran, or null.</summary>
    public string? ClusterId { get; init; }

    /// <summary>State a filter keeps for the life of this connection. Never shared between connections.</summary>
    public required IDictionary<string, object?> Items { get; init; }
}

/// <summary>A filter's answer to a connection event.</summary>
public sealed class StreamFilterResult
{
    /// <summary>Carry on unchanged. A shared instance, so the common answer allocates nothing.</summary>
    public static readonly StreamFilterResult Continue = new();

    /// <summary>Close the connection.</summary>
    public static readonly StreamFilterResult Deny = new() { Action = ScriptAction.Deny };

    public ScriptAction Action { get; init; }

    /// <summary>New-connection event only: send the connection to this cluster instead of the one the listener picked.</summary>
    public string? ClusterId { get; init; }

    /// <summary>Data events only: forward these bytes instead of the ones read. An empty array drops the piece.</summary>
    public byte[]? ReplaceData { get; init; }
}

/// <summary>
/// L4 hook for transparent passthrough listeners. Set on <c>TransparentBaseProxyEndPoint.StreamFilters</c>. The accept loop
/// picks the filtered relay when the list is non-null and keeps the plain relay otherwise, so a listener with no filter pays nothing.
/// Filters run in list order; the first <see cref="ScriptAction.Deny"/> wins.
/// </summary>
public interface IStreamScriptFilter
{
    /// <summary>Runs once at accept, before any route is used. May deny or choose a cluster.</summary>
    ValueTask<StreamFilterResult> OnNewConnectionAsync(StreamConnectionContext context, CancellationToken cancellationToken) =>
        new(StreamFilterResult.Continue);

    /// <summary>Runs for each piece read from the client, before it is sent upstream.</summary>
    ValueTask<StreamFilterResult> OnDownstreamDataAsync(StreamConnectionContext context, ReadOnlyMemory<byte> data,
        CancellationToken cancellationToken) => new(StreamFilterResult.Continue);

    /// <summary>Runs for each piece read from the upstream, before it is sent to the client.</summary>
    ValueTask<StreamFilterResult> OnUpstreamDataAsync(StreamConnectionContext context, ReadOnlyMemory<byte> data,
        CancellationToken cancellationToken) => new(StreamFilterResult.Continue);

    /// <summary>Runs once when the connection ends. A throw is swallowed.</summary>
    ValueTask OnCloseAsync(StreamConnectionContext context) => default;
}
