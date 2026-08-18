using System.Buffers;
using System.Buffers.Text;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using Titanium.Web.Proxy.RpsLoadProbe.Support;

namespace Titanium.Web.Proxy.RpsLoadProbe;

/// <summary>
///     Minimal C# HTTP/1 reverse proxy (no MITM session model) used to measure the runtime
///     ceiling against TWP and the native reverse peer on the same loopback GET workload.
/// </summary>
internal sealed class BareHttp1ReverseProxy : IDisposable
{
    private readonly Socket listener;
    private readonly IPEndPoint originEndPoint;
    private readonly X509Certificate2? serverCertificate;
    private readonly CancellationTokenSource cts = new();
    private readonly List<Task> clients = [];
    private readonly object clientsGate = new();

    public int Port { get; }
    public string ListenUrl { get; }

    private BareHttp1ReverseProxy(Socket listener, IPEndPoint originEndPoint, X509Certificate2? serverCertificate)
    {
        this.listener = listener;
        this.originEndPoint = originEndPoint;
        this.serverCertificate = serverCertificate;
        Port = ((IPEndPoint)listener.LocalEndPoint!).Port;
        ListenUrl = serverCertificate == null
            ? $"http://127.0.0.1:{Port}/"
            : $"https://127.0.0.1:{Port}/";
    }

    public static BareHttp1ReverseProxy Start(int originHttpPort, bool tlsTerminate = false)
    {
        var listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp)
        {
            NoDelay = true
        };
        listener.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        listener.Listen(int.MaxValue);

        X509Certificate2? cert = null;
        if (tlsTerminate)
            cert = LoopbackCertificateAuthority.ServerCertificate;

        var host = new BareHttp1ReverseProxy(listener, new IPEndPoint(IPAddress.Loopback, originHttpPort), cert);
        _ = host.AcceptLoopAsync();
        return host;
    }

    private async Task AcceptLoopAsync()
    {
        try
        {
            while (!cts.IsCancellationRequested)
            {
                Socket client;
                try
                {
                    client = await listener.AcceptAsync(cts.Token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (ObjectDisposedException)
                {
                    break;
                }

                client.NoDelay = true;
                var task = Task.Run(() => HandleClientAsync(client));
                lock (clientsGate)
                    clients.Add(task);
                _ = task.ContinueWith(_ =>
                {
                    lock (clientsGate)
                        clients.Remove(task);
                }, TaskScheduler.Default);
            }
        }
        catch (Exception ex)
        {
            ProbeLog.Error($"bare-reverse accept loop failed: {ex.Message}");
        }
    }

    private async Task HandleClientAsync(Socket client)
    {
        Stream? clientStream = null;
        Socket? origin = null;
        Stream? originStream = null;
        try
        {
            clientStream = new NetworkStream(client, ownsSocket: true);
            if (serverCertificate != null)
            {
                var ssl = new SslStream(clientStream, leaveInnerStreamOpen: false);
                await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
                {
                    ServerCertificate = serverCertificate,
                    EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                    CertificateRevocationCheckMode = X509RevocationMode.NoCheck
                }, cts.Token);
                clientStream = ssl;
            }

            origin = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp)
            {
                NoDelay = true
            };
            await origin.ConnectAsync(originEndPoint, cts.Token);
            originStream = new NetworkStream(origin, ownsSocket: true);

            var buffer = ArrayPool<byte>.Shared.Rent(16 * 1024);
            var clientReader = new PrefixStream(clientStream);
            var originReader = new PrefixStream(originStream);
            try
            {
                while (!cts.IsCancellationRequested)
                {
                    if (!await RelayMessageAsync(clientReader, originStream, buffer, isRequest: true,
                            cts.Token))
                        break;
                    if (!await RelayMessageAsync(originReader, clientStream, buffer, isRequest: false,
                            cts.Token))
                        break;
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }
        catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException
                                       or ObjectDisposedException or AuthenticationException)
        {
            // Peer close / idle teardown is expected under a saturation ramp.
        }
        finally
        {
            if (originStream != null)
                await originStream.DisposeAsync();
            else
                origin?.Dispose();

            if (clientStream != null)
                await clientStream.DisposeAsync();
            else
                client.Dispose();
        }
    }

    /// <summary>
    ///     Copy one HTTP/1 message (headers + framed body) from <paramref name="from" /> to
    ///     <paramref name="to" />. Returns false when the source hits EOF before a message.
    /// </summary>
    private static async Task<bool> RelayMessageAsync(PrefixStream from, Stream to, byte[] buffer, bool isRequest,
        CancellationToken cancellationToken)
    {
        var headerBytes = await ReadHeadersAsync(from, buffer, cancellationToken);
        if (headerBytes < 0)
            return false;

        var headers = buffer.AsSpan(0, headerBytes);
        var contentLength = ParseContentLength(headers);
        var chunked = HasToken(headers, "transfer-encoding:"u8, "chunked"u8);
        var connectionClose = HasToken(headers, "connection:"u8, "close"u8);

        await to.WriteAsync(buffer.AsMemory(0, headerBytes), cancellationToken);

        if (chunked)
        {
            await CopyChunkedAsync(from, to, buffer, cancellationToken);
        }
        else if (contentLength > 0)
        {
            await CopyFixedAsync(from, to, buffer, contentLength, cancellationToken);
        }
        else if (!isRequest && contentLength < 0 && connectionClose)
        {
            await from.CopyToAsync(to, cancellationToken);
            return false;
        }

        if (connectionClose)
            return false;

        return true;
    }

    private static async Task<int> ReadHeadersAsync(PrefixStream stream, byte[] buffer,
        CancellationToken cancellationToken)
    {
        var filled = 0;
        while (filled < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(filled, buffer.Length - filled), cancellationToken);
            if (read == 0)
                return filled == 0 ? -1 : throw new IOException("EOF in the middle of HTTP headers.");

            filled += read;
            var end = IndexOfHeaderTerminator(buffer.AsSpan(0, filled));
            if (end >= 0)
            {
                if (end < filled)
                    stream.Unread(buffer.AsSpan(end, filled - end));
                return end;
            }
        }

        throw new IOException("HTTP header block exceeded the relay buffer.");
    }

    /// <summary>
    ///     Stream wrapper that can push unused bytes back after a header scan overshoots into the body.
    /// </summary>
    private sealed class PrefixStream : Stream
    {
        private readonly Stream inner;
        private byte[]? prefix;
        private int prefixOffset;
        private int prefixCount;

        public PrefixStream(Stream inner) => this.inner = inner;

        public void Unread(ReadOnlySpan<byte> data)
        {
            if (data.IsEmpty) return;
            if (prefixCount > 0)
            {
                var merged = new byte[data.Length + prefixCount];
                data.CopyTo(merged);
                prefix.AsSpan(prefixOffset, prefixCount).CopyTo(merged.AsSpan(data.Length));
                prefix = merged;
                prefixOffset = 0;
                prefixCount = merged.Length;
                return;
            }

            prefix = data.ToArray();
            prefixOffset = 0;
            prefixCount = prefix.Length;
        }

        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count,
            CancellationToken cancellationToken)
        {
            if (prefixCount > 0)
            {
                var take = Math.Min(count, prefixCount);
                prefix.AsSpan(prefixOffset, take).CopyTo(buffer.AsSpan(offset, take));
                prefixOffset += take;
                prefixCount -= take;
                return take;
            }

            return await inner.ReadAsync(buffer.AsMemory(offset, count), cancellationToken);
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (prefixCount > 0)
            {
                var take = Math.Min(buffer.Length, prefixCount);
                prefix.AsSpan(prefixOffset, take).CopyTo(buffer.Span);
                prefixOffset += take;
                prefixCount -= take;
                return ValueTask.FromResult(take);
            }

            return inner.ReadAsync(buffer, cancellationToken);
        }

        public override void Flush() => inner.Flush();
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }
        public override int Read(byte[] buffer, int offset, int count) =>
            ReadAsync(buffer, offset, count, CancellationToken.None).GetAwaiter().GetResult();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private static int IndexOfHeaderTerminator(ReadOnlySpan<byte> data)
    {
        var needle = "\r\n\r\n"u8;
        var idx = data.IndexOf(needle);
        return idx < 0 ? -1 : idx + needle.Length;
    }

    private static long ParseContentLength(ReadOnlySpan<byte> headers)
    {
        var name = "content-length:"u8;
        var line = FindHeaderLine(headers, name);
        if (line.IsEmpty)
            return -1;

        var value = TrimAscii(line);
        if (!Utf8Parser.TryParse(value, out long length, out _))
            return -1;
        return length;
    }

    private static bool HasToken(ReadOnlySpan<byte> headers, ReadOnlySpan<byte> name, ReadOnlySpan<byte> token)
    {
        var line = FindHeaderLine(headers, name);
        if (line.IsEmpty)
            return false;

        var value = TrimAscii(line);
        return value.IndexOf(token) >= 0;
    }

    private static ReadOnlySpan<byte> FindHeaderLine(ReadOnlySpan<byte> headers, ReadOnlySpan<byte> nameColon)
    {
        var remaining = headers;
        while (!remaining.IsEmpty)
        {
            var eol = remaining.IndexOf("\r\n"u8);
            if (eol < 0)
                break;

            var line = remaining[..eol];
            remaining = remaining[(eol + 2)..];
            if (line.Length >= nameColon.Length &&
                line[..nameColon.Length].SequenceEqual(nameColon) ||
                StartsWithIgnoreCase(line, nameColon))
            {
                return line[nameColon.Length..];
            }
        }

        return default;
    }

    private static bool StartsWithIgnoreCase(ReadOnlySpan<byte> line, ReadOnlySpan<byte> asciiPrefix)
    {
        if (line.Length < asciiPrefix.Length)
            return false;
        for (var i = 0; i < asciiPrefix.Length; i++)
        {
            var a = line[i];
            var b = asciiPrefix[i];
            if (a == b)
                continue;
            if (a >= 'A' && a <= 'Z')
                a = (byte)(a + 32);
            if (b >= 'A' && b <= 'Z')
                b = (byte)(b + 32);
            if (a != b)
                return false;
        }

        return true;
    }

    private static ReadOnlySpan<byte> TrimAscii(ReadOnlySpan<byte> value)
    {
        var start = 0;
        var end = value.Length;
        while (start < end && (value[start] == (byte)' ' || value[start] == (byte)'\t'))
            start++;
        while (end > start && (value[end - 1] == (byte)' ' || value[end - 1] == (byte)'\t' ||
                               value[end - 1] == (byte)'\r' || value[end - 1] == (byte)'\n'))
            end--;
        return value[start..end];
    }

    private static async Task CopyFixedAsync(PrefixStream from, Stream to, byte[] buffer, long remaining,
        CancellationToken cancellationToken)
    {
        while (remaining > 0)
        {
            var toRead = (int)Math.Min(remaining, buffer.Length);
            var read = await from.ReadAsync(buffer.AsMemory(0, toRead), cancellationToken);
            if (read == 0)
                throw new IOException("EOF before Content-Length body completed.");
            await to.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            remaining -= read;
        }
    }

    private static async Task CopyChunkedAsync(PrefixStream from, Stream to, byte[] buffer,
        CancellationToken cancellationToken)
    {
        // Probe GETs are Content-Length; keep a correct chunked copy for completeness.
        while (true)
        {
            var lineLen = await ReadLineIntoAsync(from, buffer, cancellationToken);
            await to.WriteAsync(buffer.AsMemory(0, lineLen), cancellationToken);
            var sizeSpan = buffer.AsSpan(0, lineLen);
            if (sizeSpan.EndsWith("\r\n"u8))
                sizeSpan = sizeSpan[..^2];
            var semi = sizeSpan.IndexOf((byte)';');
            if (semi >= 0)
                sizeSpan = sizeSpan[..semi];
            if (!Utf8Parser.TryParse(TrimAscii(sizeSpan), out long chunkSize, out _, 'X'))
                throw new IOException("Invalid chunk size.");
            if (chunkSize == 0)
            {
                // trailers + final CRLF
                while (true)
                {
                    var trailerLen = await ReadLineIntoAsync(from, buffer, cancellationToken);
                    await to.WriteAsync(buffer.AsMemory(0, trailerLen), cancellationToken);
                    if (trailerLen == 2 && buffer[0] == (byte)'\r' && buffer[1] == (byte)'\n')
                        return;
                }
            }

            await CopyFixedAsync(from, to, buffer, chunkSize + 2, cancellationToken);
        }
    }

    private static async Task<int> ReadLineIntoAsync(PrefixStream stream, byte[] buffer,
        CancellationToken cancellationToken)
    {
        var filled = 0;
        while (filled < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(filled, 1), cancellationToken);
            if (read == 0)
                throw new IOException("EOF while reading a chunk line.");
            filled += read;
            if (filled >= 2 && buffer[filled - 2] == (byte)'\r' && buffer[filled - 1] == (byte)'\n')
                return filled;
        }

        throw new IOException("Chunk line exceeded the relay buffer.");
    }

    public void Dispose()
    {
        cts.Cancel();
        try
        {
            listener.Dispose();
        }
        catch (ObjectDisposedException)
        {
        }

        Task[] pending;
        lock (clientsGate)
            pending = clients.ToArray();
        try
        {
            Task.WaitAll(pending, TimeSpan.FromSeconds(2));
        }
        catch (Exception)
        {
            // Best-effort drain on shutdown.
        }

        cts.Dispose();
    }
}
