using System.Text;
using System.Text.Json;

namespace Titanium.Inspector.Services;

/// <summary>Expanded session capture used by inspectors, archive, and search.</summary>
public sealed class SessionSnapshot
{
    public long Id { get; init; }
    public string Method { get; init; } = "GET";
    public string Url { get; init; } = "";
    public int? StatusCode { get; init; }
    public DateTimeOffset StartedUtc { get; init; } = DateTimeOffset.UtcNow;
    public string? RequestHeadersText { get; init; }
    public string? ResponseHeadersText { get; init; }
    public string? RequestBodyText { get; init; }
    public string? ResponseBodyText { get; init; }
    public byte[]? RequestBodyBytes { get; init; }
    public byte[]? ResponseBodyBytes { get; init; }
    public bool IsWebSocket { get; init; }
    public bool IsGrpc { get; init; }
    public bool IsTunnel { get; init; }
    public bool IsMultipart { get; init; }
    public string? ContentType { get; init; }
    public IReadOnlyList<WebSocketFrameSnapshot>? WebSocketFrames { get; init; }
    public IReadOnlyList<GrpcFrameSnapshot>? GrpcFrames { get; init; }
    public IReadOnlyList<MultipartPartSnapshot>? MultipartParts { get; init; }
}

public sealed class WebSocketFrameSnapshot
{
    public string Direction { get; init; } = "Client";
    public string Opcode { get; init; } = "Text";
    public string? PayloadPreview { get; init; }
}

public sealed class GrpcFrameSnapshot
{
    public bool Compressed { get; init; }
    public int Length { get; init; }
    public string? HexPreview { get; init; }
}

public sealed class MultipartPartSnapshot
{
    public string? Name { get; init; }
    public string? ContentType { get; init; }
    public string? Preview { get; init; }
}
