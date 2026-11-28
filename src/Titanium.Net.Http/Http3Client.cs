#pragma warning disable CA1416 // QuicException and QuicStreamType are the BCL names. This client does not start the platform QUIC stack.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Quic;
using System.Net.Security;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Titanium.Http3;
using Titanium.Quic;

namespace Titanium.Net.Http;

/// <summary>HTTP/3 requests on managed QUIC connections kept by <see cref="Http3Pool"/>.</summary>
internal static class Http3Client
{
    private static readonly SslApplicationProtocol H3 = new("h3");

    internal static Task<HttpResponseMessage> SendAsync(
        TitaniumHttpHandler handler,
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        Uri uri = request.RequestUri ?? throw new InvalidOperationException("The request URI is required.");
        return handler.Pool.SendAsync(handler, request, uri, cancellationToken);
    }

    internal static async Task<ManagedQuicConnection> ConnectAsync(
        TitaniumHttpHandler handler,
        Uri uri,
        CancellationToken cancellationToken)
    {
        ManagedQuicConnection connection;
        try
        {
            connection = await ManagedQuicConnection.ConnectAsync(BuildOptions(handler, uri), cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is QuicException or SocketException or IOException)
        {
            throw new Http3ConnectException("The HTTP/3 connection failed before the request was sent.", ex);
        }

        try
        {
            await WritePrefaceAsync(connection, cancellationToken).ConfigureAwait(false);
            return connection;
        }
        catch (Exception ex)
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            if (ex is OperationCanceledException)
            {
                throw;
            }

            if (ex is QuicException or SocketException or IOException)
            {
                throw new Http3ConnectException("The HTTP/3 connection failed before the request was sent.", ex);
            }

            throw;
        }
    }

    internal static async Task<HttpResponseMessage> ExchangeAsync(
        ManagedQuicConnection connection,
        HttpRequestMessage request,
        Uri uri,
        Func<Task> release,
        CancellationToken cancellationToken)
    {
        ManagedQuicStream stream = await connection.OpenOutboundStreamAsync(QuicStreamType.Bidirectional, cancellationToken).ConfigureAwait(false);
        await WriteRequestAsync(stream, request, uri, cancellationToken).ConfigureAwait(false);
        await stream.CompleteWritesAsync(cancellationToken).ConfigureAwait(false);
        HttpResponseMessage response = await ReadResponseAsync(stream, release, cancellationToken).ConfigureAwait(false);
        response.RequestMessage = request;
        response.Version = HttpVersion.Version30;
        return response;
    }

    internal static string Authority(Uri uri) =>
        uri.IsDefaultPort ? uri.IdnHost : uri.IdnHost + ":" + uri.Port.ToString(CultureInfo.InvariantCulture);

    private static ManagedQuicClientConnectionOptions BuildOptions(TitaniumHttpHandler handler, Uri uri)
    {
        IPEndPoint remote = new(Resolve(uri.IdnHost), uri.IsDefaultPort ? 443 : uri.Port);
        ManagedQuicClientConnectionOptions source = handler.QuicOptions ?? new ManagedQuicClientConnectionOptions();
        TimeSpan budget = handler.ConnectTimeout > TimeSpan.Zero && handler.ConnectTimeout != Timeout.InfiniteTimeSpan
            ? handler.ConnectTimeout
            : source.HandshakeTimeout;
        return new ManagedQuicClientConnectionOptions
        {
            RemoteEndPoint = remote,
            LocalEndPoint = source.LocalEndPoint,
            HandshakeTimeout = budget <= TimeSpan.Zero ? TimeSpan.FromSeconds(10) : budget,
            KeepAliveInterval = source.KeepAliveInterval,
            IdleTimeout = source.IdleTimeout,
            MaxInboundBidirectionalStreams = source.MaxInboundBidirectionalStreams,
            MaxInboundUnidirectionalStreams = source.MaxInboundUnidirectionalStreams,
            DefaultStreamErrorCode = source.DefaultStreamErrorCode,
            AllowManagedStackUnderFips = handler.AllowManagedStackUnderFips || source.AllowManagedStackUnderFips,
            ClientAuthenticationOptions = CopySsl(handler.SslOptions, uri.IdnHost),
        };
    }

    private static SslClientAuthenticationOptions CopySsl(SslClientAuthenticationOptions source, string host)
    {
        string target = string.IsNullOrEmpty(source.TargetHost) ? host : source.TargetHost;
        return new SslClientAuthenticationOptions
        {
            TargetHost = target,
            ClientCertificates = source.ClientCertificates,
            CertificateRevocationCheckMode = source.CertificateRevocationCheckMode,
            CertificateChainPolicy = source.CertificateChainPolicy,
            EncryptionPolicy = source.EncryptionPolicy,
            EnabledSslProtocols = source.EnabledSslProtocols,
            RemoteCertificateValidationCallback = source.RemoteCertificateValidationCallback,
            LocalCertificateSelectionCallback = source.LocalCertificateSelectionCallback,
            ClientCertificateContext = source.ClientCertificateContext,
            CipherSuitesPolicy = source.CipherSuitesPolicy,
            ApplicationProtocols = new List<SslApplicationProtocol> { H3 },
        };
    }

    private static IPAddress Resolve(string host)
    {
        if (IPAddress.TryParse(host, out IPAddress? parsed))
        {
            return parsed;
        }

        IPAddress[] addresses = Dns.GetHostAddresses(host);
        for (int i = 0; i < addresses.Length; i++)
        {
            if (addresses[i].AddressFamily == AddressFamily.InterNetwork)
            {
                return addresses[i];
            }
        }

        if (addresses.Length == 0)
        {
            throw new Http3ConnectException("The HTTP/3 host did not resolve.", new SocketException((int)SocketError.HostNotFound));
        }

        return addresses[0];
    }

    private static async Task WritePrefaceAsync(ManagedQuicConnection connection, CancellationToken cancellationToken)
    {
        await WriteStreamTypeAsync(connection, Http3StreamType.Control, EmptySettings(), cancellationToken).ConfigureAwait(false);
        await WriteStreamTypeAsync(connection, Http3StreamType.QpackEncoder, ReadOnlyMemory<byte>.Empty, cancellationToken).ConfigureAwait(false);
        await WriteStreamTypeAsync(connection, Http3StreamType.QpackDecoder, ReadOnlyMemory<byte>.Empty, cancellationToken).ConfigureAwait(false);
    }

    private static ReadOnlyMemory<byte> EmptySettings()
    {
        byte[] payload = new Http3Settings().Serialize();
        byte[] frame = new byte[payload.Length + 16];
        int written = Http3FrameCodec.Write(frame, Http3FrameType.Settings, payload);
        return frame.AsMemory(0, written);
    }

    private static async Task WriteStreamTypeAsync(
        ManagedQuicConnection connection,
        ulong streamType,
        ReadOnlyMemory<byte> rest,
        CancellationToken cancellationToken)
    {
        ManagedQuicStream stream = await connection.OpenOutboundStreamAsync(QuicStreamType.Unidirectional, cancellationToken).ConfigureAwait(false);
        byte[] prefix = new byte[8];
        int length = Http3VarInt.Write(prefix, streamType);
        await stream.WriteAsync(prefix.AsMemory(0, length), cancellationToken).ConfigureAwait(false);
        if (!rest.IsEmpty)
        {
            await stream.WriteAsync(rest, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task WriteRequestAsync(ManagedQuicStream stream, HttpRequestMessage request, Uri uri, CancellationToken cancellationToken)
    {
        var fields = new List<(string Name, string Value)>
        {
            (":method", request.Method.Method),
            (":scheme", uri.Scheme),
            (":authority", Authority(uri)),
            (":path", uri.PathAndQuery.Length == 0 ? "/" : uri.PathAndQuery),
        };
        AddHeaders(fields, request.Headers);
        if (request.Content is not null)
        {
            AddHeaders(fields, request.Content.Headers);
        }

        byte[] block = QpackEncoder.Encode(fields);
        await WriteFrameAsync(stream, Http3FrameType.Headers, block, cancellationToken).ConfigureAwait(false);
        if (request.Content is null)
        {
            return;
        }

        await using Stream input = await request.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        byte[] chunk = new byte[Http3FrameCodec.RelayUnitBytes];
        while (true)
        {
            int count = await input.ReadAsync(chunk, cancellationToken).ConfigureAwait(false);
            if (count == 0)
            {
                return;
            }

            await WriteFrameAsync(stream, Http3FrameType.Data, chunk.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task<HttpResponseMessage> ReadResponseAsync(
        ManagedQuicStream stream,
        Func<Task> release,
        CancellationToken cancellationToken)
    {
        var response = new HttpResponseMessage();
        while (await ReadFrameAsync(stream, cancellationToken).ConfigureAwait(false) is Frame frame)
        {
            if (frame.Type == Http3FrameType.Data)
            {
                throw new HttpRequestException("The HTTP/3 response sent a body before headers.");
            }

            if (frame.Type != Http3FrameType.Headers)
            {
                continue;
            }

            List<(string Name, string Value)> fields = QpackDecoder.Decode(frame.Payload);
            response.Content = new StreamContent(new Http3ResponseBody(stream, response, release));
            foreach ((string name, string value) in fields)
            {
                if (string.Equals(name, ":status", StringComparison.Ordinal))
                {
                    response.StatusCode = (HttpStatusCode)int.Parse(value, CultureInfo.InvariantCulture);
                }
            }

            AddFields(response.Headers, response.Content.Headers, fields);
            return response;
        }

        throw new HttpRequestException("The HTTP/3 response had no headers.");
    }

    private static void AddFields(HttpHeaders target, HttpHeaders? contentHeaders, List<(string Name, string Value)> fields)
    {
        foreach ((string name, string value) in fields)
        {
            if (name.Length == 0 || name[0] == ':')
            {
                continue;
            }

            if (!target.TryAddWithoutValidation(name, value) && contentHeaders is not null)
            {
                contentHeaders.TryAddWithoutValidation(name, value);
            }
        }
    }

    private static async Task<Frame?> ReadFrameAsync(Stream stream, CancellationToken cancellationToken)
    {
        ulong? type = await Http3VarInt.ReadAsync(stream, cancellationToken).ConfigureAwait(false);
        if (type is null)
        {
            return null;
        }

        ulong? length = await Http3VarInt.ReadAsync(stream, cancellationToken).ConfigureAwait(false);
        if (length is null || length.Value > (ulong)Http3FrameCodec.DefaultMaxPayloadBytes)
        {
            throw new HttpRequestException("The HTTP/3 frame ended early.");
        }

        byte[] payload = new byte[(int)length.Value];
        int read = 0;
        while (read < payload.Length)
        {
            int count = await stream.ReadAsync(payload.AsMemory(read), cancellationToken).ConfigureAwait(false);
            if (count == 0)
            {
                throw new HttpRequestException("The HTTP/3 frame ended early.");
            }

            read += count;
        }

        return new Frame(type.Value, payload);
    }

    private static Task WriteFrameAsync(Stream stream, ulong type, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken)
    {
        byte[] frame = new byte[payload.Length + 16];
        int written = Http3FrameCodec.Write(frame, type, payload.Span);
        return stream.WriteAsync(frame.AsMemory(0, written), cancellationToken).AsTask();
    }

    private static void AddHeaders(List<(string Name, string Value)> fields, HttpHeaders headers)
    {
        foreach (KeyValuePair<string, IEnumerable<string>> header in headers)
        {
            if (IsConnectionHeader(header.Key))
            {
                continue;
            }

            foreach (string value in header.Value)
            {
                fields.Add((header.Key.ToLowerInvariant(), value));
            }
        }
    }

    private static bool IsConnectionHeader(string name) =>
        name.Equals("Host", StringComparison.OrdinalIgnoreCase)
        || name.Equals("Connection", StringComparison.OrdinalIgnoreCase)
        || name.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase)
        || name.Equals("Upgrade", StringComparison.OrdinalIgnoreCase)
        || name.Equals("Keep-Alive", StringComparison.OrdinalIgnoreCase)
        || name.Equals("Proxy-Connection", StringComparison.OrdinalIgnoreCase);

    private readonly struct Frame
    {
        internal Frame(ulong type, byte[] payload)
        {
            Type = type;
            Payload = payload;
        }

        internal ulong Type { get; }

        internal byte[] Payload { get; }
    }

    private sealed class Http3ResponseBody : Stream
    {
        private readonly ManagedQuicStream _stream;
        private readonly HttpResponseMessage _response;
        private readonly Func<Task> _release;
        private byte[]? _pending;
        private int _offset;
        private int _finished;

        internal Http3ResponseBody(ManagedQuicStream stream, HttpResponseMessage response, Func<Task> release)
        {
            _stream = stream;
            _response = response;
            _release = release;
        }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            ReadAsync(buffer.AsMemory(offset, count), CancellationToken.None).AsTask().GetAwaiter().GetResult();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            while (true)
            {
                if (_pending is not null && _offset < _pending.Length)
                {
                    int count = Math.Min(buffer.Length, _pending.Length - _offset);
                    _pending.AsSpan(_offset, count).CopyTo(buffer.Span);
                    _offset += count;
                    return count;
                }

                if (_finished != 0)
                {
                    return 0;
                }

                Frame? frame = await ReadFrameAsync(_stream, cancellationToken).ConfigureAwait(false);
                if (frame is null)
                {
                    await FinishAsync().ConfigureAwait(false);
                    return 0;
                }

                if (frame.Value.Type == Http3FrameType.Headers)
                {
                    AddFields(_response.TrailingHeaders, null, QpackDecoder.Decode(frame.Value.Payload));
                    continue;
                }

                if (frame.Value.Type == Http3FrameType.Data && frame.Value.Payload.Length > 0)
                {
                    _pending = frame.Value.Payload;
                    _offset = 0;
                }
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                FinishAsync().GetAwaiter().GetResult();
            }

            base.Dispose(disposing);
        }

        private Task FinishAsync()
        {
            if (Interlocked.Exchange(ref _finished, 1) != 0)
            {
                return Task.CompletedTask;
            }

            return _release();
        }
    }
}

/// <summary>An HTTP/3 connect failed before a request byte was written.</summary>
internal sealed class Http3ConnectException : HttpRequestException
{
    internal Http3ConnectException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
