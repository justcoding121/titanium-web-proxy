using System.Buffers.Binary;
using System.Text;
using System.Text.Json;

namespace Titanium.Inspector.Services;

/// <summary>
/// Opt-in protobuf decode for inspect panes. Without a descriptor, returns a wire-format field dump.
/// </summary>
public static class ProtobufMessageDecoder
{
    private static readonly JsonSerializerOptions WireFormatJsonOptions = new() { WriteIndented = true };
    public static string DecodeWireFormat(byte[]? framedOrRaw, bool stripGrpcFrame = true) // NOSONAR S3776 -- Wire-format dump is a single protobuf walk.
    {
        if (framedOrRaw is null || framedOrRaw.Length == 0)
        {
            return "";
        }

        var payload = framedOrRaw.AsSpan();
        if (stripGrpcFrame && framedOrRaw.Length >= 5)
        {
            var length = BinaryPrimitives.ReadInt32BigEndian(framedOrRaw.AsSpan(1, 4));
            if (length >= 0 && length + 5 <= framedOrRaw.Length)
            {
                payload = framedOrRaw.AsSpan(5, length);
            }
        }

        var fields = new List<Dictionary<string, object?>>();
        var offset = 0;
        var bytes = payload.ToArray();
        while (offset < bytes.Length)
        {
            if (!TryReadVarint(bytes, ref offset, out var tag))
            {
                break;
            }

            var fieldNumber = (int)(tag >> 3);
            var wireType = (int)(tag & 0x7);
            object? value = null;
            switch (wireType)
            {
                case 0:
                    if (TryReadVarint(bytes, ref offset, out var v))
                    {
                        value = v;
                    }

                    break;
                case 1 when offset + 8 <= bytes.Length:
                    value = BitConverter.ToUInt64(bytes, offset);
                    offset += 8;
                    break;
                case 2:
                    value = ReadLengthDelimited(bytes, ref offset);
                    break;
                case 5 when offset + 4 <= bytes.Length:
                    value = BitConverter.ToUInt32(bytes, offset);
                    offset += 4;
                    break;
            }

            if (value is null)
            {
                break;
            }

            fields.Add(new Dictionary<string, object?>
            {
                ["field"] = fieldNumber,
                ["wireType"] = wireType,
                ["value"] = value,
            });
        }

        return JsonSerializer.Serialize(fields, WireFormatJsonOptions);
    }

    private static string? ReadLengthDelimited(byte[] payload, ref int offset)
    {
        if (!TryReadVarint(payload, ref offset, out var len) || len < 0 || offset + (int)len > payload.Length)
        {
            return null;
        }

        var slice = payload.AsSpan(offset, (int)len).ToArray();
        offset += (int)len;
        var text = Encoding.UTF8.GetString(slice);
        return text.All(c => !char.IsControl(c) || c is '\n' or '\r' or '\t')
            ? text
            : Convert.ToHexString(slice);
    }

    private static bool TryReadVarint(byte[] data, ref int offset, out ulong value)
    {
        value = 0;
        var shift = 0;
        while (offset < data.Length && shift < 64)
        {
            var b = data[offset++];
            value |= (ulong)(b & 0x7F) << shift;
            if ((b & 0x80) == 0)
            {
                return true;
            }

            shift += 7;
        }

        return false;
    }
}

/// <summary>SSE event parser for inspect tab.</summary>
public static class SseEventParser
{
    public static IReadOnlyList<SseEventSnapshot> Parse(string? text) // NOSONAR S3776 -- SSE event walk is a single line-oriented state machine.
    {
        var list = new List<SseEventSnapshot>();
        if (string.IsNullOrEmpty(text))
        {
            return list;
        }

        string? eventName = null;
        string? id = null;
        var data = new StringBuilder();
        foreach (var rawLine in text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            var line = rawLine;
            if (line.Length == 0)
            {
                Flush();
                continue;
            }

            if (line.StartsWith(':'))
            {
                continue;
            }

            var colon = line.IndexOf(':');
            var field = colon >= 0 ? line[..colon] : line;
            var value = colon >= 0 ? line[(colon + 1)..].TrimStart(' ') : "";
            switch (field)
            {
                case "event":
                    eventName = value;
                    break;
                case "id":
                    id = value;
                    break;
                case "data":
                    if (data.Length > 0)
                    {
                        data.Append('\n');
                    }

                    data.Append(value);
                    break;
            }
        }

        Flush();
        return list;

        void Flush()
        {
            if (data.Length == 0 && eventName is null && id is null)
            {
                return;
            }

            list.Add(new SseEventSnapshot
            {
                Event = eventName ?? "message",
                Id = id,
                Data = data.ToString().TrimEnd('\n'),
            });
            eventName = null;
            id = null;
            data.Clear();
        }
    }
}

public sealed class SseEventSnapshot
{
    public string Event { get; init; } = "message";
    public string? Id { get; init; }
    public string? Data { get; init; }
}

/// <summary>Named network throttle profiles for Inspector capture.</summary>
public static class NetworkThrottle
{
    public static NetworkThrottleProfile None { get; } = new("None", 0, 0);
    public static NetworkThrottleProfile Slow3G { get; } = new("Slow 3G", latencyMs: 400, bytesPerSecond: 50_000);
    public static NetworkThrottleProfile Fast3G { get; } = new("Fast 3G", latencyMs: 150, bytesPerSecond: 200_000);
    public static NetworkThrottleProfile LTE { get; } = new("LTE", latencyMs: 50, bytesPerSecond: 1_500_000);

    public static IReadOnlyList<NetworkThrottleProfile> Profiles { get; } =
        [None, Slow3G, Fast3G, LTE];

    public static NetworkThrottleProfile? Find(string? name) =>
        Profiles.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));

    public static TimeSpan DelayFor(NetworkThrottleProfile profile, int byteCount, bool applyLatency)
    {
        if (profile.BytesPerSecond <= 0 && profile.LatencyMs <= 0)
        {
            return TimeSpan.Zero;
        }

        var ms = applyLatency ? profile.LatencyMs : 0;
        if (profile.BytesPerSecond > 0 && byteCount > 0)
        {
            ms += (int)Math.Ceiling(byteCount * 1000.0 / profile.BytesPerSecond);
        }

        return TimeSpan.FromMilliseconds(Math.Max(0, ms));
    }
}

public sealed class NetworkThrottleProfile
{
    public NetworkThrottleProfile(string name, int latencyMs, int bytesPerSecond)
    {
        Name = name;
        LatencyMs = latencyMs;
        BytesPerSecond = bytesPerSecond;
    }

    public string Name { get; }
    public int LatencyMs { get; }
    public int BytesPerSecond { get; }
    public bool IsEnabled => LatencyMs > 0 || BytesPerSecond > 0;
}
