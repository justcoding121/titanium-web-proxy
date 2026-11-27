#pragma warning disable CA1416 // The platform stack is an optional peer. This process does not listen on Windows.
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Quic;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using Titanium.Quic;

namespace Titanium.Managed.Benchmarks;

internal static class QuicBenchHarness
{
    internal const int SmallBytes = 100;

    internal const int BulkBytes = 1024 * 1024;

    internal const int ChunkBytes = 8 * 1024;

    private static readonly SslApplicationProtocol Protocol = new("test");

    internal static bool PlatformListenerAllowed =>
        !OperatingSystem.IsWindows()
        && string.Equals(Environment.GetEnvironmentVariable("TITANIUM_QUIC_PLATFORM_LISTENER"), "1", StringComparison.Ordinal);

    internal static IEnumerable<StackKind> Stacks()
    {
        yield return StackKind.Managed;
        if (PlatformListenerAllowed)
        {
            yield return StackKind.Platform;
        }
    }

    internal static async Task Handshake(StackKind stack, X509Certificate2 certificate)
    {
        await using Opened opened = await Open(stack, certificate).ConfigureAwait(false);
    }

    internal static async Task Exchange(Opened opened, byte[] payload)
    {
        await opened.Outbound.WriteAsync(payload).ConfigureAwait(false);
        await ReadExactly(opened.Outbound, payload.Length).ConfigureAwait(false);
    }

    internal static async Task Transfer(Opened opened, byte[] chunk)
    {
        Task read = ReadExactly(opened.Inbound, BulkBytes);
        long written = 0;
        while (written < BulkBytes)
        {
            int length = (int)Math.Min(chunk.Length, BulkBytes - written);
            await opened.Outbound.WriteAsync(chunk.AsMemory(0, length)).ConfigureAwait(false);
            written += length;
        }

        await read.ConfigureAwait(false);
    }

    internal static Task Echo(Opened opened, CancellationToken cancellationToken)
    {
        return Task.Run(async () =>
        {
            var buffer = new byte[64 * 1024];
            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    int count = await opened.Inbound.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                    if (count == 0)
                    {
                        return;
                    }

                    await opened.Inbound.WriteAsync(buffer.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
                }
            }
            catch (Exception exception) when (exception is OperationCanceledException or IOException or QuicException or ObjectDisposedException)
            {
            }
        }, cancellationToken);
    }

    internal static Task<Opened> Open(StackKind stack, X509Certificate2 certificate)
    {
        return stack == StackKind.Platform ? OpenPlatform(certificate) : OpenManaged(certificate);
    }

    private static async Task<Opened> OpenManaged(X509Certificate2 certificate)
    {
        ManagedQuicListener listener = await ManagedQuicListener.ListenAsync(new ManagedQuicListenerOptions
        {
            ListenEndPoint = new IPEndPoint(IPAddress.Loopback, 0),
            ConnectionOptionsCallback = (_, _, _) => ValueTask.FromResult(new ManagedQuicServerConnectionOptions
            {
                ServerAuthenticationOptions = ServerOptions(certificate),
            }),
        }).ConfigureAwait(false);
        try
        {
            RequireEphemeral(listener.LocalEndPoint.Port);
            Task<ManagedQuicConnection> accepted = listener.AcceptConnectionAsync().AsTask();
            ManagedQuicConnection client = await ManagedQuicConnection.ConnectAsync(new ManagedQuicClientConnectionOptions
            {
                RemoteEndPoint = listener.LocalEndPoint,
                ClientAuthenticationOptions = ClientOptions(),
            }).ConfigureAwait(false);
            ManagedQuicConnection server = await accepted.ConfigureAwait(false);
            ManagedQuicStream outbound = await client.OpenOutboundStreamAsync(QuicStreamType.Bidirectional).ConfigureAwait(false);
            ManagedQuicStream inbound = await server.AcceptInboundStreamAsync().ConfigureAwait(false);
            return new Opened(outbound, inbound, async () =>
            {
                await client.DisposeAsync().ConfigureAwait(false);
                await server.DisposeAsync().ConfigureAwait(false);
                await listener.DisposeAsync().ConfigureAwait(false);
            });
        }
        catch
        {
            await listener.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static async Task<Opened> OpenPlatform(X509Certificate2 certificate)
    {
        if (!PlatformListenerAllowed)
        {
            throw new InvalidOperationException("A platform QUIC listener is not started on Windows.");
        }

        if (!QuicListener.IsSupported)
        {
            throw new InvalidOperationException("The platform QUIC listener is not supported on this machine.");
        }

        var listener = await QuicListener.ListenAsync(new QuicListenerOptions
        {
            ListenEndPoint = new IPEndPoint(IPAddress.Loopback, 0),
            ApplicationProtocols = new List<SslApplicationProtocol> { Protocol },
            ConnectionOptionsCallback = (_, _, _) => ValueTask.FromResult(new QuicServerConnectionOptions
            {
                DefaultCloseErrorCode = 0,
                DefaultStreamErrorCode = 0,
                IdleTimeout = TimeSpan.FromMinutes(1),
                MaxInboundBidirectionalStreams = 4,
                ServerAuthenticationOptions = ServerOptions(certificate),
            }),
        }).ConfigureAwait(false);
        try
        {
            RequireEphemeral(listener.LocalEndPoint.Port);
            Task<QuicConnection> accepted = listener.AcceptConnectionAsync().AsTask();
            QuicConnection client = await QuicConnection.ConnectAsync(new QuicClientConnectionOptions
            {
                RemoteEndPoint = listener.LocalEndPoint,
                DefaultCloseErrorCode = 0,
                DefaultStreamErrorCode = 0,
                IdleTimeout = TimeSpan.FromMinutes(1),
                ClientAuthenticationOptions = ClientOptions(),
            }).ConfigureAwait(false);
            QuicConnection server = await accepted.ConfigureAwait(false);
            QuicStream outbound = await client.OpenOutboundStreamAsync(QuicStreamType.Bidirectional).ConfigureAwait(false);
            QuicStream inbound = await server.AcceptInboundStreamAsync().ConfigureAwait(false);
            return new Opened(outbound, inbound, async () =>
            {
                await client.DisposeAsync().ConfigureAwait(false);
                await server.DisposeAsync().ConfigureAwait(false);
                await listener.DisposeAsync().ConfigureAwait(false);
            });
        }
        catch
        {
            await listener.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static SslServerAuthenticationOptions ServerOptions(X509Certificate2 certificate)
    {
        return new SslServerAuthenticationOptions
        {
            ServerCertificate = certificate,
            ApplicationProtocols = new List<SslApplicationProtocol> { Protocol },
        };
    }

    private static SslClientAuthenticationOptions ClientOptions()
    {
        return new SslClientAuthenticationOptions
        {
            TargetHost = "localhost",
            ApplicationProtocols = new List<SslApplicationProtocol> { Protocol },
            RemoteCertificateValidationCallback = (_, _, _, _) => true,
        };
    }

    private static async Task ReadExactly(Stream stream, int total)
    {
        var buffer = new byte[64 * 1024];
        int read = 0;
        while (read < total)
        {
            int count = await stream.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, total - read))).ConfigureAwait(false);
            if (count == 0)
            {
                throw new EndOfStreamException("The transfer ended early.");
            }

            read += count;
        }
    }

    private static void RequireEphemeral(int port)
    {
        if (port is 0 or 8000 or 8866 or 9080 or 9464)
        {
            throw new InvalidOperationException("The benchmark refused port " + port + ".");
        }
    }

    internal sealed class Opened : IAsyncDisposable
    {
        private readonly Func<Task> _close;

        internal Opened(Stream outbound, Stream inbound, Func<Task> close)
        {
            Outbound = outbound;
            Inbound = inbound;
            _close = close;
        }

        internal Stream Outbound { get; }

        internal Stream Inbound { get; }

        public async ValueTask DisposeAsync() => await _close().ConfigureAwait(false);
    }
}
