using System;
using System.Text;
using Titanium.Web.Proxy.Abstractions.Plugins;
using Titanium.Web.Proxy.Http;

namespace Titanium.Web.Proxy.Grpc;

/// <summary>
/// Adapter for application/grpc-web* content types. Preserves trailers; optional
/// <see cref="IGrpcTranscodeHook"/> — Core never embeds protobuf codecs.
/// </summary>
internal static class GrpcWebAdapter
{
    public const string GrpcWebContentType = "application/grpc-web";
    public const string GrpcWebProtoContentType = "application/grpc-web+proto";
    public const string GrpcWebTextContentType = "application/grpc-web-text";

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

    /// <summary>Decode a length-prefixed gRPC-Web frame (compressed flag + 4-byte BE length + payload).</summary>
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

    public static byte[] EncodeFrame(ReadOnlySpan<byte> payload, bool compressed = false)
    {
        var result = new byte[5 + payload.Length];
        result[0] = compressed ? (byte)1 : (byte)0;
        result[1] = (byte)((payload.Length >> 24) & 0xff);
        result[2] = (byte)((payload.Length >> 16) & 0xff);
        result[3] = (byte)((payload.Length >> 8) & 0xff);
        result[4] = (byte)(payload.Length & 0xff);
        payload.CopyTo(result.AsSpan(5));
        return result;
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

    public static byte[]? MaybeTranscode(IGrpcTranscodeHook? hook, byte[] requestBody)
    {
        if (hook is null)
        {
            return null;
        }

        return hook.TryTranscode(requestBody, out var response) ? response : null;
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
