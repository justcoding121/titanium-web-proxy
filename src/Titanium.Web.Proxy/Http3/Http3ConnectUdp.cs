#pragma warning disable CA1416
using System;
using System.IO;
using System.Net.Quic;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Titanium.Web.Proxy.EventArguments;
using Titanium.Web.Proxy.Http;
using Titanium.Web.Proxy.Http3.Qpack;
using Titanium.Web.Proxy.Masque;
using Titanium.Web.Proxy.Models;

namespace Titanium.Web.Proxy.Http3;

internal static partial class Http3RequestStream
{
    /// <summary>
    ///     CONNECT-UDP capsules on the HTTP/3 request stream. Datagrams share this stream with
    ///     the request, so a large body blocks the next capsule (head-of-line). There is no
    ///     SETTINGS_H3_DATAGRAM on this path.
    /// </summary>
    private static async Task RunHttp3ConnectUdpAsync(QuicStream stream, SessionEventArgs session,
        QpackContext? qpackContext, CancellationToken cancellationToken)
    {
        var server = session.Server;
        var admission = await server.TryOpenConnectUdpAsync(session, cancellationToken).ConfigureAwait(false);
        if (admission is null)
        {
            stream.Abort(QuicAbortDirection.Read, (long)Http3ErrorCode.RequestCancelled);
            await SendResponseAsync(stream, session.HttpClient.Response, qpackContext, cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        var inbound = Channel.CreateBounded<ReadOnlyMemory<byte>>(new BoundedChannelOptions(256)
        {
            SingleReader = true,
            SingleWriter = true,
            FullMode = BoundedChannelFullMode.Wait,
        });

        var pump = PumpRequestCapsulesAsync(stream, inbound.Writer, server, session, cancellationToken);
        var response = new Response
        {
            HttpVersion = HttpHeader.Version30,
            StatusCode = 200,
            StatusDescription = "OK",
        };
        response.Headers.AddHeader("capsule-protocol", "?1");
        session.RespondStreaming(response, async (body, token) =>
        {
            try
            {
                await MasqueTunnel.RelayAsync(admission.Host, inbound.Reader, new FlushingWriteStream(body),
                    admission.Open.Socket, server.Logger, token).ConfigureAwait(false);
            }
            finally
            {
                inbound.Writer.TryComplete();
                admission.Release();
            }
        });

        try
        {
            await SendResponseAsync(stream, session.HttpClient.Response, qpackContext, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            inbound.Writer.TryComplete();
            admission.Release();
            try
            {
                await pump.ConfigureAwait(false);
            }
            catch (Exception)
            {
                // The request stream closed while the relay was finishing.
            }
        }
    }

    private static async Task PumpRequestCapsulesAsync(QuicStream stream,
        ChannelWriter<ReadOnlyMemory<byte>> writer, ProxyServer server, SessionEventArgs session,
        CancellationToken cancellationToken)
    {
        try
        {
            await StreamRequestBodyToWriteAsync(stream, (data, token) =>
            {
                var copy = data.ToArray();
                return writer.WriteAsync(copy, token);
            }, server, session, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            writer.TryComplete();
        }
    }

    /// <summary>Flushes each capsule so the client can read it before the relay ends.</summary>
    private sealed class FlushingWriteStream : Stream
    {
        private readonly Stream inner;

        internal FlushingWriteStream(Stream inner) => this.inner = inner;

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush() => inner.Flush();

        public override Task FlushAsync(CancellationToken cancellationToken) =>
            inner.FlushAsync(cancellationToken);

        public override int Read(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();

        public override void SetLength(long value) =>
            throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            await inner.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
            await inner.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
    }
}
