using System;
using System.Buffers.Binary;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Titanium.Web.Proxy.Clusters;

/// <summary>Hand-coded grpc.health.v1.Health/Check for active probes (no protobuf dependency).</summary>
internal static class GrpcHealthProbe
{
    static GrpcHealthProbe() =>
        AppContext.SetSwitch("System.Net.Http.SocketsHttpHandler.Http2UnencryptedSupport", true);

    internal const string CheckPath = "/grpc.health.v1.Health/Check";
    private const int MaxBodyBytes = 64 * 1024;

    internal const int StatusServing = 1;
    internal const int StatusNotServing = 2;
    internal const int StatusServiceUnknown = 3;

    internal static byte[] EncodeRequest(string? service)
    {
        var message = EncodeHealthCheckRequestMessage(service);
        return WrapGrpcFrame(message);
    }

    internal static byte[] EncodeResponse(int status) => WrapGrpcFrame(EncodeHealthCheckResponseMessage(status));

    internal static byte[] EncodeHealthCheckRequestMessage(string? service)
    {
        if (string.IsNullOrEmpty(service))
        {
            return Array.Empty<byte>();
        }

        var utf8 = Encoding.UTF8.GetBytes(service);
        var lengthSize = GetVarintByteCount(utf8.Length);
        var message = new byte[1 + lengthSize + utf8.Length];
        message[0] = 0x0A;
        WriteVarint(message.AsSpan(1, lengthSize), (uint)utf8.Length);
        utf8.CopyTo(message.AsSpan(1 + lengthSize));
        return message;
    }

    internal static byte[] EncodeHealthCheckResponseMessage(int status)
    {
        var statusSize = GetVarintByteCount(status);
        var message = new byte[1 + statusSize];
        message[0] = 0x08;
        WriteVarint(message.AsSpan(1, statusSize), (uint)status);
        return message;
    }

    internal static byte[] WrapGrpcFrame(ReadOnlySpan<byte> message)
    {
        var frame = new byte[5 + message.Length];
        frame[0] = 0;
        BinaryPrimitives.WriteUInt32BigEndian(frame.AsSpan(1), (uint)message.Length);
        message.CopyTo(frame.AsSpan(5));
        return frame;
    }

    internal static bool TryParseResponse(ReadOnlySpan<byte> body, int? grpcStatusTrailer, out bool serving)
    {
        serving = false;
        if (grpcStatusTrailer is not 0)
        {
            return false;
        }

        // gRPC health CheckResponse is a single length-prefixed protobuf message.
        if (body.Length < 5 || body[0] != 0)
        {
            return false;
        }

        var length = BinaryPrimitives.ReadUInt32BigEndian(body[1..]);
        if (length > body.Length - 5 || length > MaxBodyBytes)
        {
            return false;
        }

        var message = body.Slice(5, (int)length);
        if (!TryParseHealthCheckResponse(message, out var status))
        {
            return false;
        }

        if (status == StatusServing)
        {
            serving = true;
            return true;
        }

        return false;
    }

    internal static async Task<bool> ProbeAsync(
        HttpClient client,
        Uri requestUri,
        string? grpcService,
        string? hostHeader,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, requestUri);
        request.Version = System.Net.HttpVersion.Version20;
        request.VersionPolicy = HttpVersionPolicy.RequestVersionExact;
        request.Headers.TryAddWithoutValidation("te", "trailers");
        request.Content = new ByteArrayContent(EncodeRequest(grpcService));
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/grpc");
        if (hostHeader is not null)
        {
            request.Headers.Host = hostHeader;
        }

        using var response = await client.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        if ((int)response.StatusCode != 200)
        {
            return false;
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var ms = new MemoryStream();
        var buffer = new byte[4096];
        while (true)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            if (ms.Length + read > MaxBodyBytes)
            {
                return false;
            }

            await ms.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }

        var trailers = response.TrailingHeaders;
        var grpcStatus = trailers.TryGetValues("grpc-status", out var values) &&
                         int.TryParse(values.FirstOrDefault(), out var parsed)
            ? parsed
            : (int?)null;
        return TryParseResponse(ms.GetBuffer().AsSpan(0, (int)ms.Length), grpcStatus, out _);
    }

    private static bool TryParseHealthCheckResponse(ReadOnlySpan<byte> message, out int status)
    {
        status = 0;
        var offset = 0;
        while (offset < message.Length)
        {
            if (!TryReadTag(message[offset..], out var tag, out var tagLength))
            {
                return false;
            }

            offset += tagLength;
            var field = tag >> 3;
            var wire = tag & 0x07;
            if (field == 1 && wire == 0)
            {
                if (!TryReadVarint(message[offset..], out var value, out var valueLength))
                {
                    return false;
                }

                status = (int)value;
                return true;
            }

            if (!TrySkipField(message[offset..], wire, out var skipLength))
            {
                return false;
            }

            offset += skipLength;
        }

        return false;
    }

    private static int GetVarintByteCount(int value)
    {
        var count = 0;
        var v = (uint)value;
        do
        {
            count++;
            v >>= 7;
        }
        while (v != 0);

        return count;
    }

    private static void WriteVarint(Span<byte> destination, uint value)
    {
        var offset = 0;
        while (value >= 0x80)
        {
            destination[offset++] = (byte)(value | 0x80);
            value >>= 7;
        }

        destination[offset] = (byte)value;
    }

    private static bool TryReadTag(ReadOnlySpan<byte> data, out uint tag, out int length)
    {
        tag = 0;
        length = 0;
        if (!TryReadVarint(data, out tag, out length))
        {
            return false;
        }

        return tag != 0 || length > 0;
    }

    private static bool TryReadVarint(ReadOnlySpan<byte> data, out uint value, out int length)
    {
        value = 0;
        length = 0;
        for (var shift = 0; shift < 35 && length < data.Length; shift += 7)
        {
            var b = data[length++];
            value |= (uint)(b & 0x7F) << shift;
            if ((b & 0x80) == 0)
            {
                return true;
            }
        }

        return false;
    }

    private static bool TrySkipField(ReadOnlySpan<byte> data, uint wire, out int length)
    {
        length = 0;
        switch (wire)
        {
            case 0:
                return TryReadVarint(data, out _, out length);
            case 1:
                length = 8;
                return data.Length >= length;
            case 2:
                if (!TryReadVarint(data, out var len, out var lenSize))
                {
                    return false;
                }

                length = lenSize + (int)len;
                return data.Length >= length;
            case 5:
                length = 4;
                return data.Length >= length;
            default:
                return false;
        }
    }
}
