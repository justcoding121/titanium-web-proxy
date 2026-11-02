using System;
using System.Buffers;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using Titanium.Web.Proxy.Abstractions.Clusters;
using Titanium.Web.Proxy.Abstractions.Routing;
using Titanium.Web.Proxy.Abstractions.Scripting;
using Titanium.Web.Proxy.Certificates;
using Titanium.Web.Proxy.Clusters;
using Titanium.Web.Proxy.Diagnostics;
using Titanium.Web.Proxy.Models;
using Titanium.Web.Proxy.Network;

namespace Titanium.Web.Proxy;

public partial class ProxyServer
{
    private static readonly Dictionary<int, SemaphoreSlim> TlsTerminateHandshakeGates = new();

    private async Task HandleTlsTerminateClientAsync(
        Socket client,
        TransparentBaseProxyEndPoint endPoint,
        CancellationToken cancellationToken)
    {
        var route = "tls-terminate";
        var result = "ok";
        long bytesIn = 0;
        long bytesOut = 0;
        StreamFilterChain? filters = null;
        if (!TryEnterTlsHandshake(endPoint, out var handshakeGate))
        {
            client.Dispose();
            ReleaseClientConnection(endPoint);
            ProxyMetrics.RecordPassthrough(route, "handshake_overload", 0, 0);
            return;
        }

        try
        {
            await using var networkStream = new NetworkStream(client, ownsSocket: false);
            await using var clientTls = new SslStream(networkStream, leaveInnerStreamOpen: false);
            if (!await TryAuthenticateInboundTlsAsync(clientTls, endPoint, cancellationToken).ConfigureAwait(false))
            {
                result = "handshake_failed";
                return;
            }

            var serverName = clientTls.TargetHostName;
            string? clusterId = null;
            if (endPoint.PassthroughTable?.Match(serverName) is { } matched)
            {
                clusterId = matched.ClusterId;
                route = matched.ServerName;
            }
            else if (!string.IsNullOrEmpty(endPoint.PassthroughDefaultClusterId))
            {
                clusterId = endPoint.PassthroughDefaultClusterId;
                route = "default";
            }

            if (endPoint.StreamFilters is { } streamFilters)
            {
                filters = StreamFilterChain.Create(streamFilters, client, serverName, clusterId);
                var accepted = await filters.OnNewConnectionAsync(cancellationToken).ConfigureAwait(false);
                if (accepted.Action == ScriptAction.Deny)
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

            if (clusterId is null)
            {
                route = "none";
                result = "no_route";
                return;
            }

            var relayOptions = CreatePassthroughOptions(client, endPoint);
            relayOptions.Filters = filters;
            relayOptions.HalfCloseGrace = TimeSpan.FromSeconds(5);
            (bytesIn, bytesOut, result) = await RelayTlsTerminateToClusterAsync(
                client, clientTls, clusterId, relayOptions, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            result = "cancelled";
            bytesIn = 0;
            bytesOut = 0;
        }
        catch (Exception ex)
        {
            result = "error";
            bytesIn = 0;
            bytesOut = 0;
            OnException(null, ex);
        }
        finally
        {
            handshakeGate.Release();
            client.Dispose();
            if (filters is not null)
            {
                await filters.OnCloseAsync().ConfigureAwait(false);
            }

            ReleaseClientConnection(endPoint);
            ProxyMetrics.RecordPassthrough(route, result, bytesIn, bytesOut);
        }
    }

    private static bool TryEnterTlsHandshake(TransparentBaseProxyEndPoint endPoint, out SemaphoreSlim gate)
    {
        var limit = endPoint.MaxConcurrentTlsHandshakes > 0 ? endPoint.MaxConcurrentTlsHandshakes : 256;
        lock (TlsTerminateHandshakeGates)
        {
            if (!TlsTerminateHandshakeGates.TryGetValue(endPoint.Port, out gate!))
            {
                gate = new SemaphoreSlim(limit, limit);
                TlsTerminateHandshakeGates[endPoint.Port] = gate;
            }
        }

        return gate.Wait(0);
    }

    private async Task<bool> TryAuthenticateInboundTlsAsync(
        SslStream clientTls,
        TransparentBaseProxyEndPoint endPoint,
        CancellationToken cancellationToken)
    {
        using var handshakeCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (endPoint.TlsHandshakeTimeout > TimeSpan.Zero)
        {
            handshakeCts.CancelAfter(endPoint.TlsHandshakeTimeout);
        }

        try
        {
            var certificate = endPoint.GenericCertificate;
            if (certificate is null && endPoint.SniCertificates is not { Count: > 0 })
            {
                return false;
            }

            var clientCertMode = endPoint.ClientCertificateMode;
            var requestsClientCert = ClientCertificateHandshake.RequestsCertificate(clientCertMode);
            var clientCertificateTrustedRoots = endPoint.ClientCertificateTrustedRoots;

            SslServerAuthenticationOptions BuildOptions(X509Certificate2 serverCertificate)
            {
                var built = new SslServerAuthenticationOptions
                {
                    ServerCertificate = serverCertificate,
                    ClientCertificateRequired = requestsClientCert,
                    EnabledSslProtocols = SupportedSslProtocols,
                    CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
                    AllowRenegotiation = false,
                };
                if (endPoint.TerminateTlsAlpn is { Count: > 0 })
                {
                    built.ApplicationProtocols = endPoint.TerminateTlsAlpn
                        .Select(static p => new System.Net.Security.SslApplicationProtocol(p))
                        .ToList();
                }

                CertificateManager.ConfigureServerCertificate(built, serverCertificate);
                ClientCertificateHandshake.Apply(built, clientCertMode, clientCertificateTrustedRoots);
                return built;
            }

            if (endPoint.SniCertificates is { Count: > 0 } bindings)
            {
                var fallback = certificate ?? bindings[0].Certificate;
                await clientTls.AuthenticateAsServerAsync(
                    (_, hello, _, _) => new ValueTask<SslServerAuthenticationOptions>(
                        BuildOptions(SniCertificateSelector.Select(hello.ServerName, bindings) ?? fallback)),
                    null,
                    handshakeCts.Token).ConfigureAwait(false);
            }
            else
            {
                await clientTls.AuthenticateAsServerAsync(BuildOptions(certificate!), handshakeCts.Token)
                    .ConfigureAwait(false);
            }

            return true;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return false;
        }
        catch (AuthenticationException)
        {
            return false;
        }
    }

    private async Task<(long BytesIn, long BytesOut, string Result)> RelayTlsTerminateToClusterAsync(
        Socket clientSocket,
        Stream clientPlaintext,
        string clusterId,
        TcpPassthroughOptions relayOptions,
        CancellationToken cancellationToken)
    {
        var reverse = ReverseProxy;
        var manager = reverse?.ClusterManager;
        if (manager is null || !manager.Snapshot.Clusters.TryGetValue(clusterId, out var cluster))
        {
            return (0, 0, "no_destination");
        }

        ApplyClusterProxyProtocol(clientSocket, cluster, relayOptions);

        var snapshot = manager.Snapshot;
        var selector = reverse!.LoadBalancer ?? (passthroughFallbackBalancer ??= new LoadBalancer());
        var balancer = selector as LoadBalancer ?? (passthroughFallbackBalancer ??= new LoadBalancer());
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
            Stream? upstream = null;
            try
            {
                upstream = await OpenTlsTerminateUpstreamAsync(
                    manager, cluster, destination, relayOptions, cancellationToken).ConfigureAwait(false);
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

            await using (upstream)
            {
                balancer.Health.ReportSuccess(destination.Id);
                var relayed = await RelayPlaintextStreamsAsync(
                    clientPlaintext, upstream, relayOptions, cancellationToken).ConfigureAwait(false);
                return (relayed.BytesIn, relayed.BytesOut, OutcomeName(relayed.Outcome));
            }
        }

        return (0, 0, tried.Count == 0 ? "no_destination" : "connect_failed");
    }

    private async Task<Stream> OpenTlsTerminateUpstreamAsync(
        IClusterManager manager,
        ClusterConfig cluster,
        DestinationConfig destination,
        TcpPassthroughOptions options,
        CancellationToken cancellationToken)
    {
        var host = ResolveDestinationHost(manager, destination);
        var port = DestinationPort(destination);
        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
        try
        {
            if (options.ConnectTimeout > TimeSpan.Zero)
            {
                using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                connectCts.CancelAfter(options.ConnectTimeout);
                await socket.ConnectAsync(host, port, connectCts.Token).ConfigureAwait(false);
            }
            else
            {
                await socket.ConnectAsync(host, port, cancellationToken).ConfigureAwait(false);
            }

            var stream = new NetworkStream(socket, ownsSocket: true);
            if (!options.Preamble.IsEmpty)
            {
                await stream.WriteAsync(options.Preamble, cancellationToken).ConfigureAwait(false);
            }

            if (!destination.UseHttps)
            {
                return stream;
            }

            var material = UpstreamTlsMaterial.For(cluster.HttpClient);
            material?.ThrowIfFaulted();
            var ssl = new SslStream(stream, leaveInnerStreamOpen: false,
                (_, certificate, chain, errors) => ValidateUpstreamCertificate(material, certificate, chain, errors),
                (_, _, _, _, _) => material?.ClientCertificate!);
            var targetHost = material?.ServerName ?? host;
            var auth = new SslClientAuthenticationOptions
            {
                TargetHost = targetHost,
                EnabledSslProtocols = SupportedSslProtocols,
                CertificateRevocationCheckMode = CheckCertificateRevocation,
            };
            if (material?.ClientCertificate is { } clientCert)
            {
                auth.ClientCertificates = new X509CertificateCollection { clientCert };
            }

            await ssl.AuthenticateAsClientAsync(auth, cancellationToken).ConfigureAwait(false);
            return ssl;
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    private bool ValidateUpstreamCertificate(
        UpstreamTlsMaterial? material,
        X509Certificate? certificate,
        X509Chain? chain,
        SslPolicyErrors errors)
    {
        if (material is null)
        {
            return errors == SslPolicyErrors.None;
        }

        if (material.AcceptAny)
        {
            return true;
        }

        errors = material.ApplyPolicy(certificate, chain, errors, CheckCertificateRevocation);
        return errors == SslPolicyErrors.None;
    }

    private static async Task<PassthroughRelayResult> RelayPlaintextStreamsAsync(
        Stream client,
        Stream upstream,
        TcpPassthroughOptions options,
        CancellationToken cancellationToken)
    {
        var clientBuffer = ArrayPool<byte>.Shared.Rent(16 * 1024);
        var upstreamBuffer = ArrayPool<byte>.Shared.Rent(16 * 1024);
        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            if (options.IdleTimeout > TimeSpan.Zero)
            {
                linked.CancelAfter(options.IdleTimeout);
            }

            var clientToUpstream = PumpStreamAsync(
                client, upstream, clientBuffer, options.Filters, fromClient: true, linked.Token);
            var upstreamToClient = PumpStreamAsync(
                upstream, client, upstreamBuffer, options.Filters, fromClient: false, linked.Token);
            await Task.WhenAll(clientToUpstream, upstreamToClient).ConfigureAwait(false);
            return new PassthroughRelayResult(await clientToUpstream, await upstreamToClient, PassthroughOutcome.Completed);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new PassthroughRelayResult(0, 0, PassthroughOutcome.IdleTimeout);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(clientBuffer);
            ArrayPool<byte>.Shared.Return(upstreamBuffer);
        }
    }

    private static async Task<long> PumpStreamAsync(
        Stream source,
        Stream sink,
        byte[] buffer,
        StreamFilterChain? filters,
        bool fromClient,
        CancellationToken cancellationToken)
    {
        long total = 0;
        while (true)
        {
            var read = await source.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken)
                .ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            var chunk = buffer.AsMemory(0, read);
            if (filters is not null)
            {
                var filtered = await filters.OnDataAsync(fromClient, chunk, cancellationToken).ConfigureAwait(false);
                if (filtered.Action == ScriptAction.Deny)
                {
                    break;
                }

                if (filtered.ReplaceData is { } replaced)
                {
                    chunk = replaced;
                }
            }

            await sink.WriteAsync(chunk, cancellationToken).ConfigureAwait(false);
            total += chunk.Length;
        }

        return total;
    }
}
