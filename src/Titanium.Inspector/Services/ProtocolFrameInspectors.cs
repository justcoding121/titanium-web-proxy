using System.Text;
using System.Buffers.Binary;

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

        // Best-effort RFC6455 frame walk when bytes look framed; else single text preview.
        var offset = 0;
        var parsedAny = false;
        while (offset + 2 <= payload.Length)
        {
            var b0 = payload[offset];
            var b1 = payload[offset + 1];
            var opcode = b0 & 0x0F;
            var masked = (b1 & 0x80) != 0;
            ulong len = (ulong)(b1 & 0x7F);
            var header = 2;
            if (len == 126)
            {
                if (offset + 4 > payload.Length) break;
                len = BinaryPrimitives.ReadUInt16BigEndian(payload.AsSpan(offset + 2, 2));
                header = 4;
            }
            else if (len == 127)
            {
                if (offset + 10 > payload.Length) break;
                len = BinaryPrimitives.ReadUInt64BigEndian(payload.AsSpan(offset + 2, 8));
                header = 10;
            }

            if (masked)
            {
                header += 4;
            }

            if (offset + header + (int)len > payload.Length || len > int.MaxValue)
            {
                break;
            }

            var dataStart = offset + header;
            var data = payload.AsSpan(dataStart, (int)len).ToArray();
            if (masked)
            {
                var mask = payload.AsSpan(offset + header - 4, 4);
                for (var i = 0; i < data.Length; i++)
                {
                    data[i] ^= mask[i % 4];
                }
            }

            list.Add(new WebSocketFrameSnapshot
            {
                Direction = "Unknown",
                Opcode = OpcodeName(opcode),
                PayloadPreview = Preview(data, opcode),
            });
            parsedAny = true;
            offset = dataStart + (int)len;
        }

        if (!parsedAny)
        {
            list.Add(new WebSocketFrameSnapshot
            {
                Direction = "Unknown",
                Opcode = "Text",
                PayloadPreview = Encoding.UTF8.GetString(payload, 0, Math.Min(payload.Length, 512)),
            });
        }

        return list;
    }

    public static WebSocketFrameSnapshot FromLiveFrame(string direction, string opcode, byte[] data) =>
        new()
        {
            Direction = direction,
            Opcode = opcode,
            PayloadPreview = Preview(data, opcode.Equals("Binary", StringComparison.OrdinalIgnoreCase) ? 2 : 1),
        };

    private static string OpcodeName(int opcode) => opcode switch
    {
        0 => "Continuation",
        1 => "Text",
        2 => "Binary",
        8 => "Close",
        9 => "Ping",
        10 => "Pong",
        _ => "Op" + opcode,
    };

    private static string Preview(byte[] data, int opcode)
    {
        if (data.Length == 0)
        {
            return "";
        }

        if (opcode == 1)
        {
            var text = Encoding.UTF8.GetString(data);
            return text.Length > 512 ? text[..512] + "…" : text;
        }

        var hex = Convert.ToHexString(data.AsSpan(0, Math.Min(data.Length, 64)));
        return data.Length > 64 ? hex + "…" : hex;
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
        if (!TryGetBoundary(contentType, body, out var boundary, out var text))
        {
            return list;
        }

        var parts = text.Split("--" + boundary, StringSplitOptions.RemoveEmptyEntries);
        foreach (var part in parts)
        {
            if (part.StartsWith("--", StringComparison.Ordinal))
            {
                continue;
            }

            list.Add(ParseMultipartPart(part));
        }

        return list;
    }

    private static bool TryGetBoundary(string? contentType, byte[]? body, out string boundary, out string text)
    {
        boundary = "";
        text = "";
        if (body is null || contentType is null ||
            !contentType.Contains("multipart/", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var boundaryIdx = contentType.IndexOf("boundary=", StringComparison.OrdinalIgnoreCase);
        if (boundaryIdx < 0)
        {
            return false;
        }

        boundary = contentType[(boundaryIdx + 9)..].Trim().Trim('"');
        text = Encoding.UTF8.GetString(body);
        return true;
    }

    private static MultipartPartSnapshot ParseMultipartPart(string part)
    {
        var headerEnd = part.IndexOf("\r\n\r\n", StringComparison.Ordinal);
        var headers = headerEnd > 0 ? part[..headerEnd] : "";
        var content = headerEnd > 0 ? part[(headerEnd + 4)..] : part;
        string? name = null;
        string? partCt = null;
        foreach (var line in headers.Split("\r\n", StringSplitOptions.RemoveEmptyEntries))
        {
            if (line.StartsWith("Content-Disposition:", StringComparison.OrdinalIgnoreCase))
            {
                name = ExtractDispositionName(line) ?? name;
            }
            else if (line.StartsWith("Content-Type:", StringComparison.OrdinalIgnoreCase))
            {
                partCt = line[13..].Trim();
            }
        }

        return new MultipartPartSnapshot
        {
            Name = name,
            ContentType = partCt,
            Preview = content.Length > 256 ? content[..256] + "…" : content.Trim(),
        };
    }

    private static string? ExtractDispositionName(string line)
    {
        var n = line.IndexOf("name=\"", StringComparison.OrdinalIgnoreCase);
        if (n < 0)
        {
            return null;
        }

        var start = n + 6;
        var end = line.IndexOf('"', start);
        return end > start ? line[start..end] : null;
    }
}
