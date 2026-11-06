using System;
using System.IO;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace Titanium.Web.Proxy.WebTransport;

/// <summary>Opens an HTTP/1.1 WebTransport upgrade to a pinned origin and copies capsule bytes.</summary>
internal static class WebTransportOrigin
{
    public static async Task<WebTransportOriginSession?> OpenHttp11Async(
        string host, int port, bool https, string path, CancellationToken cancellationToken)
    {
        if (HttpHeaderHasDelimiter(path) || HttpHeaderHasDelimiter(host))
        {
            return null;
        }

        var tcp = new TcpClient();
        try
        {
            await tcp.ConnectAsync(host, port, cancellationToken).ConfigureAwait(false);
            Stream stream = tcp.GetStream();
            if (https)
            {
                var ssl = new SslStream(stream, leaveInnerStreamOpen: false);
                await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
                {
                    TargetHost = host,
                    EnabledSslProtocols = SslProtocols.None,
                    CertificateRevocationCheckMode = System.Security.Cryptography.X509Certificates.X509RevocationMode.NoCheck,
                }, cancellationToken).ConfigureAwait(false);
                stream = ssl;
            }

            var request = Encoding.ASCII.GetBytes(
                $"GET {path} HTTP/1.1\r\nHost: {host}:{port}\r\nConnection: Upgrade\r\nUpgrade: webtransport\r\nCapsule-Protocol: ?1\r\n\r\n");
            await stream.WriteAsync(request, cancellationToken).ConfigureAwait(false);
            if (!await ReadSwitchingProtocolsAsync(stream, cancellationToken).ConfigureAwait(false))
            {
                tcp.Dispose();
                return null;
            }

            return new WebTransportOriginSession(tcp, stream);
        }
        catch (Exception)
        {
            tcp.Dispose();
            return null;
        }
    }

    public static async Task RelayAsync(
        ChannelReader<ReadOnlyMemory<byte>> inbound, Stream origin, Stream clientBody, CancellationToken cancellationToken)
    {
        using var relay = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var toOrigin = PumpChannelAsync(inbound, origin, relay.Token);
        var toClient = PumpStreamAsync(origin, clientBody, relay.Token);
        await Task.WhenAny(toOrigin, toClient).ConfigureAwait(false);
        await relay.CancelAsync().ConfigureAwait(false);
        try
        {
            origin.Close();
        }
        catch (Exception)
        {
            // Closing unblocks the other direction.
        }

        await Task.WhenAll(
            toOrigin.ContinueWith(static _ => { }, TaskScheduler.Default),
            toClient.ContinueWith(static _ => { }, TaskScheduler.Default)).ConfigureAwait(false);
    }

    private static async Task PumpChannelAsync(ChannelReader<ReadOnlyMemory<byte>> inbound, Stream origin, CancellationToken cancellationToken)
    {
        await foreach (var chunk in inbound.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            if (chunk.Length == 0)
            {
                continue;
            }

            await origin.WriteAsync(chunk, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task PumpStreamAsync(Stream origin, Stream clientBody, CancellationToken cancellationToken)
    {
        var buffer = new byte[16 * 1024];
        while (!cancellationToken.IsCancellationRequested)
        {
            var read = await origin.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                return;
            }

            await clientBody.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task<bool> ReadSwitchingProtocolsAsync(Stream stream, CancellationToken cancellationToken)
    {
        var status = await ReadLineAsync(stream, cancellationToken).ConfigureAwait(false);
        if (status is null || !status.Contains(" 101 ", StringComparison.Ordinal))
        {
            return false;
        }

        while (true)
        {
            var line = await ReadLineAsync(stream, cancellationToken).ConfigureAwait(false);
            if (line is null)
            {
                return false;
            }

            if (line.Length == 0)
            {
                return true;
            }
        }
    }

    private static async Task<string?> ReadLineAsync(Stream stream, CancellationToken cancellationToken)
    {
        var builder = new StringBuilder();
        var one = new byte[1];
        while (builder.Length < 8192)
        {
            var read = await stream.ReadAsync(one, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                return builder.Length == 0 ? null : builder.ToString();
            }

            if (one[0] == (byte)'\n')
            {
                if (builder.Length > 0 && builder[^1] == '\r')
                {
                    builder.Length--;
                }

                return builder.ToString();
            }

            builder.Append((char)one[0]);
        }

        return null;
    }

    private static bool HttpHeaderHasDelimiter(string value) =>
        value.Contains('\r') || value.Contains('\n') || value.Contains('\0');
}

internal sealed class WebTransportOriginSession : IAsyncDisposable
{
    private readonly TcpClient client;

    public WebTransportOriginSession(TcpClient client, Stream stream)
    {
        this.client = client;
        Stream = stream;
    }

    public Stream Stream { get; }

    public async ValueTask DisposeAsync()
    {
        await Stream.DisposeAsync().ConfigureAwait(false);
        client.Dispose();
    }
}
