using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace Titanium.Plus.ControlPlane;

/// <summary>A parsed control-plane request. Headers are case-insensitive; repeated headers keep the first value.</summary>
internal sealed class ControlHttpRequest
{
    public required string Method { get; init; }

    /// <summary>The raw (still percent-encoded) path without the query.</summary>
    public required string Path { get; init; }

    public string Query { get; init; } = "";

    public required IReadOnlyDictionary<string, string> Headers { get; init; }

    public byte[] Body { get; init; } = [];

    public IPAddress? RemoteAddress { get; init; }

    public string? Header(string name) => Headers.TryGetValue(name, out var value) ? value : null;

    public string BodyText() => Encoding.UTF8.GetString(Body);

    /// <summary>The first value of a query-string parameter, percent-decoded.</summary>
    public string? QueryValue(string name)
    {
        foreach (var pair in Query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = pair.IndexOf('=');
            var key = Uri.UnescapeDataString((eq < 0 ? pair : pair[..eq]).Replace('+', ' '));
            if (string.Equals(key, name, StringComparison.Ordinal))
            {
                return eq < 0 ? "" : Uri.UnescapeDataString(pair[(eq + 1)..].Replace('+', ' '));
            }
        }

        return null;
    }
}

internal sealed class ControlHttpResponse
{
    public int Status { get; init; } = 200;

    public string ContentType { get; init; } = "text/plain; charset=utf-8";

    public byte[] Body { get; init; } = [];

    public IReadOnlyList<KeyValuePair<string, string>> Headers { get; init; } = [];

    public static ControlHttpResponse Text(int status, string contentType, string body, params KeyValuePair<string, string>[] headers) =>
        new() { Status = status, ContentType = contentType, Body = Encoding.UTF8.GetBytes(body), Headers = headers };
}

/// <summary>
///     A small HTTP/1.1 server (one request per connection) for the control plane and dashboard. It exists so both
///     can sit behind optional TLS from a PEM certificate on every platform: <see cref="HttpListener"/> cannot take a
///     certificate path, and it reports no usable remote address behind a TLS terminator. Requests are bounded:
///     header size, body size, idle time, and concurrent connections.
/// </summary>
internal sealed class ControlHttpServer : IDisposable
{
    public delegate Task<ControlHttpResponse> Handler(ControlHttpRequest request, CancellationToken cancellationToken);

    internal const int MaxHeaderBytes = 32 * 1024;
    internal const int MaxBodyBytes = 8 * 1024 * 1024;
    private const int MaxConnections = 256;
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(30);

    private readonly IPAddress _address;
    private readonly int _port;
    private readonly X509Certificate2? _certificate;
    private readonly Handler _handler;
    private readonly SemaphoreSlim _slots = new(MaxConnections, MaxConnections);
    private TcpListener? _listener;
    private CancellationTokenSource? _cts;

    public ControlHttpServer(IPAddress address, int port, X509Certificate2? certificate, Handler handler)
    {
        _address = address;
        _port = port;
        _certificate = certificate;
        _handler = handler;
    }

    public bool IsTls => _certificate is not null;

    /// <summary>The bound port (useful when the requested port was 0).</summary>
    public int Port => ((IPEndPoint?)_listener?.LocalEndpoint)?.Port ?? _port;

    /// <summary>Binds and starts accepting. Throws <see cref="SocketException"/> when the port is taken.</summary>
    public void Start()
    {
        var listener = new TcpListener(_address, _port);
        listener.Start();
        _listener = listener;
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        _ = Task.Run(() => AcceptLoopAsync(listener, token), token);
    }

    public void Dispose()
    {
        try
        {
            _cts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // already disposed
        }

        try
        {
            _listener?.Stop();
        }
        catch (SocketException)
        {
            // already stopped
        }

        _cts?.Dispose();
        _cts = null;
    }

    /// <summary>Resolves a configured host (<c>127.0.0.1</c>, <c>localhost</c>, <c>0.0.0.0</c>, <c>[::]</c>, a DNS name) to a bind address.</summary>
    public static IPAddress ResolveBindAddress(string host)
    {
        var trimmed = (host ?? "").Trim().Trim('[', ']');
        if (trimmed.Length == 0 || trimmed.Equals("localhost", StringComparison.OrdinalIgnoreCase))
        {
            return IPAddress.Loopback;
        }

        if (trimmed is "*" or "+")
        {
            return IPAddress.Any;
        }

        if (IPAddress.TryParse(trimmed, out var ip))
        {
            return ip;
        }

        var resolved = Dns.GetHostAddresses(trimmed);
        return resolved.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork)
               ?? resolved.FirstOrDefault()
               ?? throw new InvalidOperationException($"Control plane host '{host}' did not resolve to an address.");
    }

    private async Task AcceptLoopAsync(TcpListener listener, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            catch (SocketException)
            {
                continue;
            }

            if (!_slots.Wait(0, cancellationToken))
            {
                client.Dispose();
                continue;
            }

            _ = Task.Run(async () =>
            {
                try
                {
                    await ServeAsync(client, cancellationToken).ConfigureAwait(false);
                }
                finally
                {
                    _slots.Release();
                }
            }, cancellationToken);
        }
    }

    private async Task ServeAsync(TcpClient client, CancellationToken serverToken)
    {
        using (client)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(serverToken);
            timeout.CancelAfter(RequestTimeout);
            var token = timeout.Token;
            Stream stream = client.GetStream();
            try
            {
                if (_certificate is not null)
                {
                    var ssl = new SslStream(stream, leaveInnerStreamOpen: false);
                    stream = ssl;
                    await ssl.AuthenticateAsServerAsync(
                        new SslServerAuthenticationOptions
                        {
                            ServerCertificate = _certificate,
                            ClientCertificateRequired = false,
                            EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                        },
                        token).ConfigureAwait(false);
                }

                var remote = (client.Client.RemoteEndPoint as IPEndPoint)?.Address;
                if (remote is { IsIPv4MappedToIPv6: true })
                {
                    remote = remote.MapToIPv4();
                }

                ControlHttpResponse response;
                try
                {
                    var request = await ReadRequestAsync(stream, remote, token).ConfigureAwait(false);
                    response = request is null
                        ? Plain(400, "bad request")
                        : await _handler(request, token).ConfigureAwait(false);
                }
                catch (RequestTooLargeException)
                {
                    response = Plain(413, "request too large");
                }
                catch (MalformedRequestException)
                {
                    response = Plain(400, "bad request");
                }
                catch (Exception ex) when (ex is not OperationCanceledException and not IOException)
                {
                    response = Plain(500, "internal error");
                }

                await WriteResponseAsync(stream, response, token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or IOException or SocketException
                                           or AuthenticationException or ObjectDisposedException)
            {
                // Peer went away, timed out, or failed the TLS handshake. Nothing to answer.
            }
            finally
            {
                await stream.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private static ControlHttpResponse Plain(int status, string body) =>
        ControlHttpResponse.Text(status, "text/plain; charset=utf-8", body);

    private static async Task<ControlHttpRequest?> ReadRequestAsync(Stream stream, IPAddress? remote, CancellationToken token)
    {
        var buffer = new byte[MaxHeaderBytes];
        var filled = 0;
        var headerEnd = -1;
        while (headerEnd < 0)
        {
            if (filled == buffer.Length)
            {
                throw new RequestTooLargeException();
            }

            var read = await stream.ReadAsync(buffer.AsMemory(filled), token).ConfigureAwait(false);
            if (read == 0)
            {
                return null;
            }

            filled += read;
            headerEnd = IndexOfHeaderEnd(buffer, filled);
        }

        var head = Encoding.Latin1.GetString(buffer, 0, headerEnd);
        var lines = head.Split("\r\n");
        var requestLine = lines[0].Split(' ', 3);
        if (requestLine.Length != 3 || !requestLine[2].StartsWith("HTTP/1.", StringComparison.Ordinal))
        {
            throw new MalformedRequestException();
        }

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 1; i < lines.Length; i++)
        {
            var colon = lines[i].IndexOf(':');
            if (colon <= 0)
            {
                throw new MalformedRequestException();
            }

            headers.TryAdd(lines[i][..colon].Trim(), lines[i][(colon + 1)..].Trim());
        }

        var target = requestLine[1];
        if (target.Contains("://", StringComparison.Ordinal) && Uri.TryCreate(target, UriKind.Absolute, out var absolute))
        {
            target = absolute.PathAndQuery;
        }

        var queryIndex = target.IndexOf('?');
        var path = queryIndex < 0 ? target : target[..queryIndex];
        var query = queryIndex < 0 ? "" : target[(queryIndex + 1)..];
        if (!path.StartsWith('/'))
        {
            throw new MalformedRequestException();
        }

        var leftoverStart = headerEnd + 4;
        var body = await ReadBodyAsync(stream, headers, buffer, leftoverStart, filled, token).ConfigureAwait(false);
        return new ControlHttpRequest
        {
            Method = requestLine[0].ToUpperInvariant(),
            Path = path,
            Query = query,
            Headers = headers,
            Body = body,
            RemoteAddress = remote,
        };
    }

    private static async Task<byte[]> ReadBodyAsync(
        Stream stream,
        Dictionary<string, string> headers,
        byte[] buffer,
        int leftoverStart,
        int filled,
        CancellationToken token)
    {
        var leftover = new ReadOnlyMemory<byte>(buffer, leftoverStart, filled - leftoverStart);
        var chunked = headers.TryGetValue("Transfer-Encoding", out var encoding) &&
                      encoding.Contains("chunked", StringComparison.OrdinalIgnoreCase);

        if (headers.TryGetValue("Expect", out var expect) && expect.Equals("100-continue", StringComparison.OrdinalIgnoreCase))
        {
            await stream.WriteAsync("HTTP/1.1 100 Continue\r\n\r\n"u8.ToArray(), token).ConfigureAwait(false);
        }

        if (chunked)
        {
            return await ReadChunkedAsync(stream, leftover, token).ConfigureAwait(false);
        }

        if (!headers.TryGetValue("Content-Length", out var lengthText))
        {
            return [];
        }

        if (!long.TryParse(lengthText, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var length))
        {
            throw new MalformedRequestException();
        }

        if (length > MaxBodyBytes)
        {
            throw new RequestTooLargeException();
        }

        var body = new byte[length];
        var have = Math.Min(leftover.Length, body.Length);
        leftover.Span[..have].CopyTo(body);
        while (have < body.Length)
        {
            var read = await stream.ReadAsync(body.AsMemory(have), token).ConfigureAwait(false);
            if (read == 0)
            {
                throw new MalformedRequestException();
            }

            have += read;
        }

        return body;
    }

    private static async Task<byte[]> ReadChunkedAsync(Stream stream, ReadOnlyMemory<byte> leftover, CancellationToken token)
    {
        var reader = new PrefixedReader(stream, leftover);
        var body = new MemoryStream();
        while (true)
        {
            var sizeLine = await reader.ReadLineAsync(token).ConfigureAwait(false);
            var semicolon = sizeLine.IndexOf(';');
            var sizeText = (semicolon < 0 ? sizeLine : sizeLine[..semicolon]).Trim();
            if (!int.TryParse(sizeText, System.Globalization.NumberStyles.AllowHexSpecifier, System.Globalization.CultureInfo.InvariantCulture, out var size) || size < 0)
            {
                throw new MalformedRequestException();
            }

            if (size == 0)
            {
                // Trailers (if any) end with an empty line.
                while ((await reader.ReadLineAsync(token).ConfigureAwait(false)).Length > 0)
                {
                }

                return body.ToArray();
            }

            if (body.Length + size > MaxBodyBytes)
            {
                throw new RequestTooLargeException();
            }

            var chunk = new byte[size];
            await reader.ReadExactlyAsync(chunk, token).ConfigureAwait(false);
            body.Write(chunk);
            if ((await reader.ReadLineAsync(token).ConfigureAwait(false)).Length != 0)
            {
                throw new MalformedRequestException();
            }
        }
    }

    private static int IndexOfHeaderEnd(byte[] buffer, int length)
    {
        for (var i = 0; i + 3 < length; i++)
        {
            if (buffer[i] == '\r' && buffer[i + 1] == '\n' && buffer[i + 2] == '\r' && buffer[i + 3] == '\n')
            {
                return i;
            }
        }

        return -1;
    }

    private static async Task WriteResponseAsync(Stream stream, ControlHttpResponse response, CancellationToken token)
    {
        var head = new StringBuilder()
            .Append("HTTP/1.1 ").Append(response.Status).Append(' ').Append(Reason(response.Status)).Append("\r\n")
            .Append("Content-Type: ").Append(response.ContentType).Append("\r\n")
            .Append("Content-Length: ").Append(response.Body.Length).Append("\r\n")
            .Append("Connection: close\r\n");
        // Defaults for every response; a handler that already set one (the dashboard does) is not repeated.
        if (!HasHeader(response, "Cache-Control"))
        {
            head.Append("Cache-Control: no-store\r\n");
        }

        if (!HasHeader(response, "X-Content-Type-Options"))
        {
            head.Append("X-Content-Type-Options: nosniff\r\n");
        }

        foreach (var (name, value) in response.Headers)
        {
            // Header values come from this code, but never let a CR/LF through.
            head.Append(name.Replace('\r', ' ').Replace('\n', ' ')).Append(": ")
                .Append(value.Replace('\r', ' ').Replace('\n', ' ')).Append("\r\n");
        }

        head.Append("\r\n");
        await stream.WriteAsync(Encoding.Latin1.GetBytes(head.ToString()), token).ConfigureAwait(false);
        if (response.Body.Length > 0)
        {
            await stream.WriteAsync(response.Body, token).ConfigureAwait(false);
        }

        await stream.FlushAsync(token).ConfigureAwait(false);
    }

    private static bool HasHeader(ControlHttpResponse response, string name)
    {
        foreach (var (key, _) in response.Headers)
        {
            if (key.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static string Reason(int status) =>
        Enum.IsDefined(typeof(HttpStatusCode), status) ? ((HttpStatusCode)status).ToString() : "Status";

    private sealed class RequestTooLargeException : Exception;

    private sealed class MalformedRequestException : Exception;

    /// <summary>Reads lines and exact byte counts from bytes already buffered, then from the stream.</summary>
    private sealed class PrefixedReader(Stream stream, ReadOnlyMemory<byte> prefix)
    {
        private ReadOnlyMemory<byte> _prefix = prefix;

        public async Task<string> ReadLineAsync(CancellationToken token)
        {
            var line = new StringBuilder();
            var one = new byte[1];
            while (true)
            {
                if (await ReadAsync(one, token).ConfigureAwait(false) == 0)
                {
                    throw new MalformedRequestException();
                }

                if (one[0] == '\n')
                {
                    if (line.Length > 0 && line[^1] == '\r')
                    {
                        line.Length--;
                    }

                    return line.ToString();
                }

                line.Append((char)one[0]);
                if (line.Length > 1024)
                {
                    throw new MalformedRequestException();
                }
            }
        }

        public async Task ReadExactlyAsync(byte[] destination, CancellationToken token)
        {
            var have = 0;
            while (have < destination.Length)
            {
                var read = await ReadAsync(destination.AsMemory(have), token).ConfigureAwait(false);
                if (read == 0)
                {
                    throw new MalformedRequestException();
                }

                have += read;
            }
        }

        private async ValueTask<int> ReadAsync(Memory<byte> destination, CancellationToken token)
        {
            if (_prefix.Length > 0)
            {
                var n = Math.Min(destination.Length, _prefix.Length);
                _prefix[..n].CopyTo(destination);
                _prefix = _prefix[n..];
                return n;
            }

            return await stream.ReadAsync(destination, token).ConfigureAwait(false);
        }
    }
}
