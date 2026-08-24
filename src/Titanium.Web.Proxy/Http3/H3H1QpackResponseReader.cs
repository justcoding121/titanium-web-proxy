using System;
using System.Buffers.Text;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Titanium.Web.Proxy.Helpers;
using Titanium.Web.Proxy.Http;
using Titanium.Web.Proxy.Http3.Qpack;
using Titanium.Web.Proxy.Models;

namespace Titanium.Web.Proxy.Http3;

/// <summary>
///     H3→H1 fast path: parse origin H1 response headers straight into a QPACK block without a
///     <see cref="Response"/> / <see cref="HeaderCollection"/> graph (CI multiplex gen0 cut).
/// </summary>
internal static class H3H1QpackResponseReader
{
    internal readonly struct Result
    {
        public required byte[] QpackHeaders { get; init; }
        /// <summary>Content-Length when present; -1 when absent.</summary>
        public long ContentLength { get; init; }
        public bool IsChunked { get; init; }
        public bool ConnectionClose { get; init; }
    }

    internal static async ValueTask<Result?> TryReadAsync(
        HttpStream reader,
        int statusCode,
        QpackContext? qpackContext,
        CancellationToken cancellationToken)
    {
        using var builder = QpackEncoder.RentResponseBlockBuilder(statusCode, qpackContext);
        long contentLength = -1;
        var isChunked = false;
        var connectionClose = false;

        while (reader.TryConsumeHeaderLineFromBuffer(out var emptyLine, out var lineBytes))
        {
            if (emptyLine)
                return Finish(builder, contentLength, isChunked, connectionClose);
            ConsumeLine(lineBytes, builder, ref contentLength, ref isChunked, ref connectionClose);
        }

        while (true)
        {
            while (reader.TryConsumeHeaderLineFromBuffer(out var emptyLine, out var lineBytes))
            {
                if (emptyLine)
                    return Finish(builder, contentLength, isChunked, connectionClose);
                ConsumeLine(lineBytes, builder, ref contentLength, ref isChunked, ref connectionClose);
            }

            var (tmpLine, cancelled) = await reader.ReadLineWithResultAsync(cancellationToken);
            if (cancelled)
                return null;
            if (string.IsNullOrEmpty(tmpLine))
                return Finish(builder, contentLength, isChunked, connectionClose);
            ConsumeLine(tmpLine, builder, ref contentLength, ref isChunked, ref connectionClose);
        }
    }

    private static Result Finish(
        QpackEncoder.ResponseBlockBuilder builder,
        long contentLength,
        bool isChunked,
        bool connectionClose) =>
        new()
        {
            QpackHeaders = builder.Finish(),
            ContentLength = contentLength,
            IsChunked = isChunked,
            ConnectionClose = connectionClose
        };

    private static void ConsumeLine(
        ReadOnlySpan<byte> tmpLine,
        QpackEncoder.ResponseBlockBuilder builder,
        ref long contentLength,
        ref bool isChunked,
        ref bool connectionClose)
    {
        var colonIndex = tmpLine.IndexOf((byte)':');
        if (colonIndex == -1)
            throw new FormatException("Header line should contain a colon character.");

        var nameSpan = TrimAscii(tmpLine.Slice(0, colonIndex));
        var valueSpan = TrimAscii(tmpLine.Slice(colonIndex + 1));
        var lowerName = LowerAsciiName(nameSpan);

        if (lowerName is "connection" or "keep-alive" or "proxy-connection" or "upgrade")
        {
            if (lowerName == "connection" && AsciiEqualsIgnoreCase(valueSpan, "close"))
                connectionClose = true;
            return;
        }

        if (lowerName == "transfer-encoding")
        {
            if (AsciiContainsIgnoreCase(valueSpan, "chunked"))
                isChunked = true;
            return; // hop-by-hop — omit from QPACK
        }

        if (lowerName == "content-length"
            && Utf8Parser.TryParse(valueSpan, out long cl, out _) && cl >= 0)
            contentLength = cl;

        var value = GetLatin1String(valueSpan);
        builder.AddHeader(lowerName, value);
    }

    private static void ConsumeLine(
        string tmpLine,
        QpackEncoder.ResponseBlockBuilder builder,
        ref long contentLength,
        ref bool isChunked,
        ref bool connectionClose)
    {
        var colonIndex = tmpLine.IndexOf(':');
        if (colonIndex == -1)
            throw new FormatException("Header line should contain a colon character.");

        var nameSpan = tmpLine.AsSpan(0, colonIndex).Trim();
        var valueSpan = tmpLine.AsSpan(colonIndex + 1).Trim();
        var lowerName = LowerAsciiName(nameSpan);

        if (lowerName is "connection" or "keep-alive" or "proxy-connection" or "upgrade")
        {
            if (lowerName == "connection"
                && valueSpan.Equals("close", StringComparison.OrdinalIgnoreCase))
                connectionClose = true;
            return;
        }

        if (lowerName == "transfer-encoding")
        {
            if (valueSpan.Contains("chunked", StringComparison.OrdinalIgnoreCase))
                isChunked = true;
            return;
        }

        if (lowerName == "content-length"
            && long.TryParse(valueSpan, out var cl) && cl >= 0)
            contentLength = cl;

        builder.AddHeader(lowerName, valueSpan.ToString());
    }

    private static string LowerAsciiName(ReadOnlySpan<byte> name)
    {
        if (KnownHeaders.TryMatchName(name, out var known))
            return KnownToQpackName(known);

        return AsciiToLowerAlloc(name);
    }

    private static string LowerAsciiName(ReadOnlySpan<char> name)
    {
        if (KnownHeaders.TryMatchName(name, out var known))
            return KnownToQpackName(known);

        for (var i = 0; i < name.Length; i++)
        {
            if (name[i] is >= 'A' and <= 'Z')
                return string.Create(name.Length, name, static (dest, src) =>
                {
                    for (var j = 0; j < src.Length; j++)
                    {
                        var c = src[j];
                        dest[j] = c is >= 'A' and <= 'Z' ? (char)(c + 32) : c;
                    }
                });
        }

        return name.ToString();
    }

    private static string KnownToQpackName(KnownHeader known)
    {
        // Title-Case KnownHeaders → lowercase QPACK (interned).
        if (ReferenceEquals(known, KnownHeaders.ContentLength)
            || ReferenceEquals(known, KnownHeaders.ContentLengthHttp2))
            return "content-length";
        if (ReferenceEquals(known, KnownHeaders.ContentType))
            return "content-type";
        if (ReferenceEquals(known, KnownHeaders.Date))
            return "date";
        if (ReferenceEquals(known, KnownHeaders.Server))
            return "server";
        if (ReferenceEquals(known, KnownHeaders.Connection))
            return "connection";
        if (ReferenceEquals(known, KnownHeaders.TransferEncoding))
            return "transfer-encoding";
        if (ReferenceEquals(known, KnownHeaders.Host))
            return "host";

        var s = known.String;
        for (var i = 0; i < s.Length; i++)
        {
            if (s[i] is >= 'A' and <= 'Z')
                return s.ToLowerInvariant();
        }

        return s;
    }

    private static string AsciiToLowerAlloc(ReadOnlySpan<byte> name) =>
        string.Create(name.Length, name, static (dest, src) =>
        {
            for (var i = 0; i < src.Length; i++)
            {
                var b = src[i];
                dest[i] = (char)(b is >= (byte)'A' and <= (byte)'Z' ? b + 32 : b);
            }
        });

    private static string GetLatin1String(ReadOnlySpan<byte> value)
    {
        if (value.IsEmpty)
            return string.Empty;
        return string.Create(value.Length, value, static (dest, src) =>
        {
            for (var i = 0; i < src.Length; i++)
                dest[i] = (char)src[i];
        });
    }

    private static ReadOnlySpan<byte> TrimAscii(ReadOnlySpan<byte> value)
    {
        var start = 0;
        while (start < value.Length && value[start] is (byte)' ' or (byte)'\t')
            start++;
        var end = value.Length;
        while (end > start && value[end - 1] is (byte)' ' or (byte)'\t')
            end--;
        return value.Slice(start, end - start);
    }

    private static bool AsciiEqualsIgnoreCase(ReadOnlySpan<byte> value, string ascii)
    {
        if (value.Length != ascii.Length)
            return false;
        for (var i = 0; i < value.Length; i++)
        {
            var a = value[i];
            var b = (byte)ascii[i];
            if (a is >= (byte)'A' and <= (byte)'Z')
                a = (byte)(a + 32);
            if (b is >= (byte)'A' and <= (byte)'Z')
                b = (byte)(b + 32);
            if (a != b)
                return false;
        }

        return true;
    }

    private static bool AsciiContainsIgnoreCase(ReadOnlySpan<byte> value, string ascii)
    {
        if (ascii.Length == 0 || value.Length < ascii.Length)
            return false;
        for (var i = 0; i <= value.Length - ascii.Length; i++)
        {
            if (AsciiEqualsIgnoreCase(value.Slice(i, ascii.Length), ascii))
                return true;
        }

        return false;
    }
}
