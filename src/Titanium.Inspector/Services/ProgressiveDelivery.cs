using System.Buffers;
using System.Diagnostics;
using System.IO.Compression;
using Titanium.Web.Proxy.Http;
using Titanium.Web.Proxy.Models;

namespace Titanium.Inspector.Services;

internal enum ProgressiveSkipReason
{
    None,
    Disabled,
    TooSmall,
    Status,
    Head,
    Upgrade,
    Trailers,
    Encoding,
    Transcoded,
    Edited,
    NoBody,
    BuildFailed,
}

/// <summary>
///     Rebuilds an already-buffered download so the proxy can write it in paced slices.
///     Anything this cannot rebuild safely is left for the one-shot delay path.
/// </summary>
internal static class ProgressiveDelivery
{
    public const int MinWireBytes = 64 * 1024;
    public const int SliceBytes = 16 * 1024;

    private static long _progressive;
    private static long _fallbackTooSmall;
    private static long _fallbackStatus;
    private static long _fallbackHead;
    private static long _fallbackUpgrade;
    private static long _fallbackTrailers;
    private static long _fallbackEncoding;
    private static long _fallbackTranscoded;
    private static long _fallbackEdited;
    private static long _fallbackNoBody;
    private static long _fallbackBuildFailed;

    internal static ProgressivePlan? TryBuild(
        Response origin,
        Request request,
        byte[]? plainBody,
        long originWireBytes,
        bool transcoded,
        bool edited,
        bool enabled,
        long minWireBytes = MinWireBytes)
    {
        var reason = Evaluate(origin, request, plainBody, originWireBytes, transcoded, edited, enabled, minWireBytes);
        if (reason != ProgressiveSkipReason.None)
        {
            Record(reason);
            return null;
        }

        try
        {
            var encoding = ContentEncoding(origin);
            var wire = Encode(plainBody!, encoding);
            if (wire is null)
            {
                Record(ProgressiveSkipReason.BuildFailed);
                return null;
            }

            var response = CopyResponse(origin, wire.Length);
            Record(ProgressiveSkipReason.None);
            return new ProgressivePlan(response, wire, originWireBytes > 0 ? originWireBytes : wire.Length);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"ProgressiveDelivery build failed: {ex.GetType().Name}");
            Record(ProgressiveSkipReason.BuildFailed);
            return null;
        }
    }

    /// <summary>
    ///     A streamed replacement skips the proxy's own response processing, which is where Core appends its
    ///     <c>Via</c> entry (RFC 9110 §7.6.3). Append the same entry so the client sees the headers it would
    ///     see on the normal path. Idempotent for the exact received-protocol entry.
    /// </summary>
    internal static void AppendVia(HeaderCollection headers, Version httpVersion, string pseudonym)
    {
        var entry = $"{httpVersion.Major}.{httpVersion.Minor} {pseudonym}";
        var existing = headers.GetHeaders("Via");
        if (existing is not { Count: > 0 })
        {
            headers.AddHeader("via", entry);
            return;
        }

        var values = existing.Select(h => h.Value).ToList();
        foreach (var value in values)
        {
            foreach (var token in value.Split(','))
            {
                if (string.Equals(token.Trim(), entry, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }
            }
        }

        values[0] = $"{values[0]}, {entry}";
        headers.RemoveHeader("Via");
        foreach (var value in values)
        {
            headers.AddHeader("via", value);
        }
    }

    /// <summary>
    ///     Tells an HTTP/1 client the connection ends after this response, so it never reuses a socket the
    ///     proxy is about to close (a reused socket would race with the close and surface as a reset).
    /// </summary>
    internal static void MarkConnectionClose(HeaderCollection headers)
    {
        headers.RemoveHeader("Connection");
        headers.RemoveHeader("Keep-Alive");
        headers.AddHeader("Connection", "close");
    }

    internal static ProgressiveSkipReason Evaluate(
        Response origin,
        Request request,
        byte[]? plainBody,
        long originWireBytes,
        bool transcoded,
        bool edited,
        bool enabled,
        long minWireBytes = MinWireBytes)
    {
        if (!enabled)
        {
            return ProgressiveSkipReason.Disabled;
        }

        if (edited)
        {
            return ProgressiveSkipReason.Edited;
        }

        if (transcoded)
        {
            return ProgressiveSkipReason.Transcoded;
        }

        if (string.Equals(request.Method, "HEAD", StringComparison.OrdinalIgnoreCase))
        {
            return ProgressiveSkipReason.Head;
        }

        if (origin.StatusCode is not (200 or 206))
        {
            return ProgressiveSkipReason.Status;
        }

        if (request.UpgradeToWebSocket || HasHeader(request.Headers, "Upgrade") || HasHeader(origin.Headers, "Upgrade"))
        {
            return ProgressiveSkipReason.Upgrade;
        }

        if (HasHeader(origin.Headers, "Trailer"))
        {
            return ProgressiveSkipReason.Trailers;
        }

        var size = originWireBytes > 0 ? originWireBytes : plainBody?.LongLength ?? 0;
        if (plainBody is null || plainBody.Length == 0 || size < minWireBytes)
        {
            return size <= 0 || plainBody is null || plainBody.Length == 0
                ? ProgressiveSkipReason.NoBody
                : ProgressiveSkipReason.TooSmall;
        }

        var encoding = ContentEncoding(origin);
        if (encoding is not (null or "identity" or "gzip" or "deflate" or "br"))
        {
            return ProgressiveSkipReason.Encoding;
        }

        return ProgressiveSkipReason.None;
    }

    public static async Task WritePacedAsync(
        Stream destination,
        byte[] wire,
        long chargeWireBytes,
        Func<LinkShaper?> shaper,
        CancellationToken cancellationToken,
        int sliceBytes = SliceBytes,
        TimeSpan leadDelay = default)
    {
        if (wire.Length == 0)
        {
            return;
        }

        // Latency is paid here, on the writer's own task, so HTTP/2 siblings overlap their waits
        // instead of queueing behind one another on the connection's frame loop.
        if (leadDelay > TimeSpan.Zero && shaper() is { } lead)
        {
            await lead.WaitAsync(leadDelay, cancellationToken).ConfigureAwait(false);
        }

        if (sliceBytes <= 0)
        {
            sliceBytes = SliceBytes;
        }

        long charged = 0;
        var offset = 0;
        while (offset < wire.Length)
        {
            var count = Math.Min(sliceBytes, wire.Length - offset);
            var last = offset + count >= wire.Length;
            long sliceCharge;
            if (chargeWireBytes <= 0)
            {
                sliceCharge = count;
            }
            else if (last)
            {
                sliceCharge = Math.Max(0, chargeWireBytes - charged);
            }
            else
            {
                sliceCharge = chargeWireBytes * count / wire.Length;
            }

            charged += sliceCharge;
            var link = shaper();
            if (link is not null && sliceCharge > 0)
            {
                await link.WaitAsync(link.Reserve(ThrottleDirection.Download, sliceCharge), cancellationToken)
                    .ConfigureAwait(false);
            }

            await destination.WriteAsync(wire.AsMemory(offset, count), cancellationToken).ConfigureAwait(false);
            offset += count;
        }
    }

    internal static long ProgressiveCount => Interlocked.Read(ref _progressive);

    internal static long FallbackCount(ProgressiveSkipReason reason) => reason switch
    {
        ProgressiveSkipReason.TooSmall => Interlocked.Read(ref _fallbackTooSmall),
        ProgressiveSkipReason.Status => Interlocked.Read(ref _fallbackStatus),
        ProgressiveSkipReason.Head => Interlocked.Read(ref _fallbackHead),
        ProgressiveSkipReason.Upgrade => Interlocked.Read(ref _fallbackUpgrade),
        ProgressiveSkipReason.Trailers => Interlocked.Read(ref _fallbackTrailers),
        ProgressiveSkipReason.Encoding => Interlocked.Read(ref _fallbackEncoding),
        ProgressiveSkipReason.Transcoded => Interlocked.Read(ref _fallbackTranscoded),
        ProgressiveSkipReason.Edited => Interlocked.Read(ref _fallbackEdited),
        ProgressiveSkipReason.NoBody => Interlocked.Read(ref _fallbackNoBody),
        ProgressiveSkipReason.BuildFailed => Interlocked.Read(ref _fallbackBuildFailed),
        _ => 0,
    };

    private static void Record(ProgressiveSkipReason reason)
    {
        var count = reason switch
        {
            ProgressiveSkipReason.None => Interlocked.Increment(ref _progressive),
            ProgressiveSkipReason.TooSmall => Interlocked.Increment(ref _fallbackTooSmall),
            ProgressiveSkipReason.Status => Interlocked.Increment(ref _fallbackStatus),
            ProgressiveSkipReason.Head => Interlocked.Increment(ref _fallbackHead),
            ProgressiveSkipReason.Upgrade => Interlocked.Increment(ref _fallbackUpgrade),
            ProgressiveSkipReason.Trailers => Interlocked.Increment(ref _fallbackTrailers),
            ProgressiveSkipReason.Encoding => Interlocked.Increment(ref _fallbackEncoding),
            ProgressiveSkipReason.Transcoded => Interlocked.Increment(ref _fallbackTranscoded),
            ProgressiveSkipReason.Edited => Interlocked.Increment(ref _fallbackEdited),
            ProgressiveSkipReason.NoBody => Interlocked.Increment(ref _fallbackNoBody),
            ProgressiveSkipReason.BuildFailed => Interlocked.Increment(ref _fallbackBuildFailed),
            _ => 0,
        };
        Debug.WriteLine($"ProgressiveDelivery {reason} count={count}");
    }

    private static Response CopyResponse(Response origin, int wireLength)
    {
        var copy = new Response
        {
            HttpVersion = origin.HttpVersion,
            StatusCode = origin.StatusCode,
            StatusDescription = origin.StatusDescription,
        };
        var replacedLength = false;
        foreach (var header in origin.Headers)
        {
            if (header.Name.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var value = header.Name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase)
                ? wireLength.ToString()
                : header.Value;
            if (header.Name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
            {
                replacedLength = true;
            }

            copy.Headers.AddHeader(new HttpHeader(header.Name, value));
        }

        if (!replacedLength)
        {
            copy.Headers.AddHeader(new HttpHeader("Content-Length", wireLength.ToString()));
        }

        return copy;
    }

    private static string? ContentEncoding(Response origin)
    {
        var value = origin.ContentEncoding;
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        value = value.Trim();
        if (value.Contains(',', StringComparison.Ordinal))
        {
            return value;
        }

        return value.ToLowerInvariant();
    }

    private static byte[]? Encode(byte[] plain, string? encoding)
    {
        if (encoding is null or "identity")
        {
            return plain;
        }

        if (encoding is not ("gzip" or "deflate" or "br"))
        {
            return null;
        }

        // A growing MemoryStream plus ToArray peaks at several copies of the body. Compress into one pooled
        // buffer sized for the worst case instead, then copy out only the bytes produced. The stream cannot
        // grow, so a miscalculated bound throws and the caller falls back to the one-shot delay path.
        var bound = encoding == "br"
            ? BrotliEncoder.GetMaxCompressedLength(plain.Length)
            : plain.Length + (plain.Length / 64) + 4096;
        var rented = ArrayPool<byte>.Shared.Rent(bound);
        try
        {
            using var output = new MemoryStream(rented, 0, rented.Length, writable: true, publiclyVisible: true);
            Stream compressor = encoding switch
            {
                "gzip" => new GZipStream(output, CompressionLevel.Fastest, leaveOpen: true),
                "deflate" => new DeflateStream(output, CompressionLevel.Fastest, leaveOpen: true),
                _ => new BrotliStream(output, CompressionLevel.Fastest, leaveOpen: true),
            };
            using (compressor)
            {
                compressor.Write(plain, 0, plain.Length);
            }

            return rented.AsSpan(0, (int)output.Position).ToArray();
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    private static bool HasHeader(HeaderCollection headers, string name)
    {
        foreach (var header in headers)
        {
            if (header.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}

internal sealed class ProgressivePlan
{
    public ProgressivePlan(Response response, byte[] wire, long chargeWireBytes)
    {
        Response = response;
        Wire = wire;
        ChargeWireBytes = chargeWireBytes;
    }

    public Response Response { get; }
    public byte[] Wire { get; }
    public long ChargeWireBytes { get; }
}
