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
    internal partial class Http2Helper
    {
        // Gate-off same-protocol path: keep HPACK hpack.Decoder in sync with a no-op listener, then
        // forward the compressed block unchanged (valid when both legs negotiated table size 0).
        // Same-transport / patched scheme + no origin pool: sync enqueue (no async SM).
        // Async only for multi-origin AssignStreamAsync or rare scheme-decode RST/GOAWAY.
        private static void EnqueueRelayedHeaderBlock(
        Http2ConnectionState connectionState,
        bool isClient,
        Http2Settings remoteSettings,
        int wireStreamId, ReadOnlyMemory<byte> blockToRelay,
        bool endStreamFlag, byte[]? appendSuffix, Http2FrameWriter? dedicatedWriter,
        SemaphoreSlim writeLock, Stream writeStream)
        {
            var relayFrameHeader = new Http2FrameHeader { StreamId = wireStreamId };
            var appendMemory = appendSuffix == null ? ReadOnlyMemory<byte>.Empty : appendSuffix.AsMemory();
            // Header bytes are written straight into the rented frame; no shared 9-byte scratch.
            var framed = RentFramedHeaderBlock(relayFrameHeader, Array.Empty<byte>(), wireStreamId,
                Http2FrameType.Headers, endStreamFlag, hasPriority: false, blockToRelay, appendMemory,
                remoteSettings.MaxFrameSize);
            if (dedicatedWriter != null)
                dedicatedWriter.EnqueueRented(framed.Array!, framed.Count);
            else
                connectionState.EnqueueWriteRented(isClient, writeLock, writeStream,
                    framed.Array!, framed.Count);
        }

        private static Task RelayCompressedHeaderBlockAsync(
        Http2ConnectionState connectionState,
        Stream input,
        Stream output,
        SemaphoreSlim outputWriteLock,
        SemaphoreSlim ownLegWriteLock,
        Http2OriginRelayPool.OriginLeg? originReceiveLeg,
        ByteString compressedRelaySchemeOverride,
        bool isClient,
        CancellationToken cancellationToken,
        CopyDirectionHpack hpack,
        Http2Settings remoteSettings,
        int maxDecodedHeaderListBytes,
        ILogger logger,
        Action<int> removeAndFinalizeStream,
        int hbStreamId, byte[] compressed, bool endStreamFlag,
        byte[]? appendSuffix = null)
        {
            // Mixed-transport: prefer a structural HPACK walk that only rewrites Indexed
            // :scheme (0x86↔0x87) — no Decoder, no HeaderCollection. .NET HttpClient and most
            // browsers emit static-indexed :scheme; decode+re-encode is the rare fallback.
            // Same-transport relay stays verbatim (no override).
            ReadOnlyMemory<byte> blockToRelay = compressed;
            if (compressedRelaySchemeOverride.Length > 0)
            {
                switch (TryApplyStaticIndexedSchemeOverride(compressed, compressedRelaySchemeOverride,
                            out var patchedFast))
                {
                    case StaticSchemeOverrideResult.Patched:
                        blockToRelay = patchedFast;
                        break;
                    case StaticSchemeOverrideResult.AlreadyMatching:
                        break;
                    default:
                        return RelayCompressedWithSchemeDecodeAsync(
        connectionState, input, output, outputWriteLock, ownLegWriteLock, originReceiveLeg,
        compressedRelaySchemeOverride, isClient, cancellationToken, hpack, remoteSettings,
        maxDecodedHeaderListBytes, logger, removeAndFinalizeStream,
                            hbStreamId, compressed, endStreamFlag, appendSuffix);
                }
            }

            if (isClient && connectionState.OriginRelayPool != null)
                return RelayCompressedWithOriginPoolAsync(
                connectionState, isClient, remoteSettings, cancellationToken,
                hbStreamId, blockToRelay, endStreamFlag, appendSuffix);

            Http2FrameWriter? dedicatedWriter = null;
            var writeLock = outputWriteLock;
            var writeStream = output;
            if (!isClient && originReceiveLeg != null)
            {
                // Origin → client: hbStreamId is already remapped to the client stream id by the caller.
                dedicatedWriter = connectionState.ClientFrameWriter;
            }

            EnqueueRelayedHeaderBlock(connectionState, isClient, remoteSettings,
                hbStreamId, blockToRelay, endStreamFlag, appendSuffix,
                dedicatedWriter, writeLock, writeStream);
            return Task.CompletedTask;
        }

        private static async Task RelayCompressedWithOriginPoolAsync(
        Http2ConnectionState connectionState,
        bool isClient,
        Http2Settings remoteSettings,
        CancellationToken cancellationToken,
        int hbStreamId, ReadOnlyMemory<byte> blockToRelay,
        bool endStreamFlag, byte[]? appendSuffix)
        {
            var assignment = await connectionState.OriginRelayPool!
                .AssignStreamAsync(hbStreamId, cancellationToken).ConfigureAwait(false);
            EnqueueRelayedHeaderBlock(connectionState, isClient, remoteSettings,
                assignment.OriginStreamId, blockToRelay, endStreamFlag, appendSuffix,
                assignment.Leg.Writer, assignment.Leg.WriteLock, assignment.Leg.Stream);
        }

        private static async Task RelayCompressedWithSchemeDecodeAsync(
        Http2ConnectionState connectionState,
        Stream input,
        Stream output,
        SemaphoreSlim outputWriteLock,
        SemaphoreSlim ownLegWriteLock,
        Http2OriginRelayPool.OriginLeg? originReceiveLeg,
        ByteString compressedRelaySchemeOverride,
        bool isClient,
        CancellationToken cancellationToken,
        CopyDirectionHpack hpack,
        Http2Settings remoteSettings,
        int maxDecodedHeaderListBytes,
        ILogger logger,
        Action<int> removeAndFinalizeStream,
        int hbStreamId, byte[] compressed, bool endStreamFlag,
        byte[]? appendSuffix)
        {
            var overrideHeaders = new HeaderCollection();
            var overrideListener = new MyHeaderListener(
                (name, value) => overrideHeaders.AddHeader(new HttpHeader(name, value)),
                isRequest: true);
            try
            {
                if (hpack.Decoder == null)
                {
                    hpack.HeaderTableSize = remoteSettings.HeaderTableSize;
                    hpack.Decoder = new Decoder(maxDecodedHeaderListBytes, hpack.HeaderTableSize);
                }
                else if (hpack.HeaderTableSize != remoteSettings.HeaderTableSize)
                {
                    hpack.HeaderTableSize = remoteSettings.HeaderTableSize;
                    hpack.Decoder.SetMaxHeaderTableSize(hpack.HeaderTableSize);
                }

                hpack.Decoder.Decode(compressed.AsSpan(0, compressed.Length), overrideListener);
                if (hpack.Decoder.EndHeaderBlock())
                {
                    ReportException(logger, new ProxyHttpException(
                        "HTTP/2 header list too large on compressed-relay stream.", null, null));
                    removeAndFinalizeStream(hbStreamId);
                    await LockedWriteAsync(ownLegWriteLock, cancellationToken, () => SendRstStreamAsync(new Http2FrameHeader(),
                        new byte[9], hbStreamId, (Http2ErrorCode)0xb /* ENHANCE_YOUR_CALM */,
                        input));
                    return;
                }
            }
            catch (Exception ex)
            {
                ReportException(logger, new ProxyHttpException(
                    "Failed to decode HTTP/2 headers on compressed-relay stream", ex, null));
                await LockedWriteAsync(ownLegWriteLock, cancellationToken, () => SendGoAwayAsync(new Http2FrameHeader(), new byte[9],
                    hbStreamId, Http2ErrorCode.CompressionError, input));
                throw;
            }

            ReadOnlyMemory<byte> blockToRelay = compressed;
            // Trailers / CONNECT (no :scheme) and already-matching schemes stay verbatim.
            if (!overrideListener.HasMalformedHeader
                && overrideListener.RawScheme.Length > 0
                && !overrideListener.RawScheme.Equals(compressedRelaySchemeOverride))
            {
                if (TryPatchStaticIndexedScheme(compressed, overrideListener.RawScheme,
                        compressedRelaySchemeOverride, out var patched))
                    blockToRelay = patched;
                else
                    blockToRelay = ReencodeCompressedRequestBlock(remoteSettings,
                        overrideListener, overrideHeaders, compressedRelaySchemeOverride);
            }

            if (isClient && connectionState.OriginRelayPool != null)
            {
                await RelayCompressedWithOriginPoolAsync(
                    connectionState, isClient, remoteSettings, cancellationToken,
                    hbStreamId, blockToRelay, endStreamFlag, appendSuffix)
                    .ConfigureAwait(false);
                return;
            }

            Http2FrameWriter? dedicatedWriter = null;
            if (!isClient && originReceiveLeg != null)
                dedicatedWriter = connectionState.ClientFrameWriter;

            EnqueueRelayedHeaderBlock(connectionState, isClient, remoteSettings,
                hbStreamId, blockToRelay, endStreamFlag, appendSuffix,
                dedicatedWriter, outputWriteLock, output);
        }
    }
}
