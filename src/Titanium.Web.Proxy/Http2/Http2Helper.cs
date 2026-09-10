using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Titanium.Web.Proxy.Compression;
using Titanium.Web.Proxy.Diagnostics;
using Titanium.Web.Proxy.EventArguments;
using Titanium.Web.Proxy.Exceptions;
using Titanium.Web.Proxy.Extensions;
using Titanium.Web.Proxy.Helpers;
using Titanium.Web.Proxy.Http;
using Titanium.Web.Proxy.Http2.Hpack;
using Titanium.Web.Proxy.Logging;
using Titanium.Web.Proxy.Models;
using Titanium.Web.Proxy.Network.Streams;
using Titanium.Web.Proxy.Network.Tcp;
using Titanium.Web.Proxy.Options;
using Decoder = Titanium.Web.Proxy.Http2.Hpack.Decoder;
using Encoder = Titanium.Web.Proxy.Http2.Hpack.Encoder;

namespace Titanium.Web.Proxy.Http2
{
    /// <summary>
    ///     Thrown when a decoded HTTP/2 header block exceeds the local policy limit
    ///     (<see cref="ProxyServer.MaxDecodedHeaderListBytes"/>). The caller should send
    ///     RST_STREAM with error code ENHANCE_YOUR_CALM (0xb) rather than a connection-level
    ///     COMPRESSION_ERROR, since the header block was structurally valid HPACK.
    /// </summary>
    internal sealed class Http2HeaderListTooLargeException : IOException
    {
        internal Http2HeaderListTooLargeException(string message) : base(message) { }
    }

    internal partial class Http2Helper
    {
        public static readonly byte[] ConnectionPreface = Encoding.ASCII.GetBytes("PRI * HTTP/2.0\r\n\r\nSM\r\n\r\n");

        private static readonly byte[] ConnectMethodBytes = "CONNECT"u8.ToArray();
        private const string SyntheticResponseFailedMessage = "HTTP/2 synthetic response failed";

        /// <summary>
        ///     Connection-level WINDOW_UPDATE increment matching Chrome/Edge (0xEF0001). Grows the peer's
        ///     connection send window from the RFC default 65535 to ~15 MB. Without this, multiplexed large
        ///     responses (e.g. Instagram CDN JS) share a 64 KiB connection window and crawl until credit is
        ///     drip-fed back one DATA frame at a time. <see cref="Http2OriginConnection"/> already sends this
        ///     on the bridge path; the H2↔H2 MITM relay must do the same after writing the client preface.
        /// </summary>
        internal const int InitialConnectionWindowIncrement = 15663105;

        /// <summary>
        ///     768 KiB stream receive window advertised to the HTTP/2 client via SETTINGS_INITIAL_WINDOW_SIZE
        ///     (768 KiB). RFC default 65535 is one byte short of a 64 KiB POST and serializes concurrent uploads.
        /// </summary>
        internal const int ClientInitialStreamWindowSize = 768 * 1024;

        /// <summary>
        ///     1 MiB connection receive window for the HTTP/2 client. Sent as a connection-level
        ///     WINDOW_UPDATE increment of <see cref="ClientConnectionWindowIncrement"/>.
        /// </summary>
        internal const int ClientInitialConnectionWindowSize = 1024 * 1024;

        /// <summary>
        ///     Connection WINDOW_UPDATE increment toward the client: 1 MiB − RFC default 65535.
        /// </summary>
        internal const int ClientConnectionWindowIncrement =
            ClientInitialConnectionWindowSize - Http2FlowController.InitialConnectionWindow;

        /// <summary>
        ///     Batch threshold for receive-side WINDOW_UPDATE (half of <see cref="ClientInitialStreamWindowSize"/>),
        ///     matching a batched half-window credit strategy so credit is not drip-fed under the write lock.
        /// </summary>
        internal const int ReceiveCreditBatchThreshold = ClientInitialStreamWindowSize / 2;

        /// <summary>
        ///     Writes initial client SETTINGS (ENABLE_PUSH=0) and a Chrome-sized connection WINDOW_UPDATE onto
        ///     a proxy-owned origin stream that has just received the HTTP/2 connection preface
        ///     (<see cref="Http2OriginConnection"/> / protocol bridges). The H2↔H2 MITM path must not call
        ///     this: it relays the browser's SETTINGS as the first frame after the preface (RFC 7540 §3.5),
        ///     then appends the same WINDOW_UPDATE from <see cref="SendHttp2"/> so an extra proxy SETTINGS
        ///     ACK is never forwarded to the client.
        /// </summary>
        /// <param name="originStream">Origin HTTP/2 stream (preface already written).</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        internal static async Task SendHttp2ClientConnectionStartupAsync(Stream originStream,
            CancellationToken cancellationToken)
        {
            var frameHeader = new Http2FrameHeader();
            var frameHeaderBuffer = new byte[9];

            // SETTINGS: ENABLE_PUSH=0 + HEADER_TABLE_SIZE=0 (static-table-only for compressed relay /
            // origin connections the proxy owns) + the 768 KiB INITIAL_WINDOW_SIZE. The stream
            // window must be at least ReceiveCreditBatchThreshold or the batched receive-credit grants
            // never flush and a >64 KiB response body deadlocks on the RFC-default 65,535 window.
            frameHeader.StreamId = 0;
            frameHeader.Type = Http2FrameType.Settings;
            frameHeader.Flags = 0;
            frameHeader.Length = 18;
            frameHeader.CopyToBuffer(frameHeaderBuffer);

            var settingsPayload = new byte[18];
            BinaryPrimitives.WriteUInt16BigEndian(settingsPayload.AsSpan(0, 2), (ushort)Http2SettingsId.HeaderTableSize);
            BinaryPrimitives.WriteUInt32BigEndian(settingsPayload.AsSpan(2, 4), 0);
            BinaryPrimitives.WriteUInt16BigEndian(settingsPayload.AsSpan(6, 2), (ushort)Http2SettingsId.EnablePush);
            BinaryPrimitives.WriteUInt32BigEndian(settingsPayload.AsSpan(8, 4), 0);
            BinaryPrimitives.WriteUInt16BigEndian(settingsPayload.AsSpan(12, 2),
                (ushort)Http2SettingsId.InitialWindowSize);
            BinaryPrimitives.WriteUInt32BigEndian(settingsPayload.AsSpan(14, 4), ClientInitialStreamWindowSize);

            await originStream.WriteAsync(frameHeaderBuffer, cancellationToken);
            await originStream.WriteAsync(settingsPayload, cancellationToken);

            await SendWindowUpdateAsync(frameHeader, frameHeaderBuffer, 0, InitialConnectionWindowIncrement,
                originStream);
            await originStream.FlushAsync(cancellationToken);
        }

        /// <summary>
        ///     The largest frame payload this proxy will accept from either peer. Neither leg is ever told
        ///     (via a proxy-originated SETTINGS frame) that a larger value is acceptable, so this is the
        ///     value a conformant peer will honor; a peer that ignores it and sends a larger frame anyway is
        ///     treated as a protocol violation (FRAME_SIZE_ERROR) rather than risking an unbounded/undersized
        ///     buffer allocation.
        /// </summary>
        private const int MaxAcceptableFrameSize = 16384;

        /// <summary>
        ///     RFC 7541 §4.2 / the HTTP/2bis clarification of it: regardless of what a peer's
        ///     SETTINGS_HEADER_TABLE_SIZE advertises as the *ceiling* it will allow, both that peer's decoder
        ///     and our own encoder targeting it are defined to start with a dynamic table size of exactly
        ///     4096 bytes - growing (or shrinking) beyond that requires an explicit HPACK Dynamic Table Size
        ///     Update instruction (see <see cref="Hpack.Encoder.SetMaxHeaderTableSize" />) at the start of a
        ///     header block, it is never implied just by the peer having advertised a larger ceiling. A real
        ///     client's SETTINGS_HEADER_TABLE_SIZE is routinely larger than 4096 (e.g. Chrome sends 65536),
        ///     and by the time the first response here is encoded, that SETTINGS frame has typically already
        ///     been parsed into <see cref="Http2Settings.HeaderTableSize" /> - so constructing a brand new
        ///     Encoder with `settings.HeaderTableSize` as its *initial* size (as this used to) makes the
        ///     encoder start already believing it has the full ceiling to itself, with no update instruction
        ///     ever emitted (since the "did the size change?" check below then compares the ceiling against
        ///     itself). The peer's real decoder, having received no such instruction, stays at the spec's
        ///     4096-byte default for the entire connection while our encoder keeps entries alive - and
        ///     computes indices - as if up to 65536 bytes of history were still resolvable. The two
        ///     dynamic tables silently diverge from the very first response, and the *first* indexed
        ///     reference that lands on an entry the real decoder already evicted (or a slot it renumbered
        ///     differently) is decoded as the wrong header or rejected outright - observed as an
        ///     intermittent net::ERR_HTTP2_COMPRESSION_ERROR that gets worse the longer the connection lives
        ///     and the more distinct headers flow over it. Always starting the encoder at the RFC default
        ///     instead means the size-change check on the very first call correctly detects the gap and
        ///     emits the one legitimate Dynamic Table Size Update needed to bring the real decoder up to the
        ///     ceiling in lockstep.
        /// </summary>
        private const int RfcDefaultHeaderTableSize = 4096;


        /// <summary>
        ///     Reports an HTTP/2 protocol/relay failure through the centralized logging gateway. Every
        ///     <c>ProxyHttpException</c> raised anywhere in this class goes through here (the previous
        ///     behavior invoked <c>ExceptionFunc</c> directly at each of the ~30 call sites below).
        ///     Uses <see cref="ProxyDiagnostics.ReportException"/> so peer disconnect / idle teardown
        ///     wrapped as <see cref="IOException"/> (including <c>QuicException</c>) stays Debug-level,
        ///     while genuine protocol violations (typically null-inner <see cref="ProxyHttpException"/>)
        ///     remain Error.
        /// </summary>
        private static void ReportException(ILogger logger, ProxyHttpException ex)
        {
            ProxyDiagnostics.ReportException(logger, ex.Message, ex);
        }

        /// <summary>
        ///     Bind multiplexed origin metadata and attribute
        ///     <see cref="HttpRequestTiming.UpstreamConnectionReused" /> via <see cref="TcpServerConnection.ClaimFirstUse" />
        ///     so the first stream on a connection records fresh, later streams record reused.
        /// </summary>
        private static void BindOriginForHttp2Stream(SessionEventArgs sessionArgs, TcpServerConnection originConnection)
        {
            sessionArgs.HttpClient.BindUpstreamConnection(originConnection);
            var reused = !originConnection.ClaimFirstUse();
            if (sessionArgs.Timing != null)
                sessionArgs.Timing.MarkConnectionReady(originConnection.Id, reused);
        }

        /// <summary>
        ///     Align origin-facing <c>:scheme</c> with the origin transport: cleartext origins expect
        ///     <c>http</c> (TLS-terminate clients often send <c>https</c>); TLS origins expect <c>https</c>
        ///     when the client spoke inbound h2c (<c>http</c>).
        /// </summary>
        private static void ApplyCleartextOriginScheme(Request request, TcpServerConnection? originConnection,
            TcpClientConnection? clientConnection = null)
        {
            if (originConnection is { IsHttps: false })
                request.IsHttps = false;
            else if (originConnection is { IsHttps: true } && clientConnection is { Http2CleartextClient: true })
                request.IsHttps = true;
        }

        private static bool IsAsciiDigit(byte b) => b is >= (byte)'0' and <= (byte)'9';

        /// <summary>
        ///     relays the input clientStream to the server at the specified host name and port with the given httpCmd and headers
        ///     as prefix
        ///     Useful for websocket requests
        ///     Task-based Asynchronous Pattern
        /// </summary>
        /// <returns></returns>
        internal static async Task SendHttp2(Stream clientStream, Stream serverStream, // NOSONAR S107 -- Relay collaborators are explicit to preserve the established internal protocol boundary.
            Func<SessionEventArgs> sessionFactory,
            Func<SessionEventArgs, Http2StreamContext, Task> onBeforeRequest,
            Func<SessionEventArgs, Http2StreamContext, Task> onBeforeResponse,
            Func<SessionEventArgs, Task> onAfterResponse, Action<HeaderCollection> prepareRequestHeaders,
            CancellationTokenSource cancellationTokenSource, long connectionId,
            ILogger logger, int maxDecodedHeaderListBytes = 64 * 1024, bool enableRfc8441 = false,
            ProxyResourceLimits? resourceLimits = null,
            TcpServerConnection? originConnection = null,
            bool httpInterceptionEnabled = true,
            Func<HttpInterceptionContext, bool>? shouldInterceptHttp = null,
            Func<CancellationToken, Task<TcpServerConnection>>? openOriginConnectionAsync = null,
            bool forceStaticHpackForMitmUnchangedRelay = false)
        {
            resourceLimits ??= ProxyResourceLimits.Default;
            var connectionState = new Http2ConnectionState(connectionId, cancellationTokenSource,
                resourceLimits.MaxConcurrentStreamsPerConnection);

            // Dedicated writers (share the direction locks so control-frame paths cannot interleave).
            connectionState.ClientFrameWriter =
                new Http2FrameWriter(clientStream, connectionState.ClientWriteLock);
            connectionState.ServerFrameWriter =
                new Http2FrameWriter(serverStream, connectionState.ServerWriteLock);

            Http2OriginRelayPool? originPool = null;
            // Same-protocol H2↔H2 (not NullOrigin / RFC 8441) can use compressed-relay topology.
            // HEADER_TABLE_SIZE=0 is forced only for gate-off compressed-relay, or for transparent/
            // socks MITM that can finish unchanged streams via compressed relay. Explicit MITM
            // injects Via and re-encodes — keep the peer's table size (Chrome/YouTube HPACK).
            var canCompressedRelayTopology = !enableRfc8441
                && originConnection != null
                && serverStream is not NullOriginStream;
            // Multi-origin pool is for gate-off compressed-relay only. Under true MITM, unchanged
            // streams may relay via the primary leg while mutated streams re-encode through
            // QueueSendHeaderTowardServer onto the same serverStream — enabling the pool would
            // dispose ServerFrameWriter and race those writes with the pool's primary Writer.
            // Tip A/B (MITM MaxOrigin=2 + pool): Lite err%~29 / RSS blow-up — keep disabled.
            var useMultiOrigin = canCompressedRelayTopology
                && !httpInterceptionEnabled
                && openOriginConnectionAsync != null
                && resourceLimits.MaxOriginHttp2ConnectionsPerAuthority > 1;

            if (useMultiOrigin)
            {
                // Primary leg reuses ServerWriteLock / ServerFrameWriter's stream; dispose the
                // connection-level server writer so only the pool's primary Writer owns that direction.
                try { await connectionState.ServerFrameWriter.DisposeAsync(); }
                catch { /* ignore */ }
                connectionState.ServerFrameWriter = null;

                originPool = new Http2OriginRelayPool(originConnection!, openOriginConnectionAsync!,
                    resourceLimits, logger, connectionState.ServerWriteLock);
                connectionState.OriginRelayPool = originPool;
            }

            // Do NOT send connection WINDOW_UPDATE toward the client before the first SETTINGS frame.
            // Strict peers (including .NET HttpClient and strict ASP.NET Core server stacks) treat a non-SETTINGS first frame as a
            // PROTOCOL_ERROR — the same failure mode as InitialOriginWindowUpdateSent toward origins.
            // Client connection credit is sent immediately after ServerSettingsRelayed below.

            var sendRelay =
                CopyHttp2FrameAsync(clientStream, serverStream, connectionState,
                    sessionFactory, onBeforeRequest, onAfterResponse, prepareRequestHeaders, true,
                    cancellationTokenSource.Token, logger, maxDecodedHeaderListBytes, enableRfc8441,
                    resourceLimits, originConnection, httpInterceptionEnabled, shouldInterceptHttp,
                    forceStaticHpackForMitmUnchangedRelay: forceStaticHpackForMitmUnchangedRelay);

            Task receiveRelay;
            if (originPool != null)
            {
                // Each origin leg has its own read loop; remaps origin stream ids back to the client.
                receiveRelay = RunMultiOriginReceiveAsync(clientStream, connectionState, originPool,
                    sessionFactory, onBeforeResponse, onAfterResponse, cancellationTokenSource.Token, logger,
                    maxDecodedHeaderListBytes, enableRfc8441, resourceLimits, httpInterceptionEnabled,
                    shouldInterceptHttp);
            }
            else
            {
                receiveRelay =
                    CopyHttp2FrameAsync(serverStream, clientStream, connectionState,
                        sessionFactory, onBeforeResponse, onAfterResponse, null, false,
                        cancellationTokenSource.Token,
                        logger, maxDecodedHeaderListBytes, enableRfc8441, resourceLimits, originConnection,
                        httpInterceptionEnabled, shouldInterceptHttp,
                        forceStaticHpackForMitmUnchangedRelay: forceStaticHpackForMitmUnchangedRelay);
            }

            await Task.WhenAny(sendRelay, receiveRelay);
            await cancellationTokenSource.CancelAsync();

            await Task.WhenAll(sendRelay, receiveRelay);

            // Drain queued origin and client-bound frame writes so HPACK/socket work is not abandoned mid-frame.
            try { await connectionState.ServerWriteChain; }
            catch { /* relay already faulted / cancelled */ }
            try { await connectionState.ClientWriteChain; }
            catch { /* relay already faulted / cancelled */ }
            if (connectionState.ClientFrameWriter != null)
            {
                try { await connectionState.ClientFrameWriter.DisposeAsync(); }
                catch { /* ignore */ }
            }

            if (connectionState.ServerFrameWriter != null)
            {
                try { await connectionState.ServerFrameWriter.DisposeAsync(); }
                catch { /* ignore */ }
            }

            if (originPool != null)
            {
                try { await originPool.DisposeAsync(); }
                catch { /* ignore */ }
            }

            // Both relay directions have stopped (client/server disconnect, cancellation, or an
            // unrecoverable protocol error); any stream that never reached a normal end-stream/RST_STREAM
            // completion (e.g. the connection was torn down mid-request) must still get exactly one
            // AfterResponse + Dispose, matching HTTP/1.x's `finally { OnAfterResponse(args); args.Dispose(); }`
            // for every session regardless of how it ended.
            foreach (var leftover in connectionState.Streams.Values)
            {
                // Same rationale as the RST_STREAM case above: a stream still in-flight when the whole
                // connection tears down never got a response, so record that as Exception before finalizing.
                if (leftover.SessionArgs is { } leftoverArgs
                    && leftoverArgs.Exception == null
                    && !leftoverArgs.HttpClient.Response.Locked)
                {
                    leftoverArgs.Exception = new OperationCanceledException(
                        "The HTTP/2 connection was closed before this stream received a response.");
                }

                ScheduleFinalize(leftover, onAfterResponse, logger, connectionState);
            }
            connectionState.MultipartObservers.Clear();

            if (!connectionState.PendingFinalizations.IsEmpty)
            {
                await connectionState.PendingFinalizations.WhenAllAsync();
            }
        }

        private static async Task RunMultiOriginReceiveAsync( // NOSONAR S107
            Stream clientStream,
            Http2ConnectionState connectionState,
            Http2OriginRelayPool originPool,
            Func<SessionEventArgs> sessionFactory,
            Func<SessionEventArgs, Http2StreamContext, Task> onBeforeResponse,
            Func<SessionEventArgs, Task> onAfterResponse,
            CancellationToken cancellationToken,
            ILogger logger,
            int maxDecodedHeaderListBytes,
            bool enableRfc8441,
            ProxyResourceLimits resourceLimits,
            bool httpInterceptionEnabled,
            Func<HttpInterceptionContext, bool>? shouldInterceptHttp)
        {
            var tasks = new ConcurrentDictionary<Http2OriginRelayPool.OriginLeg, Task>();

            void EnsureLegReceive(Http2OriginRelayPool.OriginLeg leg)
            {
                tasks.GetOrAdd(leg, l => CopyHttp2FrameAsync(l.Stream, clientStream, connectionState,
                    sessionFactory, onBeforeResponse, onAfterResponse, null, false, cancellationToken,
                    logger, maxDecodedHeaderListBytes, enableRfc8441, resourceLimits, null,
                    httpInterceptionEnabled, shouldInterceptHttp, originReceiveLeg: l));
            }

            foreach (var leg in originPool.SnapshotLegs())
                EnsureLegReceive(leg);

            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    foreach (var leg in originPool.SnapshotLegs())
                        EnsureLegReceive(leg);

                    var running = tasks.Values.ToArray();
                    if (running.Length == 0)
                    {
                        await Task.Delay(1, cancellationToken).ConfigureAwait(false);
                        continue;
                    }

                    // Poll so newly opened overflow legs get a receive loop without waiting for an
                    // existing leg to finish (AssignStreamAsync can open legs while primary is busy).
                    var poll = Task.Delay(5, cancellationToken);
                    var completed = await Task.WhenAny(running.Append(poll)).ConfigureAwait(false);
                    if (ReferenceEquals(completed, poll))
                        continue;

                    Http2OriginRelayPool.OriginLeg? completedLeg = null;
                    foreach (var kvp in tasks) // NOSONAR S3267 -- Explicit loop avoids LINQ enumerator allocation on hot path.
                    {
                        if (ReferenceEquals(kvp.Value, completed))
                        {
                            completedLeg = kvp.Key;
                            break;
                        }
                    }

                    if (completedLeg != null)
                        tasks.TryRemove(completedLeg, out _);

                    var primaryEnded = completedLeg != null
                        && ReferenceEquals(completedLeg, originPool.PrimaryLeg);

                    try
                    {
                        await completed.ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        return;
                    }
                    catch (Exception ex) when (!primaryEnded)
                    {
                        logger.LogDebug(ex, "Overflow origin HTTP/2 receive ended");
                    }

                    if (primaryEnded)
                        return;
                }
            }
            finally
            {
                foreach (var t in tasks.Values)
                {
                    try { await t.ConfigureAwait(false); }
                    catch { /* shutting down */ }
                }
            }
        }
    }
}
