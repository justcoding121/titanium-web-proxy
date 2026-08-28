using System.Buffers.Binary;
using System.Text;

namespace Titanium.Inspector.Services;

/// <summary>WebSocket frame and gRPC length-prefixed frame inspectors.</summary>
public static class ProtocolFrameInspectors
{
    public static IReadOnlyList<WebSocketFrameSnapshot> ParseWebSocketFrames(byte[]? payload)
    {
        var list = new List<WebSocketFrameSnapshot>();
        if (payload is null || payload.Length == 0)
        {
            return list;
        }

        // Best-effort: treat text payloads as a single text frame preview.
        list.Add(new WebSocketFrameSnapshot
        {
            Direction = "Unknown",
            Opcode = "Text",
            PayloadPreview = Encoding.UTF8.GetString(payload, 0, Math.Min(payload.Length, 512)),
        });
        return list;
    }

    public static IReadOnlyList<GrpcFrameSnapshot> ParseGrpcFrames(byte[]? payload)
    {
        var list = new List<GrpcFrameSnapshot>();
        if (payload is null || payload.Length < 5)
        {
            return list;
        }

        var offset = 0;
        while (offset + 5 <= payload.Length)
        {
            var compressed = (payload[offset] & 1) != 0;
            var length = BinaryPrimitives.ReadInt32BigEndian(payload.AsSpan(offset + 1, 4));
            if (length < 0 || offset + 5 + length > payload.Length)
            {
                break;
            }

            var previewLen = Math.Min(length, 32);
            var hex = Convert.ToHexString(payload.AsSpan(offset + 5, previewLen));
            list.Add(new GrpcFrameSnapshot
            {
                Compressed = compressed,
                Length = length,
                HexPreview = hex,
            });
            offset += 5 + length;
        }

        return list;
    }

    public static IReadOnlyList<MultipartPartSnapshot> ParseMultipart(string? contentType, byte[]? body)
    {
        var list = new List<MultipartPartSnapshot>();
        if (body is null || contentType is null ||
            !contentType.Contains("multipart/", StringComparison.OrdinalIgnoreCase))
        {
            return list;
        }

        var boundaryIdx = contentType.IndexOf("boundary=", StringComparison.OrdinalIgnoreCase);
        if (boundaryIdx < 0)
        {
            return list;
        }

        var boundary = contentType[(boundaryIdx + 9)..].Trim().Trim('"');
        var text = Encoding.UTF8.GetString(body);
        var parts = text.Split("--" + boundary, StringSplitOptions.RemoveEmptyEntries);
        foreach (var part in parts)
        {
            if (part.StartsWith("--", StringComparison.Ordinal))
            {
                continue;
            }

            var headerEnd = part.IndexOf("\r\n\r\n", StringComparison.Ordinal);
            var headers = headerEnd > 0 ? part[..headerEnd] : "";
            var content = headerEnd > 0 ? part[(headerEnd + 4)..] : part;
            string? name = null;
            string? partCt = null;
            foreach (var line in headers.Split("\r\n", StringSplitOptions.RemoveEmptyEntries))
            {
                if (line.StartsWith("Content-Disposition:", StringComparison.OrdinalIgnoreCase))
                {
                    var n = line.IndexOf("name=\"", StringComparison.OrdinalIgnoreCase);
                    if (n >= 0)
                    {
                        var start = n + 6;
                        var end = line.IndexOf('"', start);
                        if (end > start)
                        {
                            name = line[start..end];
                        }
                    }
                }
                else if (line.StartsWith("Content-Type:", StringComparison.OrdinalIgnoreCase))
                {
                    partCt = line[13..].Trim();
                }
            }

            list.Add(new MultipartPartSnapshot
            {
                Name = name,
                ContentType = partCt,
                Preview = content.Length > 256 ? content[..256] + "…" : content.Trim(),
            });
        }

        return list;
    }
}
