#pragma warning disable CA1416
#pragma warning disable TWP001

using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Quic;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Titanium.Web.Proxy.Http3;
using Titanium.Web.Proxy.Http3.Qpack;
using Titanium.Web.Proxy.IntegrationTests.Setup;

namespace Titanium.Web.Proxy.IntegrationTests.Helpers;

/// <summary>
///     Minimal HTTP/3 origin over <see cref="QuicListener" /> for end-to-end proxy tests.
/// </summary>
internal sealed class QuicHttp3OriginServer : IAsyncDisposable
{
    private readonly X509Certificate2 certificate;
    private readonly QuicListener v6Listener;
    private readonly QuicListener v4Listener;
    private readonly CancellationTokenSource cts = new();
    private Func<QuicHttp3Request, Task<QuicHttp3Response>> handler =
        _ => Task.FromResult(new QuicHttp3Response(200, "ok"));
    private int acceptedConnectionCount;

    public QuicHttp3OriginServer(X509Certificate2 certificate)
    {
        this.certificate = certificate;
        // HttpClient / the proxy resolve "localhost" to ::1 first, while QuicHttp3Client connects
        // to 127.0.0.1. Both families, same port, no wildcard — a wildcard bind prompts the
        // Windows firewall for testhost.exe.
        const int maxAttempts = 20;
        QuicListener? boundV6 = null;
        QuicListener? boundV4 = null;
        for (var attempt = 1; ; attempt++)
        {
            QuicListener? v6 = null;
            try
            {
                v6 = Listen(new IPEndPoint(IPAddress.IPv6Loopback, 0));
                var port = v6.LocalEndPoint.Port;
                boundV4 = Listen(new IPEndPoint(IPAddress.Loopback, port));
                boundV6 = v6;
                break;
            }
            catch (Exception ex) when (attempt < maxAttempts && IsUdpAddressInUse(ex))
            {
                v6?.DisposeAsync().AsTask().GetAwaiter().GetResult();
                boundV4?.DisposeAsync().AsTask().GetAwaiter().GetResult();
                boundV4 = null;
            }
            catch
            {
                v6?.DisposeAsync().AsTask().GetAwaiter().GetResult();
                boundV4?.DisposeAsync().AsTask().GetAwaiter().GetResult();
                throw;
            }
        }

        v6Listener = boundV6 ?? throw new InvalidOperationException("QUIC origin failed to bind ::1.");
        v4Listener = boundV4 ?? throw new InvalidOperationException("QUIC origin failed to bind 127.0.0.1.");
        _ = AcceptLoopAsync(v6Listener);
        _ = AcceptLoopAsync(v4Listener);
    }

    public int Port => v6Listener.LocalEndPoint.Port;

    /// <summary>Local endpoints of the ::1 and 127.0.0.1 listeners, in that order.</summary>
    public IReadOnlyList<IPEndPoint> LocalEndPoints =>
        new[] { v6Listener.LocalEndPoint, v4Listener.LocalEndPoint };

    private QuicListener Listen(IPEndPoint endPoint)
    {
        var options = new QuicListenerOptions
        {
            ListenEndPoint = endPoint,
            ApplicationProtocols = new List<SslApplicationProtocol> { SslApplicationProtocol.Http3 },
            ConnectionOptionsCallback = (_, _, _) => ValueTask.FromResult(new QuicServerConnectionOptions
            {
                DefaultStreamErrorCode = (long)Http3ErrorCode.RequestCancelled,
                DefaultCloseErrorCode = (long)Http3ErrorCode.NoError,
                IdleTimeout = TimeSpan.FromSeconds(30),
                MaxInboundBidirectionalStreams = 100,
                MaxInboundUnidirectionalStreams = 3,
                ServerAuthenticationOptions = new SslServerAuthenticationOptions
                {
                    ServerCertificate = certificate,
                    ApplicationProtocols = new List<SslApplicationProtocol> { SslApplicationProtocol.Http3 }
                }
            })
        };

        return QuicListener.ListenAsync(options).AsTask().GetAwaiter().GetResult();
    }

    private static bool IsUdpAddressInUse(Exception ex)
    {
        for (Exception? cur = ex; cur != null; cur = cur.InnerException)
        {
            if (cur is SocketException se &&
                (se.SocketErrorCode == SocketError.AddressAlreadyInUse
                 || se.NativeErrorCode is 10048 or 98))
                return true;
        }

        return false;
    }

    public int AcceptedConnectionCount => Volatile.Read(ref acceptedConnectionCount);

    public void HandleRequest(Func<QuicHttp3Request, Task<QuicHttp3Response>> requestHandler)
        => handler = requestHandler;

    private async Task AcceptLoopAsync(QuicListener quicListener)
    {
        while (!cts.IsCancellationRequested)
        {
            QuicConnection connection;
            try
            {
                connection = await quicListener.AcceptConnectionAsync(cts.Token);
            }
            catch
            {
                return;
            }

            Interlocked.Increment(ref acceptedConnectionCount);
            _ = Task.Run(() => HandleConnectionAsync(connection));
        }
    }

    private async Task HandleConnectionAsync(QuicConnection connection)
    {
        await using (connection)
        {
            try
            {
                // Server control stream + SETTINGS (required before serving requests).
                await using var control = await connection.OpenOutboundStreamAsync(
                    QuicStreamType.Unidirectional, cts.Token);
                await control.WriteAsync(new byte[] { (byte)Http3StreamType.Control }, cts.Token);
                var settings = new Http3Settings();
                settings.SetQpackMaxTableCapacity(0);
                settings.SetQpackBlockedStreams(0);
                await Http3Frame.WriteAsync(control, Http3FrameType.Settings, settings.Serialize(), cts.Token);

                while (!cts.IsCancellationRequested)
                {
                    var stream = await connection.AcceptInboundStreamAsync(cts.Token);
                    if (stream.Type == QuicStreamType.Unidirectional)
                    {
                        _ = Task.Run(async () =>
                        {
                            await using (stream)
                            {
                                // Drain client control / QPACK streams; protocol requires SETTINGS first
                                // on the client control stream, but this origin does not depend on it.
                                var buf = new byte[4096];
                                while (await stream.ReadAsync(buf, cts.Token) > 0) { }
                            }
                        });
                        continue;
                    }

                    _ = Task.Run(() => HandleRequestStreamAsync(stream));
                }
            }
            catch (OperationCanceledException) { }
            catch (QuicException) { }
        }
    }

    private async Task HandleRequestStreamAsync(QuicStream stream)
    {
        await using (stream)
        {
            try
            {
                var headersFrame = await Http3Frame.ReadAsync(stream, maxPayloadBytes: 64 * 1024, cts.Token);
                if (headersFrame is null || headersFrame.Type != Http3FrameType.Headers)
                    return;

                var decoded = QpackDecoder.Decode(headersFrame.Payload.Span);
                string method = "GET", path = "/", authority = "localhost";
                foreach (var (name, value) in decoded)
                {
                    switch (name)
                    {
                        case ":method": method = value; break;
                        case ":path": path = value; break;
                        case ":authority": authority = value; break;
                    }
                }

                var body = new List<byte>();
                var dataFrameCount = 0;
                while (true)
                {
                    var frame = await Http3Frame.ReadAsync(stream, maxPayloadBytes: 0, cts.Token);
                    if (frame is null) break;
                    if (frame.Type == Http3FrameType.Data)
                    {
                        dataFrameCount++;
                        body.AddRange(frame.Payload.ToArray());
                    }
                    else if (frame.Type == Http3FrameType.Headers)
                        break;
                }

                var response = await handler(new QuicHttp3Request(method, path, authority, body.ToArray(),
                    dataFrameCount));
                var headerList = new List<(string, string)>
                {
                    (":status", response.StatusCode.ToString()),
                    ("content-type", response.ContentType ?? "text/plain")
                };
                if (response.ExtraHeaders != null)
                    headerList.AddRange(response.ExtraHeaders);
                var responseHeaders = QpackEncoder.Encode(headerList);
                await Http3Frame.WriteAsync(stream, Http3FrameType.Headers, responseHeaders, cts.Token);
                if (response.Body is { Length: > 0 })
                {
                    // Optional multi-frame emit for streaming-hook tests; default remains one DATA frame.
                    var frameSize = response.DataFrameSize ?? 0;
                    var chunkSize = frameSize > 0 && frameSize < response.Body.Length
                        ? frameSize
                        : response.Body.Length;
                    for (var offset = 0; offset < response.Body.Length; offset += chunkSize)
                    {
                        var len = Math.Min(chunkSize, response.Body.Length - offset);
                        await Http3Frame.WriteAsync(stream, Http3FrameType.Data,
                            response.Body.AsMemory(offset, len), cts.Token);
                    }
                }
                stream.CompleteWrites();
            }
            catch (OperationCanceledException) { }
            catch (QuicException) { }
            catch (Http3ConnectionException) { }
            catch (Http3StreamException) { }
        }
    }

    public async ValueTask DisposeAsync()
    {
        cts.Cancel();
        try
        {
            await v6Listener.DisposeAsync();
        }
        finally
        {
            await v4Listener.DisposeAsync();
        }

        cts.Dispose();
        certificate.Dispose();
    }
}

/// <summary>
///     Minimal HTTP/3 client over <see cref="QuicConnection" /> for transparent-proxy tests.
/// </summary>
internal sealed class QuicHttp3Client : IAsyncDisposable
{
    private readonly QuicConnection connection;
    private bool controlOpened;

    private QuicHttp3Client(QuicConnection connection) => this.connection = connection;

    public static async Task<QuicHttp3Client> ConnectAsync(
        IPEndPoint remoteEndPoint,
        string sniHost,
        RemoteCertificateValidationCallback? validationCallback = null,
        CancellationToken cancellationToken = default)
    {
        var options = new QuicClientConnectionOptions
        {
            RemoteEndPoint = remoteEndPoint,
            DefaultStreamErrorCode = (long)Http3ErrorCode.RequestCancelled,
            DefaultCloseErrorCode = (long)Http3ErrorCode.NoError,
            MaxInboundBidirectionalStreams = 0,
            MaxInboundUnidirectionalStreams = 3,
            ClientAuthenticationOptions = new SslClientAuthenticationOptions
            {
                ApplicationProtocols = new List<SslApplicationProtocol> { SslApplicationProtocol.Http3 },
                TargetHost = sniHost,
                RemoteCertificateValidationCallback = validationCallback
                    ?? ((_, cert, _, errors) =>
                        cert != null && TestCertificateAuthority.Validate(cert, errors))
            }
        };

        var connection = await QuicConnection.ConnectAsync(options, cancellationToken);
        var client = new QuicHttp3Client(connection);
        client.StartAcceptingPeerStreams();
        await client.OpenControlStreamAsync(cancellationToken);
        return client;
    }

    private QuicStream? controlStream;
    private readonly CancellationTokenSource lifetimeCts = new();

    private void StartAcceptingPeerStreams()
    {
        // Drain proxy→client control / QPACK uni streams so MsQuic flow control does not stall.
        _ = Task.Run(async () =>
        {
            try
            {
                while (!lifetimeCts.IsCancellationRequested)
                {
                    var stream = await connection.AcceptInboundStreamAsync(lifetimeCts.Token);
                    _ = Task.Run(async () =>
                    {
                        await using (stream)
                        {
                            var buf = new byte[4096];
                            while (await stream.ReadAsync(buf, lifetimeCts.Token) > 0) { }
                        }
                    });
                }
            }
            catch
            {
                // Connection closed.
            }
        });
    }

    private async Task OpenControlStreamAsync(CancellationToken cancellationToken)
    {
        if (controlOpened) return;
        controlStream = await connection.OpenOutboundStreamAsync(
            QuicStreamType.Unidirectional, cancellationToken);
        await controlStream.WriteAsync(new byte[] { (byte)Http3StreamType.Control }, cancellationToken);
        var settings = new Http3Settings();
        settings.SetQpackMaxTableCapacity(0);
        settings.SetQpackBlockedStreams(0);
        await Http3Frame.WriteAsync(controlStream, Http3FrameType.Settings, settings.Serialize(), cancellationToken);
        controlOpened = true;
    }

    public async Task<QuicHttp3Response> SendAsync(
        string method,
        string authority,
        string path,
        byte[]? body = null,
        int? requestDataFrameSize = null,
        IReadOnlyList<(string Name, string Value)>? extraRequestHeaders = null,
        CancellationToken cancellationToken = default)
    {
        await using var stream = await connection.OpenOutboundStreamAsync(
            QuicStreamType.Bidirectional, cancellationToken);

        var headers = new List<(string, string)>
        {
            (":method", method),
            (":scheme", "https"),
            (":authority", authority),
            (":path", path)
        };
        if (body is { Length: > 0 })
            headers.Add(("content-length", body.Length.ToString()));
        if (extraRequestHeaders != null)
            headers.AddRange(extraRequestHeaders);

        await Http3Frame.WriteAsync(stream, Http3FrameType.Headers, QpackEncoder.Encode(headers), cancellationToken);
        if (body is { Length: > 0 })
        {
            var frameSize = requestDataFrameSize ?? 0;
            var chunkSize = frameSize > 0 && frameSize < body.Length
                ? frameSize
                : body.Length;
            try
            {
                for (var offset = 0; offset < body.Length; offset += chunkSize)
                {
                    var len = Math.Min(chunkSize, body.Length - offset);
                    await Http3Frame.WriteAsync(stream, Http3FrameType.Data, body.AsMemory(offset, len),
                        cancellationToken);
                }

                stream.CompleteWrites();
            }
            catch (QuicException)
            {
                // Proxy may abort an unread request body after a synthetic BeforeRequest response
                // (H3_REQUEST_CANCELLED). The response may already be readable on this stream.
            }
        }
        else
        {
            stream.CompleteWrites();
        }

        var headersFrame = await Http3Frame.ReadAsync(stream, maxPayloadBytes: 64 * 1024, cancellationToken);
        if (headersFrame is null || headersFrame.Type != Http3FrameType.Headers)
            throw new InvalidOperationException("Expected response HEADERS frame.");

        var decoded = QpackDecoder.Decode(headersFrame.Payload.Span);
        var status = 0;
        foreach (var (name, value) in decoded)
        {
            if (name == ":status" && int.TryParse(value, out var code))
                status = code;
        }

        var responseBody = new List<byte>();
        var responseDataFrames = 0;
        while (true)
        {
            var frame = await Http3Frame.ReadAsync(stream, maxPayloadBytes: 0, cancellationToken);
            if (frame is null) break;
            if (frame.Type == Http3FrameType.Data)
            {
                responseDataFrames++;
                responseBody.AddRange(frame.Payload.ToArray());
            }
            else if (frame.Type == Http3FrameType.Headers)
                break;
        }

        return new QuicHttp3Response(status, Encoding.UTF8.GetString(responseBody.ToArray()),
            responseBody.ToArray(), dataFrameCount: responseDataFrames);
    }

    public async ValueTask DisposeAsync()
    {
        lifetimeCts.Cancel();
        if (controlStream != null)
            await controlStream.DisposeAsync();
        await connection.DisposeAsync();
        lifetimeCts.Dispose();
    }
}

internal readonly record struct QuicHttp3Request(
    string Method, string Path, string Authority, byte[] Body, int DataFrameCount = 0);

internal sealed class QuicHttp3Response
{
    public QuicHttp3Response(int statusCode, string textBody)
        : this(statusCode, textBody, Encoding.UTF8.GetBytes(textBody))
    {
    }

    public QuicHttp3Response(int statusCode, string textBody, byte[] body,
        IReadOnlyList<(string Name, string Value)>? extraHeaders = null, string? contentType = null,
        int? dataFrameSize = null, int dataFrameCount = 0)
    {
        StatusCode = statusCode;
        TextBody = textBody;
        Body = body;
        ExtraHeaders = extraHeaders;
        ContentType = contentType;
        DataFrameSize = dataFrameSize;
        DataFrameCount = dataFrameCount;
    }

    public int StatusCode { get; }
    public string TextBody { get; }
    public byte[] Body { get; }
    public IReadOnlyList<(string Name, string Value)>? ExtraHeaders { get; }
    public string? ContentType { get; }

    /// <summary>When set, the origin writes <see cref="Body"/> as multiple DATA frames of this size.</summary>
    public int? DataFrameSize { get; }

    /// <summary>Number of DATA frames observed when this object is a client-parsed response.</summary>
    public int DataFrameCount { get; }
}

#pragma warning restore TWP001
#pragma warning restore CA1416
