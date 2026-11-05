using System;
using System.Collections.Generic;
using System.Text;
using Titanium.Web.Proxy.Http;
using Titanium.Web.Proxy.Models;

namespace Titanium.Web.Proxy.Grpc;

/// <summary>
/// Adapter for application/grpc-web* content types and length-prefixed gRPC frames.
/// Protobuf codecs live outside Core (e.g. Plus gRPC-JSON transcoder).
/// </summary>
internal static class GrpcWebAdapter
{
    public const string GrpcWebContentType = "application/grpc-web";
    public const string GrpcWebProtoContentType = "application/grpc-web+proto";
    public const string GrpcWebTextContentType = "application/grpc-web-text";
    public const string GrpcContentType = "application/grpc";

    /// <summary>Frame flag bit that marks a gRPC-Web trailer frame (the payload is HTTP/1-style header text).</summary>
    public const byte TrailerFrameFlag = 0x80;

    public static bool IsGrpcWeb(HeaderCollection headers)
    {
        var ct = headers.GetFirstHeader(KnownHeaders.ContentType)?.Value;
        if (string.IsNullOrEmpty(ct))
        {
            return false;
        }

        return ct.StartsWith(GrpcWebContentType, StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsGrpcWebText(HeaderCollection headers)
    {
        var ct = headers.GetFirstHeader(KnownHeaders.ContentType)?.Value;
        return ct is not null &&
               ct.StartsWith(GrpcWebTextContentType, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// True for the binary gRPC-Web media types the proxy translates: <c>application/grpc-web</c> and
    /// <c>application/grpc-web+proto</c>, with or without parameters. <c>-text</c> and other suffixes are not.
    /// </summary>
    public static bool IsGrpcWebBinary(string? contentType)
    {
        var mediaType = MediaType(contentType);
        return mediaType.Equals(GrpcWebContentType, StringComparison.OrdinalIgnoreCase) ||
               mediaType.Equals(GrpcWebProtoContentType, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>True for native gRPC (<c>application/grpc</c> or <c>application/grpc+proto</c>), not gRPC-Web.</summary>
    public static bool IsNativeGrpc(string? contentType)
    {
        var mediaType = MediaType(contentType);
        return mediaType.Equals(GrpcContentType, StringComparison.OrdinalIgnoreCase) ||
               mediaType.StartsWith(GrpcContentType + "+", StringComparison.OrdinalIgnoreCase);
    }

    private static ReadOnlySpan<char> MediaType(string? contentType)
    {
        if (string.IsNullOrEmpty(contentType))
        {
            return default;
        }

        var span = contentType.AsSpan();
        var semicolon = span.IndexOf(';');
        return (semicolon < 0 ? span : span[..semicolon]).Trim();
    }

    /// <summary>Decode a length-prefixed gRPC frame (compressed flag + 4-byte BE length + payload).</summary>
    public static bool TryReadFrame(ReadOnlySpan<byte> buffer, out bool compressed, out ReadOnlySpan<byte> payload, out int consumed)
    {
        compressed = false;
        payload = default;
        consumed = 0;
        if (buffer.Length < 5)
        {
            return false;
        }

        compressed = (buffer[0] & 1) != 0;
        var length = (buffer[1] << 24) | (buffer[2] << 16) | (buffer[3] << 8) | buffer[4];
        if (length < 0 || buffer.Length < 5 + length)
        {
            return false;
        }

        payload = buffer.Slice(5, length);
        consumed = 5 + length;
        return true;
    }

    public static byte[] EncodeFrame(ReadOnlySpan<byte> payload, bool compressed = false) =>
        EncodeFrame(payload, compressed ? (byte)1 : (byte)0);

    /// <summary>Encode a length-prefixed frame with an explicit flag byte (<c>0x80</c> marks a gRPC-Web trailer frame).</summary>
    public static byte[] EncodeFrame(ReadOnlySpan<byte> payload, byte flags)
    {
        var result = new byte[5 + payload.Length];
        result[0] = flags;
        result[1] = (byte)((payload.Length >> 24) & 0xff);
        result[2] = (byte)((payload.Length >> 16) & 0xff);
        result[3] = (byte)((payload.Length >> 8) & 0xff);
        result[4] = (byte)(payload.Length & 0xff);
        payload.CopyTo(result.AsSpan(5));
        return result;
    }

    /// <summary>
    /// Builds the final gRPC-Web frame: flag <c>0x80</c>, then <c>name: value\r\n</c> lines with lowercase names.
    /// Pseudo-headers (names starting with <c>:</c>) are skipped.
    /// </summary>
    public static byte[] EncodeTrailerFrame(IEnumerable<HttpHeader> trailers)
    {
        var text = new StringBuilder();
        foreach (var header in trailers)
        {
            var name = header.Name;
            if (name.Length == 0 || name[0] == ':')
            {
                continue;
            }

            text.Append(name.ToLowerInvariant()).Append(": ").Append(header.Value).Append("\r\n");
        }

        return EncodeFrame(Encoding.UTF8.GetBytes(text.ToString()), TrailerFrameFlag);
    }

    /// <summary>Copy grpc-status / grpc-message trailers into response headers for gRPC-Web clients.</summary>
    public static void PromoteTrailersToHeaders(HeaderCollection trailers, HeaderCollection responseHeaders)
    {
        foreach (var header in trailers)
        {
            var name = header.Name;
            if (name.Equals("grpc-status", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("grpc-message", StringComparison.OrdinalIgnoreCase) ||
                name.StartsWith("grpc-", StringComparison.OrdinalIgnoreCase))
            {
                responseHeaders.AddHeader(name, header.Value);
            }
        }
    }

    public static byte[]? DecodeBase64TextBody(ReadOnlySpan<byte> asciiBody)
    {
        try
        {
            var text = Encoding.ASCII.GetString(asciiBody);
            return Convert.FromBase64String(text);
        }
        catch (FormatException)
        {
            return null;
        }
    }
}
