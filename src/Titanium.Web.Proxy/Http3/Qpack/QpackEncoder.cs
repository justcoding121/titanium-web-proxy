using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Titanium.Web.Proxy.Extensions;
using Titanium.Web.Proxy.Http;

namespace Titanium.Web.Proxy.Http3.Qpack;

/// <summary>
///     QPACK encoder (RFC 9204). Supports both static-table-only mode and dynamic-table mode.
///     When <see cref="QpackContext" /> is provided and not disabled, the encoder will attempt to
///     reference dynamic-table entries for repeated headers; otherwise all blocks use static-table
///     references or literals. The 2-byte prefix (Required Insert Count + Base) is computed per
///     RFC 9204 §4.5.1 and emitted at the front of every HEADERS block.
/// </summary>
internal static class QpackEncoder
{
    [ThreadStatic]
    private static MemoryStream? reusableBuffer;

    /// <summary>
    ///     Encodes a list of header fields into a QPACK header block using static-table-only mode.
    ///     Equivalent to calling <see cref="Encode(IEnumerable{ValueTuple{string,string}},QpackContext?)" />
    ///     with a null context.
    /// </summary>
    public static byte[] Encode(IEnumerable<(string Name, string Value)> headers) =>
        Encode(headers, context: null);

    /// <summary>
    ///     Encodes a list of header fields into a QPACK header block.
    ///     When <paramref name="context" /> is non-null and the outbound table is not disabled, the
    ///     encoder will reference dynamic-table entries. The Required Insert Count prefix is encoded per
    ///     RFC 9204 §4.5.1.1.
    /// </summary>
    public static byte[] Encode(IEnumerable<(string Name, string Value)> headers, QpackContext? context) // NOSONAR S3776 -- This protocol/state-machine path shares mutable parsing or transport state; splitting it further would create disproportionate regression risk.
    {
        var body = reusableBuffer ??= new MemoryStream();
        body.SetLength(0);
        var outboundTable = context != null && !context.OutboundTableDisabled && context.MaxTableCapacityFromPeer > 0
            ? context.OutboundEncoderTable
            : null;

        ulong maxRequiredInsertCount = 0;

        foreach (var (name, value) in headers)
            EncodeOne(body, outboundTable, ref maxRequiredInsertCount, name, value);

        return FinishBlock(body, maxRequiredInsertCount, outboundTable, context);
    }

    /// <summary>
    ///     Encodes an HTTP response without building an intermediate header list (hot H3→client path).
    /// </summary>
    public static byte[] EncodeResponse(Response response, QpackContext? context)
    {
        var body = reusableBuffer ??= new MemoryStream();
        body.SetLength(0);
        var outboundTable = context != null && !context.OutboundTableDisabled && context.MaxTableCapacityFromPeer > 0
            ? context.OutboundEncoderTable
            : null;

        ulong maxRequiredInsertCount = 0;
        EncodeOne(body, outboundTable, ref maxRequiredInsertCount, ":status",
            StatusCodeToString(response.StatusCode));

        foreach (var header in response.Headers)
        {
            var name = header.Name;
            if (HasUpperAscii(name))
                name = name.ToLowerInvariant();
            if (name is "connection" or "keep-alive" or "proxy-connection"
                or "transfer-encoding" or "upgrade")
                continue;
            EncodeOne(body, outboundTable, ref maxRequiredInsertCount, name, header.Value);
        }

        return FinishBlock(body, maxRequiredInsertCount, outboundTable, context);
    }

    /// <summary>
    ///     Encodes an HTTP request without building an intermediate header list (hot H3→origin path).
    /// </summary>
    public static byte[] EncodeRequest(Request request, string authorityHost, QpackContext? context = null)
    {
        var body = reusableBuffer ??= new MemoryStream();
        body.SetLength(0);
        var outboundTable = context != null && !context.OutboundTableDisabled && context.MaxTableCapacityFromPeer > 0
            ? context.OutboundEncoderTable
            : null;

        ulong maxRequiredInsertCount = 0;

        var authority = request.Authority.Length > 0
            ? request.Authority.GetString()
            : (!string.IsNullOrEmpty(request.Host) ? request.Host! : authorityHost);
        var path = request.RequestUriString8.Length > 0
            ? request.RequestUriString8.GetString()
            : "/";
        if (UriExtensions.GetScheme(request.RequestUriString8).Length > 0)
        {
            try
            {
                var uri = request.RequestUri;
                authority = uri.Authority;
                path = uri.PathAndQuery;
            }
            catch
            {
                // Keep ByteString-derived authority/path.
            }
        }

        EncodeOne(body, outboundTable, ref maxRequiredInsertCount, ":method", request.Method);
        EncodeOne(body, outboundTable, ref maxRequiredInsertCount, ":scheme",
            request.IsHttps ? "https" : "http");
        EncodeOne(body, outboundTable, ref maxRequiredInsertCount, ":authority", authority);
        EncodeOne(body, outboundTable, ref maxRequiredInsertCount, ":path",
            path.Length > 0 ? path : "/");

        foreach (var header in request.Headers.GetAllHeaders())
        {
            var name = header.Name;
            if (HasUpperAscii(name))
                name = name.ToLowerInvariant();
            if (name is "connection" or "keep-alive" or "proxy-connection"
                or "transfer-encoding" or "upgrade" or "te" or "host"
                or "http2-settings" or "proxy-authorization" or "proxy-authenticate")
                continue;
            EncodeOne(body, outboundTable, ref maxRequiredInsertCount, name, header.Value);
        }

        return FinishBlock(body, maxRequiredInsertCount, outboundTable, context);
    }

    private static string StatusCodeToString(int statusCode) => statusCode switch
    {
        200 => "200",
        204 => "204",
        301 => "301",
        302 => "302",
        304 => "304",
        400 => "400",
        404 => "404",
        500 => "500",
        502 => "502",
        503 => "503",
        _ => statusCode.ToString()
    };

    private static bool HasUpperAscii(string s)
    {
        for (var i = 0; i < s.Length; i++)
        {
            var c = s[i];
            if (c is >= 'A' and <= 'Z') return true;
        }

        return false;
    }

    private static void EncodeOne(MemoryStream body, QpackDynamicTable? outboundTable,
        ref ulong maxRequiredInsertCount, string lowerName, string value)
    {
        var exact = QpackStaticTable.FindExact(lowerName, value);
        if (exact >= 0)
        {
            WriteIndexed(body, (ulong)exact);
            return;
        }

        var nameOnlyStaticIndex = QpackStaticTable.FindName(lowerName);

        if (outboundTable != null && outboundTable.TryFind(lowerName, value, out ulong dynAbsIdx, out bool dynExact))
        {
            if (dynExact)
            {
                WriteDynamicIndexed(body, dynAbsIdx);
                maxRequiredInsertCount = Math.Max(maxRequiredInsertCount, dynAbsIdx + 1);
            }
            else
            {
                WriteLiteralWithDynamicNameRef(body, dynAbsIdx, value);
                maxRequiredInsertCount = Math.Max(maxRequiredInsertCount, dynAbsIdx + 1);
            }

            return;
        }

        if (nameOnlyStaticIndex >= 0)
        {
            WriteLiteralWithStaticNameRef(body, (ulong)nameOnlyStaticIndex, value);
            return;
        }

        WriteLiteralNewName(body, lowerName, value);
    }

    private static byte[] FinishBlock(MemoryStream body, ulong maxRequiredInsertCount,
        QpackDynamicTable? outboundTable, QpackContext? context)
    {
        byte ricByte, sByte;
        if (maxRequiredInsertCount == 0 || outboundTable == null)
        {
            ricByte = 0x00;
            sByte = 0x00;
        }
        else
        {
            var encodedRic = EncodeRequiredInsertCount(maxRequiredInsertCount, context!.MaxTableCapacityFromPeer);
            ricByte = (byte)(encodedRic & 0xFF);
            sByte = 0x00;
        }

        var result = new byte[2 + body.Length];
        result[0] = ricByte;
        result[1] = sByte;
        body.GetBuffer().AsSpan(0, (int)body.Length).CopyTo(result.AsSpan(2));
        return result;
    }

    /// <summary>
    ///     Encodes the Required Insert Count per RFC 9204 §4.5.1.1.
    ///     Encoded = (count % (2 * MaxEntries)) + 1, where MaxEntries = floor(MaxTableCapacity / 32).
    /// </summary>
    internal static ulong EncodeRequiredInsertCount(ulong requiredInsertCount, uint maxTableCapacity)
    {
        var maxEntries = (ulong)(maxTableCapacity / 32);
        if (maxEntries == 0) maxEntries = 1;
        return (requiredInsertCount % (2 * maxEntries)) + 1;
    }

    // Indexed Header Field (static): 1 1 S=1 Index(6) — pattern 0xC0
    private static void WriteIndexed(MemoryStream buf, ulong index)
    {
        WritePrefixedInt(buf, 0xC0, 6, index);
    }

    // Indexed Header Field (dynamic, post-base): 0 0 0 1 Index(4) — pattern 0x10
    private static void WriteDynamicIndexed(MemoryStream buf, ulong absoluteIndex)
    {
        WritePrefixedInt(buf, 0x10, 4, absoluteIndex);
    }

    // Literal Header Field With Name Reference (static): 0 1 N=0 S=1 Index(4) — pattern 0x50
    private static void WriteLiteralWithStaticNameRef(MemoryStream buf, ulong nameIndex, string value)
    {
        WritePrefixedInt(buf, 0x50, 4, nameIndex);
        WriteStringLiteral(buf, value);
    }

    // Literal Header Field With Name Reference (dynamic, post-base): 0 0 0 0 N=0 Index(3) — pattern 0x00
    private static void WriteLiteralWithDynamicNameRef(MemoryStream buf, ulong absoluteIndex, string value)
    {
        WritePrefixedInt(buf, 0x00, 3, absoluteIndex);
        WriteStringLiteral(buf, value);
    }

    // Literal Field Line With Literal Name (RFC 9204 §4.5.6):
    //   0 0 1 N H NameLen(3+) | Name | H ValueLen(7+) | Value
    // Name length occupies the 3-bit prefix of the first byte — it is NOT a separate string literal.
    private static void WriteLiteralNewName(MemoryStream buf, string name, string value)
    {
        var nameBytes = Encoding.Latin1.GetBytes(name);
        // pattern 0x20 => 001 N=0 H=0, then 3-bit-prefixed name length
        WritePrefixedInt(buf, 0x20, 3, (ulong)nameBytes.Length);
        buf.Write(nameBytes, 0, nameBytes.Length);
        WriteStringLiteral(buf, value);
    }

    /// <summary>Writes an RFC 7541 §5.1 prefixed integer into the stream.</summary>
    private static void WritePrefixedInt(MemoryStream buf, byte patternByte, int prefixBits, ulong value)
    {
        var mask = (uint)((1 << prefixBits) - 1);
        if (value < mask)
        {
            buf.WriteByte((byte)(patternByte | (byte)value));
            return;
        }

        buf.WriteByte((byte)(patternByte | (byte)mask));
        value -= mask;
        while (value >= 0x80)
        {
            buf.WriteByte((byte)((value & 0x7F) | 0x80));
            value >>= 7;
        }
        buf.WriteByte((byte)value);
    }

    /// <summary>
    ///     Writes an RFC 7541 §5.2 string literal (length-prefixed, no Huffman encoding).
    ///     We always emit raw bytes (H=0) to keep the implementation simple.
    /// </summary>
    private static void WriteStringLiteral(MemoryStream buf, string s)
    {
        var bytes = Encoding.Latin1.GetBytes(s);
        WritePrefixedInt(buf, 0x00, 7, (ulong)bytes.Length);
        buf.Write(bytes, 0, bytes.Length);
    }
}
