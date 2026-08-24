#pragma warning disable CA1416
#pragma warning disable TWP001

using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Quic;
using System.Net.Security;
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
    private readonly QuicListener listener;
    private readonly CancellationTokenSource cts = new();
    private Func<QuicHttp3Request, Task<QuicHttp3Response>> handler =
        _ => Task.FromResult(new QuicHttp3Response(200, "ok"));
    private int acceptedConnectionCount;

    public QuicHttp3OriginServer(X509Certificate2 certificate)
    {
        this.certificate = certificate;
        var options = new QuicListenerOptions
        {
            // Dual-stack: QuicConnectionFactory connects via DnsEndPoint("localhost"), which may
            // resolve to ::1 first. An IPv4-only Loopback listener never Accept()s those handshakes
            // and surfaces as a misleading ALPN failure on the client.
            ListenEndPoint = new IPEndPoint(IPAddress.IPv6Any, 0),
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
                    ServerCertificate = this.certificate,
                    ApplicationProtocols = new List<SslApplicationProtocol> { SslApplicationProtocol.Http3 }
                }
            })
        };

        listener = QuicListener.ListenAsync(options).AsTask().GetAwaiter().GetResult();
        _ = AcceptLoopAsync();
    }

    public int Port => listener.LocalEndPoint.Port;

    public int AcceptedConnectionCount => Volatile.Read(ref acceptedConnectionCount);

    public void HandleRequest(Func<QuicHttp3Request, Task<QuicHttp3Response>> requestHandler)
        => handler = requestHandler;

    private async Task AcceptLoopAsync()
    {
        while (!cts.IsCancellationRequested)
        {
            QuicConnection connection;
            try
            {
                connection = await listener.AcceptConnectionAsync(cts.Token);
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
        await listener.DisposeAsync();
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
