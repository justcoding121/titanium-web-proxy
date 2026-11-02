using System;
using System.Buffers;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Titanium.Web.Proxy.Abstractions.Clusters;
using Titanium.Web.Proxy.Abstractions.Routing;
using Titanium.Web.Proxy.Clusters;
using Titanium.Web.Proxy.Diagnostics;
using Titanium.Web.Proxy.Models;
using Titanium.Web.Proxy.Network;
using Titanium.Web.Proxy.Network.Ssl;

namespace Titanium.Web.Proxy;

public partial class ProxyServer
{
    /// <summary>Destinations a single passthrough connection may try before it is dropped.</summary>
    private const int MaxPassthroughAttempts = 3;

    private static readonly TimeSpan DefaultPassthroughSniReadTimeout = TimeSpan.FromSeconds(5);

    private LoadBalancer? passthroughFallbackBalancer;

    private async Task HandlePassthroughClientAsync( // NOSONAR S3776 -- Passthrough path shares socket state across destination attempts.
        Socket client, TransparentBaseProxyEndPoint endPoint, CancellationToken cancellationToken)
    {
        if (endPoint.TerminateTls)
        {
            await HandleTlsTerminateClientAsync(client, endPoint, cancellationToken).ConfigureAwait(false);
            return;
        }

        var route = "forward";
        var result = "ok";
        long bytesIn = 0;
        long bytesOut = 0;
        byte[]? helloBuffer = null;
        StreamFilterChain? filters = null;
        try
        {
            var initial = ReadOnlyMemory<byte>.Empty;
            string? serverName = null;
            var table = endPoint.PassthroughTable;
            if (table is not null)
            {
                helloBuffer = ArrayPool<byte>.Shared.Rent(ClientHelloSniParser.MaxRecordWithHeader);
                int length;
                (length, serverName) = await ReadClientHelloAsync(
                    client, helloBuffer, endPoint, cancellationToken).ConfigureAwait(false);
                if (length < 0)
                {
                    result = "client_closed";
                    route = "none";
                    return;
                }

                initial = helloBuffer.AsMemory(0, length);
            }

            string? clusterId = null;
            if (serverName is not null && table?.Match(serverName) is { } matched)
            {
                clusterId = matched.ClusterId;
                route = matched.ServerName;
            }
            else if (!string.IsNullOrEmpty(endPoint.PassthroughDefaultClusterId))
            {
                clusterId = endPoint.PassthroughDefaultClusterId;
                route = "default";
            }

            // L4 script filters are chosen here, once per connection. A listener without them skips this block.
            if (endPoint.StreamFilters is { } streamFilters)
            {
                filters = StreamFilterChain.Create(streamFilters, client, serverName, clusterId);
                var accepted = await filters.OnNewConnectionAsync(cancellationToken).ConfigureAwait(false);
                var admitted = accepted.Action != Abstractions.Scripting.ScriptAction.Deny;
                if (admitted && !initial.IsEmpty)
                {
                    var first = await filters.OnDataAsync(true, initial, cancellationToken).ConfigureAwait(false);
                    admitted = first.Action != Abstractions.Scripting.ScriptAction.Deny;
                    if (first.ReplaceData is { } replaced)
                    {
                        initial = replaced;
                    }
                }

                if (!admitted)
                {
                    route = "filter";
                    result = "denied";
                    return;
                }

                if (accepted.ClusterId is { } chosenCluster)
                {
                    clusterId = chosenCluster;
                    route = "script";
                }
            }

            var relayOptions = CreatePassthroughOptions(client, endPoint);
            relayOptions.Filters = filters;
            PassthroughRelayResult relayed;
            if (clusterId is not null)
            {
                (relayed, result) = await RelayToClusterAsync(
                    client, clusterId, initial, relayOptions, cancellationToken).ConfigureAwait(false);
            }
            else if (endPoint.ForwardHost is { Length: > 0 } forwardHost)
            {
                var port = endPoint.ForwardPort ?? (endPoint.DecryptSsl ? 443 : 80);
                relayed = await TcpPassthrough.RelayAsync(
                    client, forwardHost, port, initial, relayOptions, cancellationToken).ConfigureAwait(false);
                result = OutcomeName(relayed.Outcome);
            }
            else
            {
                route = "none";
                result = "no_route";
                return;
            }

            bytesIn = relayed.BytesIn;
            bytesOut = relayed.BytesOut;
        }
        catch (OperationCanceledException)
        {
            // The proxy is stopping. Closing the sockets below ends the relay.
            result = "cancelled";
        }
        catch (Exception ex)
        {
            result = "connect_failed";
            OnException(null, ex);
        }
        finally
        {
            if (helloBuffer is not null)
            {
                ArrayPool<byte>.Shared.Return(helloBuffer);
            }

            client.Dispose();
            if (filters is not null)
            {
                await filters.OnCloseAsync().ConfigureAwait(false);
            }

            ReleaseClientConnection(endPoint);
            ProxyMetrics.RecordPassthrough(route, result, bytesIn, bytesOut);
        }
    }

    private static TcpPassthroughOptions CreatePassthroughOptions(Socket client, TransparentBaseProxyEndPoint endPoint)
    {
        // Zero grace keeps the plain forward-host path on the zero-cost relay (no idle/grace CTS). An EOF
        // still half-closes so the origin can answer; set PassthroughIdleTimeout to bound a quiet peer.
        // Idle timeout or a PROXY preamble alone opts into the enhanced path.
        var options = new TcpPassthroughOptions
        {
            IdleTimeout = endPoint.PassthroughIdleTimeout,
            HalfCloseGrace = TimeSpan.Zero,
        };
        if (endPoint.PassthroughProxyProtocol == PassthroughProxyProtocolMode.V2)
        {
            options.Preamble = ProxyProtocolWriter.BuildV2(
                client.RemoteEndPoint as IPEndPoint, client.LocalEndPoint as IPEndPoint);
        }

        return options;
    }

    /// <summary>
    ///     A cluster's <c>sendProxyProtocol</c> replaces the listener's setting: <c>v2</c> writes the header,
    ///     <c>none</c> suppresses it, and null keeps whatever the listener chose.
    /// </summary>
    private static void ApplyClusterProxyProtocol(Socket client, ClusterConfig cluster, TcpPassthroughOptions options)
    {
        var mode = cluster.SendProxyProtocol;
        if (string.IsNullOrWhiteSpace(mode))
        {
            return;
        }

        options.Preamble = mode.Equals("v2", StringComparison.OrdinalIgnoreCase)
            ? ProxyProtocolWriter.BuildV2(client.RemoteEndPoint as IPEndPoint, client.LocalEndPoint as IPEndPoint)
            : ReadOnlyMemory<byte>.Empty;
    }

    /// <summary>
    ///     Reads until the first TLS record is complete, the bytes are clearly not TLS, the peer closes, or the SNI
    ///     timeout passes. Returns the byte count (possibly zero after a timeout), or -1 when the client closed
    ///     without sending anything.
    /// </summary>
    private static async Task<(int Length, string? ServerName)> ReadClientHelloAsync(
        Socket client, byte[] buffer, TransparentBaseProxyEndPoint endPoint, CancellationToken cancellationToken)
    {
        string? serverName = null;
        var timeout = endPoint.PassthroughSniReadTimeout > TimeSpan.Zero
            ? endPoint.PassthroughSniReadTimeout
            : DefaultPassthroughSniReadTimeout;
        using var readCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        readCts.CancelAfter(timeout);
        var received = 0;
        var closed = false;
        try
        {
            while (received < ClientHelloSniParser.MaxRecordWithHeader)
            {
                var read = await client.ReceiveAsync(
                    buffer.AsMemory(received, ClientHelloSniParser.MaxRecordWithHeader - received),
                    SocketFlags.None, readCts.Token).ConfigureAwait(false);
                if (read == 0)
                {
                    closed = true;
                    break;
                }

                received += read;
                if (ClientHelloSniParser.TryGetServerName(
                        buffer.AsSpan(0, received), out serverName, out var parsed) ||
                    parsed != ClientHelloSniParser.ParseResult.NeedMoreData)
                {
                    break;
                }
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // A client that waits for the server to speak first never sends a ClientHello.
            // It is routed like one with no server name.
        }

        return (closed && received == 0 ? -1 : received, serverName);
    }

    private async Task<(PassthroughRelayResult Relayed, string Result)> RelayToClusterAsync(
        Socket client, string clusterId, ReadOnlyMemory<byte> initial, TcpPassthroughOptions relayOptions,
        CancellationToken cancellationToken)
    {
        var none = new PassthroughRelayResult(0, 0, PassthroughOutcome.Error);
        var reverse = ReverseProxy;
        var manager = reverse?.ClusterManager;
        if (manager is null || !manager.Snapshot.Clusters.TryGetValue(clusterId, out var cluster))
        {
            return (none, "no_destination");
        }

        ApplyClusterProxyProtocol(client, cluster, relayOptions);

        var snapshot = manager.Snapshot;
        var selector = reverse!.LoadBalancer
                       ?? (passthroughFallbackBalancer ??= new LoadBalancer());
        var balancer = selector as LoadBalancer
                       ?? (passthroughFallbackBalancer ??= new LoadBalancer());
        var eligible = selector is LoadBalancer concrete
            ? concrete.CountEligible(cluster, snapshot)
            : cluster.Destinations.Count;
        var attempts = Math.Min(MaxPassthroughAttempts, eligible);
        var tried = new List<string>(attempts);
        relayOptions.ConnectTimeout = TimeSpan.FromSeconds(Math.Max(1, ConnectTimeOutSeconds));

        for (var attempt = 0; attempt < attempts; attempt++)
        {
            var destination = attempt == 0
                ? selector.Select(cluster, snapshot)
                : balancer.SelectFirstUntried(cluster, snapshot, id => tried.Contains(id));
            if (destination is null || tried.Contains(destination.Id))
            {
                break;
            }

            tried.Add(destination.Id);
            using var lease = balancer.Health.TrackRequest(destination.Id);
            Socket upstream;
            try
            {
                upstream = await TcpPassthrough.OpenAsync(
                    ResolveDestinationHost(manager, destination), DestinationPort(destination), initial, relayOptions,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception)
            {
                balancer.Health.ReportFailure(destination.Id, manager);
                continue;
            }

            using (upstream)
            {
                balancer.Health.ReportSuccess(destination.Id);
                var relayed = await TcpPassthrough.RelayAsync(client, upstream, relayOptions, cancellationToken)
                    .ConfigureAwait(false);
                relayed = relayed with { BytesIn = relayed.BytesIn + initial.Length };
                return (relayed, OutcomeName(relayed.Outcome));
            }
        }

        return (none, tried.Count == 0 ? "no_destination" : "connect_failed");
    }

    private static string ResolveDestinationHost(IClusterManager manager, DestinationConfig destination) =>
        destination.DnsRefreshSeconds > 0 &&
        manager is ClusterManager dns &&
        dns.TryGetResolved(destination.Id, out var pinned)
            ? pinned.ToString()
            : destination.Address;

    private static int DestinationPort(DestinationConfig destination)
    {
        if (destination.Port != 0)
        {
            return destination.Port;
        }

        return destination.UseHttps ? 443 : 80;
    }

    private static string OutcomeName(PassthroughOutcome outcome) => outcome switch
    {
        PassthroughOutcome.Completed => "ok",
        PassthroughOutcome.IdleTimeout => "idle_timeout",
        PassthroughOutcome.GraceExpired => "grace_expired",
        PassthroughOutcome.Cancelled => "cancelled",
        PassthroughOutcome.Denied => "denied",
        _ => "error",
    };
}
