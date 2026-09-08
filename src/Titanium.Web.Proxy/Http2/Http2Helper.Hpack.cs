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
        [Conditional("DEBUG")]
        private static void Breakpoint()
        {
            // when this method is called something received which is not yet implemented
        }

        /// <summary>Cheap check avoiding a ToLowerInvariant() allocation for the common already-lowercase case.</summary>
        private static bool HasUpperCaseAscii(ByteString name)
        {
            var span = name.Span;
            for (var i = 0; i < span.Length; i++)
            {
                if (span[i] is >= (byte)'A' and <= (byte)'Z')
                    return true;
            }

            return false;
        }

        /// <summary>
        ///     ASCII lowercase copy for HPACK wire names — no <see cref="string.ToLowerInvariant"/> /
        ///     encoding round-trip (those allocated under origin <c>writeLock</c> on H1→H2).
        /// </summary>
        private static ByteString AsciiToLowerByteString(ByteString name)
        {
            var span = name.Span;
            var buf = new byte[span.Length];
            for (var i = 0; i < span.Length; i++)
            {
                var c = span[i];
                buf[i] = c is >= (byte)'A' and <= (byte)'Z' ? (byte)(c + 32) : c;
            }

            return new ByteString(buf);
        }

        private static readonly ByteString ViaHeaderLower = "via".GetByteString();

        /// <summary>
        ///     Hop-by-hop / connection-specific names RFC 7540 §8.1.2.2 forbids on HTTP/2 (plus Host, which
        ///     becomes :authority). Compared on <see cref="ByteString"/> so EncodeHeaderBlock does not
        ///     force <c>header.Name</c> GetString under writeLock.
        /// </summary>
        private static bool ShouldOmitHttp2Header(ByteString name)
        {
            var span = name.Span;
            return span.Length switch
            {
                2 => EqualsAsciiIgnoreCase(span, "te"u8),
                4 => EqualsAsciiIgnoreCase(span, "host"u8),
                7 => EqualsAsciiIgnoreCase(span, "upgrade"u8),
                10 => EqualsAsciiIgnoreCase(span, "connection"u8)
                      || EqualsAsciiIgnoreCase(span, "keep-alive"u8),
                16 => EqualsAsciiIgnoreCase(span, "proxy-connection"u8),
                17 => EqualsAsciiIgnoreCase(span, "transfer-encoding"u8),
                _ => false
            };
        }

        private static bool EqualsAsciiIgnoreCase(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
        {
            if (a.Length != b.Length) return false;
            for (var i = 0; i < a.Length; i++)
            {
                var x = a[i];
                var y = b[i];
                if (x is >= (byte)'A' and <= (byte)'Z') x = (byte)(x + 32);
                if (y is >= (byte)'A' and <= (byte)'Z') y = (byte)(y + 32);
                if (x != y) return false;
            }

            return true;
        }

        // Common :status values (StaticTable also indexes several of these).
        private static readonly ByteString Status200 = "200".GetByteString();
        private static readonly ByteString Status204 = "204".GetByteString();
        private static readonly ByteString Status206 = "206".GetByteString();
        private static readonly ByteString Status301 = "301".GetByteString();
        private static readonly ByteString Status302 = "302".GetByteString();
        private static readonly ByteString Status304 = "304".GetByteString();
        private static readonly ByteString Status400 = "400".GetByteString();
        private static readonly ByteString Status404 = "404".GetByteString();
        private static readonly ByteString Status500 = "500".GetByteString();
        private static readonly ByteString Status502 = "502".GetByteString();

        private static ByteString StatusCodeBytes(int statusCode) => statusCode switch
        {
            200 => Status200,
            204 => Status204,
            206 => Status206,
            301 => Status301,
            302 => Status302,
            304 => Status304,
            400 => Status400,
            404 => Status404,
            500 => Status500,
            502 => Status502,
            _ => statusCode.ToString().GetByteString()
        };

        // Hot-path caches: avoid allocating ByteString for common :method / :scheme under writeLock.
        private static readonly ByteString MethodGet = "GET".GetByteString();
        private static readonly ByteString MethodHead = "HEAD".GetByteString();
        private static readonly ByteString MethodPost = "POST".GetByteString();
        private static readonly ByteString MethodPut = "PUT".GetByteString();
        private static readonly ByteString MethodDelete = "DELETE".GetByteString();
        private static readonly ByteString MethodOptions = "OPTIONS".GetByteString();
        private static readonly ByteString MethodConnect = "CONNECT".GetByteString();
        private static readonly ByteString SchemeHttps = "https".GetByteString();
        private static readonly ByteString SchemeHttp = "http".GetByteString();

        private static ByteString MethodBytes(string method) => method switch
        {
            "GET" => MethodGet,
            "HEAD" => MethodHead,
            "POST" => MethodPost,
            "PUT" => MethodPut,
            "DELETE" => MethodDelete,
            "OPTIONS" => MethodOptions,
            "CONNECT" => MethodConnect,
            _ => method.GetByteString()
        };

        /// <summary>
        ///     HPACK-encodes <paramref name="rr"/> into the direction's scratch stream. Must run on the
        ///     frame-read loop (or otherwise be serialized) so the dynamic table stays ordered.
        /// </summary>
        private static ReadOnlyMemory<byte> EncodeHeaderBlock(Http2Settings settings, RequestResponseBase rr) // NOSONAR S3776 -- Same encode path as SendHeader; keep logic together.
        {
            // Reuse one Encoder (and its HPACK dynamic table) per direction for the lifetime of the connection,
            // mirroring how the Decoder is persisted below - the dynamic table is connection-scoped, not
            // per-message, so recreating it on every call (as before) meant every header was encoded as a
            // literal and repeated headers across streams/messages were never indexed. `settings` is one of
            // the two Http2Settings instances created once in SendHttp2 and shared by both relay directions,
            // so storing the encoder on it here gives every SendHeader call for this direction (including the
            // one used for synthetic responses) the same encoder/table instance.
            var encoder = settings.Encoder;
            if (encoder == null)
            {
                encoder = new Encoder(RfcDefaultHeaderTableSize);
                settings.Encoder = encoder;
            }

            // Encode scratch is connection-direction scoped and only used under the write lock / frame loop.
            var ms = settings.GetEncodeStream();
            var writer = settings.GetEncodeWriter();

            // RFC 7540 ?6.2: the HEADERS frame payload is [Pad Length?] [E + Stream Dependency + Weight, if
            // PRIORITY] [Header Block Fragment] [Padding?] - the priority fields (when present) are a
            // frame-level prefix that comes strictly *before* the header block fragment, which is the HPACK
            // byte sequence built below (dynamic table size update, if any, followed by the encoded
            // pseudo-headers/headers). Writing the priority bytes after the size-update instruction (as a
            // previous version of this code did) shifted every subsequent byte by 5, so the peer tried to
            // HPACK-decode a header block that actually started with garbage priority bytes - corrupting
            // this connection's HPACK state and manifesting as an intermittent, hard-to-reproduce
            // net::ERR_HTTP2_COMPRESSION_ERROR in the browser whenever a priority-bearing request happened
            // to coincide with a table-size change.
            if (rr.Priority.HasValue)
            {
                long p = rr.Priority.Value;
                writer.Write((byte)((p >> 32) & 0xff));
                writer.Write((byte)((p >> 24) & 0xff));
                writer.Write((byte)((p >> 16) & 0xff));
                writer.Write((byte)((p >> 8) & 0xff));
                writer.Write((byte)(p & 0xff));
            }

            // RFC 7541 §6.3: Dynamic Table Size Update(s) must appear at the beginning of the first
            // header block following any change to the peer's advertised ceiling.
            //
            // When multiple SETTINGS_HEADER_TABLE_SIZE updates arrive between two header blocks the spec
            // requires signalling the smallest value that occurred first so the peer's decoder can evict
            // entries it could no longer keep, before the encoder expands back to the final size.
            // (Example: Google sends size=0 then size=65536 during connection setup; omitting the
            // intermediate 0 leaves the encoder with live table entries the decoder already evicted,
            // causing indexed references to resolve to stale/wrong slots — manifesting as a
            // RST_STREAM(PROTOCOL_ERROR) from strict origins on the very next H2-native-relay stream.)
            var minSize = settings.MinHeaderTableSizeSinceLastEncode;
            var curSize = settings.HeaderTableSize;
            if (encoder.MaxHeaderTableSize != minSize)
                encoder.SetMaxHeaderTableSize(writer, minSize);
            if (encoder.MaxHeaderTableSize != curSize)
                encoder.SetMaxHeaderTableSize(writer, curSize);
            // Reset so only updates arriving *after* this encode are rolled into the next header block.
            settings.NotifyHeaderBlockEncoded();

            if (rr is Request request)
            {
                // Do NOT touch RequestUri/Url here: those allocate a Uri + string under writeLock
                // (H1→H2 / H3→H2 origin SendAsync critical section). Prefer already-materialized
                // Authority / IsHttps / RequestUriString8 from the bridge or HPACK decode.
                encoder.EncodeHeader(writer, StaticTable.KnownHeaderMethod, MethodBytes(request.Method));
                var authorityValue = request.Authority.Length > 0
                    ? request.Authority
                    : (request.Host ?? string.Empty).GetByteString();
                encoder.EncodeHeader(writer, StaticTable.KnownHeaderAuhtority, authorityValue);
                encoder.EncodeHeader(writer, StaticTable.KnownHeaderScheme,
                    request.IsHttps ? SchemeHttps : SchemeHttp);
                // Index :path (static "/" / repeated paths). IndexType.None forced a literal on every
                // stream and lengthened writeLock under Mac dual-TLS H1→H2 / H3→H2 multiplex.
                encoder.EncodeHeader(writer, StaticTable.KnownHeaderPath, request.RequestUriString8);
                // RFC 8441 §5: :protocol must appear after the other pseudo-headers.
                if (request.ExtendedConnectProtocol != null)
                    encoder.EncodeHeader(writer, StaticTable.KnownHeaderProtocol,
                        request.ExtendedConnectProtocol.GetByteString());
            }
            else
            {
                var response = (Response)rr;
                encoder.EncodeHeader(writer, StaticTable.KnownHeaderStatus, StatusCodeBytes(response.StatusCode));
            }

            foreach (var header in rr.Headers)
            {
                // RFC 7540 §8.1.2: header field names MUST be lowercase on the wire. Bridge handlers
                // normalize this up front (see LowercaseHeaderNames in Http2ToHttp11BridgeHandler /
                // Http2ToHttp3BridgeHandler), but that pass can be silently undone by anything that
                // re-adds a header afterwards using its canonical mixed-case name - e.g.
                // RequestResponseBase.ContentLength's setter picks "Content-Length" whenever
                // HttpVersion is below 2.0, which is exactly the state an H1/H3-origin-bridged
                // response is still in when CompressBodyAndUpdateContentLength() re-sets it right
                // before this loop runs. Enforcing lowercase here, at the single point where every
                // header actually gets HPACK-encoded onto an h2 wire, closes that gap regardless of
                // which upstream code path is responsible - a mixed-case name here reaches the peer
                // verbatim and manifests as a client RST_STREAM(PROTOCOL_ERROR).
                var nameData = header.NameData;
                if (!rr.HeaderNamesAreHttp2Normalized && HasUpperCaseAscii(nameData))
                    nameData = AsciiToLowerByteString(nameData);

                // Strip hop-by-hop / Host here so PrepareRequestForOrigin need not RemoveHeader seven
                // times under the H1→H2 path (still strips Host for Authority capture separately).
                if (ShouldOmitHttp2Header(nameData))
                    continue;

                // Via is added by the proxy itself on every request and varies across hops; it must
                // not enter the HPACK dynamic table.  If it did, stream N would encode it as a
                // single-byte dynamic-table reference, and strict H2 origins (Google's play.google.com
                // included) respond with RST_STREAM(PROTOCOL_ERROR) on any stream that carries a
                // Via header via an indexed reference rather than an explicit literal field.
                // IndexType.None means "literal without indexing" — the encoder skips Add() so
                // the entry never lands in the dynamic table, and every subsequent stream gets a
                // fresh literal representation instead of a back-reference.
                if (nameData.Equals(ViaHeaderLower) || nameData.EqualsIgnoreCaseAscii(ViaHeaderLower))
                    encoder.EncodeHeader(writer, nameData, header.ValueData, false,
                        HpackUtil.IndexType.None);
                else
                    encoder.EncodeHeader(writer, nameData, header.ValueData);
            }

            writer.Flush();
            return GetMemoryStreamMemory(ms);
        }

        // HPACK static table: :scheme http = 6, :scheme https = 7 → Indexed Header Field bytes.
        private const byte IndexedSchemeHttp = 0x86;
        private const byte IndexedSchemeHttps = 0x87;

        internal enum StaticSchemeOverrideResult
        {
            NeedFallback,
            Patched,
            AlreadyMatching,
        }

        private static byte StaticIndexedSchemeByte(ByteString scheme)
        {
            if (scheme.Equals(ProxyServer.UriSchemeHttp8) || scheme.Equals(SchemeHttp))
                return IndexedSchemeHttp;
            if (scheme.Equals(ProxyServer.UriSchemeHttps8) || scheme.Equals(SchemeHttps))
                return IndexedSchemeHttps;
            return 0;
        }

        /// <summary>
        ///     Fast mixed-transport path: walk HEADER_TABLE_SIZE=0 HPACK and rewrite Indexed
        ///     <c>:scheme</c> (0x86↔0x87) without a Decoder. Returns <see cref="StaticSchemeOverrideResult.AlreadyMatching"/>
        ///     when the block already carries the origin-transport scheme as a static index.
        /// </summary>
        internal static StaticSchemeOverrideResult TryApplyStaticIndexedSchemeOverride(byte[] block, // NOSONAR S3776 -- This protocol/state-machine path shares mutable parsing or transport state; splitting it further would create disproportionate regression risk.
            ByteString toScheme, out byte[] patched)
        {
            patched = null!;
            var to = StaticIndexedSchemeByte(toScheme);
            if (to == 0)
                return StaticSchemeOverrideResult.NeedFallback;
            var from = to == IndexedSchemeHttps ? IndexedSchemeHttp : IndexedSchemeHttps;

            var i = 0;
            var fromAt = -1;
            var toCount = 0;
            while (i < block.Length)
            {
                var b = block[i];
                if ((b & 0x80) != 0)
                {
                    if ((b & 0x7f) == 0)
                        return StaticSchemeOverrideResult.NeedFallback;
                    if (b == from)
                    {
                        if (fromAt >= 0)
                            return StaticSchemeOverrideResult.NeedFallback;
                        fromAt = i;
                    }
                    else if (b == to)
                    {
                        toCount++;
                    }

                    i++;
                    continue;
                }

                if ((b & 0xe0) == 0x20)
                {
                    if ((b & 0x1f) == 0x1f)
                        return StaticSchemeOverrideResult.NeedFallback;
                    i++;
                    continue;
                }

                if (!TrySkipHpackLiteral(block, ref i))
                    return StaticSchemeOverrideResult.NeedFallback;
            }

            if (fromAt >= 0 && toCount == 0)
            {
                // Owned fragment / CapturedCompressedHeaders / TryPrepare rebuild — patch in place
                // (TLS↔h2c). Avoids per-stream alloc+copy on the mixed-transport hot path.
                block[fromAt] = to;
                patched = block;
                return StaticSchemeOverrideResult.Patched;
            }

            if (fromAt < 0 && toCount == 1)
                return StaticSchemeOverrideResult.AlreadyMatching;

            return StaticSchemeOverrideResult.NeedFallback;
        }

        /// <summary>
        ///     Walk a HEADER_TABLE_SIZE=0 HPACK block and rewrite a single Indexed Header Field for
        ///     <c>:scheme</c> (static indices 6/7). Returns false when scheme is not indexed that way
        ///     (literal encoding, absent, or ambiguous) so the caller can fall back to full re-encode.
        /// </summary>
        internal static bool TryPatchStaticIndexedScheme(byte[] block, ByteString fromScheme, // NOSONAR S3776 -- This protocol/state-machine path shares mutable parsing or transport state; splitting it further would create disproportionate regression risk.
            ByteString toScheme, out byte[] patched)
        {
            patched = null!;
            var from = StaticIndexedSchemeByte(fromScheme);
            var to = StaticIndexedSchemeByte(toScheme);
            if (from == 0 || to == 0 || from == to)
                return false;

            var i = 0;
            var foundAt = -1;
            while (i < block.Length)
            {
                var b = block[i];
                if ((b & 0x80) != 0)
                {
                    // Indexed Header Field — 7-bit index fits in one byte for static table (1–61).
                    if ((b & 0x7f) == 0)
                        return false; // 7-bit integer continuation; not used for indices 6/7
                    if (b == from)
                    {
                        if (foundAt >= 0)
                            return false;
                        foundAt = i;
                    }

                    i++;
                    continue;
                }

                if ((b & 0xe0) == 0x20)
                {
                    // Dynamic Table Size Update — skip 5-bit integer (always single-byte when size=0).
                    if ((b & 0x1f) == 0x1f)
                        return false;
                    i++;
                    continue;
                }

                // Literal Header Field (with/without indexing / never indexed): skip name + value.
                if (!TrySkipHpackLiteral(block, ref i))
                    return false;
            }

            if (foundAt < 0)
                return false;

            patched = new byte[block.Length];
            Buffer.BlockCopy(block, 0, patched, 0, block.Length);
            patched[foundAt] = to;
            return true;
        }

        private static bool TrySkipHpackLiteral(byte[] block, ref int i)
        {
            if (i >= block.Length)
                return false;
            var b = block[i];
            int nameIndex;
            if ((b & 0xc0) == 0x40)
            {
                // Literal with incremental indexing — 6-bit name index
                nameIndex = b & 0x3f;
                i++;
                if (nameIndex == 0x3f && !TrySkipHpackIntegerContinuation(block, ref i))
                    return false;
            }
            else if ((b & 0xf0) == 0x00 || (b & 0xf0) == 0x10)
            {
                // Literal without indexing / never indexed — 4-bit name index
                nameIndex = b & 0x0f;
                i++;
                if (nameIndex == 0x0f && !TrySkipHpackIntegerContinuation(block, ref i))
                    return false;
            }
            else
            {
                return false;
            }

            if (nameIndex == 0 && !TrySkipHpackString(block, ref i))
                return false;

            return TrySkipHpackString(block, ref i);
        }

        private static bool TrySkipHpackString(byte[] block, ref int i)
        {
            if (i >= block.Length)
                return false;
            var len = block[i] & 0x7f;
            i++;
            if (len == 0x7f)
            {
                // RFC 7541 §5.1: value = (2^N - 1) + continuation
                if (!TrySkipHpackIntegerContinuation(block, ref i, out var extra))
                    return false;
                len = 127 + extra;
            }

            if (i + len > block.Length)
                return false;
            i += len;
            return true;
        }

        private static bool TrySkipHpackIntegerContinuation(byte[] block, ref int i)
            => TrySkipHpackIntegerContinuation(block, ref i, out _);

        private static bool TrySkipHpackIntegerContinuation(byte[] block, ref int i, out int value)
        {
            value = 0;
            var m = 0;
            while (i < block.Length)
            {
                var b = block[i++];
                value += (b & 0x7f) << m;
                if ((b & 0x80) == 0)
                    return true;
                m += 7;
                if (m > 28)
                    return false;
            }

            return false;
        }

        /// <summary>
        ///     Re-encodes a compressed-relay request header block with <paramref name="scheme"/> replacing
        ///     the client's ':scheme' - used on mixed-transport passthrough connections (inbound h2c client
        ///     with a TLS origin, or TLS-terminated client with a cleartext h2 origin) where relaying the
        ///     block verbatim makes strict origins reset every stream with PROTOCOL_ERROR because the scheme
        ///     does not match the origin transport. Compressed relay forces HEADER_TABLE_SIZE=0 on both
        ///     legs, so re-encoded blocks stay context-free and remain safe for any origin leg.
        /// </summary>
        private static byte[] ReencodeCompressedRequestBlock(Http2Settings settings, MyHeaderListener pseudo,
            HeaderCollection headers, ByteString scheme)
        {
            // Same serialization contract as QueueSendHeader: encoder + scratch are direction-scoped.
            lock (settings.Sync)
            {
                var encoder = settings.Encoder;
                if (encoder == null)
                {
                    encoder = new Encoder(RfcDefaultHeaderTableSize);
                    settings.Encoder = encoder;
                }

                var ms = settings.GetEncodeStream();
                var writer = settings.GetEncodeWriter();

                // Same RFC 7541 §6.3 dual-DTSU logic as EncodeHeaderBlock (see the detailed comment there).
                var minSize = settings.MinHeaderTableSizeSinceLastEncode;
                var curSize = settings.HeaderTableSize;
                if (encoder.MaxHeaderTableSize != minSize)
                    encoder.SetMaxHeaderTableSize(writer, minSize);
                if (encoder.MaxHeaderTableSize != curSize)
                    encoder.SetMaxHeaderTableSize(writer, curSize);
                settings.NotifyHeaderBlockEncoded();

                encoder.EncodeHeader(writer, StaticTable.KnownHeaderMethod, pseudo.Method);
                if (pseudo.Authority.Length > 0)
                    encoder.EncodeHeader(writer, StaticTable.KnownHeaderAuhtority, pseudo.Authority);
                encoder.EncodeHeader(writer, StaticTable.KnownHeaderScheme, scheme);
                if (pseudo.Path.Length > 0)
                    encoder.EncodeHeader(writer, StaticTable.KnownHeaderPath, pseudo.Path);
                // RFC 8441 §5: :protocol must appear after the other pseudo-headers.
                if (pseudo.Protocol.Length > 0)
                    encoder.EncodeHeader(writer, StaticTable.KnownHeaderProtocol, pseudo.Protocol);

                foreach (var header in headers)
                {
                    // RFC 7540 §8.1.2: names must be lowercase on the wire (same guard as EncodeHeaderBlock).
                    var nameData = header.NameData;
                    if (HasUpperCaseAscii(nameData))
                        nameData = AsciiToLowerByteString(nameData);
                    encoder.EncodeHeader(writer, nameData, header.ValueData);
                }

                writer.Flush();
            return GetMemoryStreamMemory(ms).ToArray();
            }
        }

        private static byte[]? BuildStaticLiteralAppendSuffix(
            MitmCompressedRelayHelper.AddedHeaderBuffer added, string? extraName, string? extraValue)
        {
            var literalCount = added.Count + (extraName != null ? 1 : 0);
            if (literalCount == 0)
                return null;

            var extraSize = 0;
            for (var i = 0; i < added.Count; i++)
            {
                var h = added[i];
                extraSize += GetStaticLiteralAppendSize(h.NameData.Length, h.ValueData.Length);
            }

            if (extraName != null)
                extraSize += GetStaticLiteralAppendSize(extraName.Length, extraValue!.Length);

            var result = new byte[extraSize];
            var offset = 0;
            for (var i = 0; i < added.Count; i++)
            {
                var h = added[i];
                offset = WriteStaticLiteralWithoutIndexing(result, offset, h.NameData.Span, h.ValueData.Span);
            }

            if (extraName != null)
                offset = WriteStaticLiteralWithoutIndexing(result, offset, extraName, extraValue!);

            return result;
        }

        private static bool TryPrepareMitmStaticHpackRelay(
            byte[] capturedBlock,
            MitmCompressedRelayHelper.HeaderRelayBaseline baseline,
            HeaderCollection after,
            bool injectVia,
            string? viaValue,
            out byte[] blockToRelay,
            out byte[]? appendSuffix)
        {
            blockToRelay = capturedBlock;
            appendSuffix = null;

            if (!MitmStaticRebuildHelper.TryPrepareStaticHpackRelay(
                    capturedBlock, baseline, after, out blockToRelay, out var added))
                return false;

            var injectViaLiteral = injectVia && !after.HeaderExists("via");
            appendSuffix = BuildStaticLiteralAppendSuffix(
                added,
                injectViaLiteral && !added.ContainsName("via") ? "via" : null,
                injectViaLiteral && !added.ContainsName("via") ? viaValue : null);
            return true;
        }

        private static int GetStaticLiteralAppendSize(int nameLength, int valueLength) =>
            1 + GetHpackStringLiteralEncodedSize(nameLength) + GetHpackStringLiteralEncodedSize(valueLength);

        private static int GetHpackStringLiteralEncodedSize(int byteLength) =>
            byteLength < 127 ? 1 + byteLength : WriteHpackPrefixedIntSize(7, (ulong)byteLength) + byteLength;

        private static int WriteHpackPrefixedIntSize(int prefixBits, ulong value)
        {
            var mask = (uint)((1 << prefixBits) - 1);
            if (value < mask)
                return 1;

            var size = 1;
            value -= mask;
            while (value >= 0x80)
            {
                size++;
                value >>= 7;
            }

            return size + 1;
        }

        private static int WriteStaticLiteralWithoutIndexing(byte[] dest, int offset, ReadOnlySpan<byte> name,
            ReadOnlySpan<byte> value)
        {
            dest[offset++] = 0x00; // Literal without indexing, new name (name index 0)
            offset += WriteHpackAsciiStringLiteral(dest.AsSpan(offset), name);
            offset += WriteHpackAsciiStringLiteral(dest.AsSpan(offset), value);
            return offset;
        }

        private static int WriteStaticLiteralWithoutIndexing(byte[] dest, int offset, string name, string value)
        {
            dest[offset++] = 0x00;
            offset += WriteHpackAsciiStringLiteral(dest.AsSpan(offset), name);
            offset += WriteHpackAsciiStringLiteral(dest.AsSpan(offset), value);
            return offset;
        }

        private static int WriteHpackAsciiStringLiteral(Span<byte> dest, ReadOnlySpan<byte> value)
        {
            var written = WriteHpackPrefixedInt(dest, 0x00, 7, (ulong)value.Length);
            value.CopyTo(dest.Slice(written));
            return written + value.Length;
        }

        private static int WriteHpackAsciiStringLiteral(Span<byte> dest, string value)
        {
            var written = WriteHpackPrefixedInt(dest, 0x00, 7, (ulong)value.Length);
            for (var i = 0; i < value.Length; i++)
                dest[written + i] = (byte)value[i];
            return written + value.Length;
        }

        private static int WriteHpackPrefixedInt(Span<byte> dest, byte patternByte, int prefixBits, ulong value)
        {
            var mask = (uint)((1 << prefixBits) - 1);
            if (value < mask)
            {
                dest[0] = (byte)(patternByte | (byte)value);
                return 1;
            }

            dest[0] = (byte)(patternByte | (byte)mask);
            var written = 1;
            value -= mask;
            while (value >= 0x80)
            {
                dest[written++] = (byte)((value & 0x7F) | 0x80);
                value >>= 7;
            }

            dest[written++] = (byte)value;
            return written;
        }
    }
}
