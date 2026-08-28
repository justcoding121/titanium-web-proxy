using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Titanium.Inspector.Services;

/// <summary>Mutable session capture used by inspectors, archive, search, and the grid.</summary>
public sealed class SessionSnapshot : INotifyPropertyChanged
{
    private int? _statusCode;
    private string? _requestHeadersText;
    private string? _responseHeadersText;
    private string? _requestBodyText;
    private string? _responseBodyText;
    private byte[]? _requestBodyBytes;
    private byte[]? _responseBodyBytes;
    private string? _contentType;
    private string? _protocol;
    private string? _host;
    private long? _bodySize;
    private int _processId;
    private long _receivedBytes;
    private long _sentBytes;
    private string? _processName;
    private double? _durationMs;
    private double? _ttfbMs;

    public long Id { get; set; }
    public string Method { get; set; } = "GET";
    public string Url { get; set; } = "";
    public DateTimeOffset StartedUtc { get; set; } = DateTimeOffset.UtcNow;
    public bool IsWebSocket { get; set; }
    public bool IsGrpc { get; set; }
    public bool IsTunnel { get; set; }
    public bool IsMultipart { get; set; }
    public IReadOnlyList<WebSocketFrameSnapshot>? WebSocketFrames { get; set; }
    public IReadOnlyList<GrpcFrameSnapshot>? GrpcFrames { get; set; }
    public IReadOnlyList<MultipartPartSnapshot>? MultipartParts { get; set; }

    public int? StatusCode
    {
        get => _statusCode;
        set => SetField(ref _statusCode, value);
    }

    public string? RequestHeadersText
    {
        get => _requestHeadersText;
        set => SetField(ref _requestHeadersText, value);
    }

    public string? ResponseHeadersText
    {
        get => _responseHeadersText;
        set => SetField(ref _responseHeadersText, value);
    }

    public string? RequestBodyText
    {
        get => _requestBodyText;
        set => SetField(ref _requestBodyText, value);
    }

    public string? ResponseBodyText
    {
        get => _responseBodyText;
        set => SetField(ref _responseBodyText, value);
    }

    public byte[]? RequestBodyBytes
    {
        get => _requestBodyBytes;
        set => SetField(ref _requestBodyBytes, value);
    }

    public byte[]? ResponseBodyBytes
    {
        get => _responseBodyBytes;
        set => SetField(ref _responseBodyBytes, value);
    }

    public string? ContentType
    {
        get => _contentType;
        set => SetField(ref _contentType, value);
    }

    public string? Protocol
    {
        get => _protocol;
        set => SetField(ref _protocol, value);
    }

    public string? Host
    {
        get => _host;
        set => SetField(ref _host, value);
    }

    public long? BodySize
    {
        get => _bodySize;
        set => SetField(ref _bodySize, value);
    }

    public int ProcessId
    {
        get => _processId;
        set => SetField(ref _processId, value);
    }

    public string? ProcessName
    {
        get => _processName;
        set => SetField(ref _processName, value);
    }

    public long ReceivedBytes
    {
        get => _receivedBytes;
        set => SetField(ref _receivedBytes, value);
    }

    public long SentBytes
    {
        get => _sentBytes;
        set => SetField(ref _sentBytes, value);
    }

    public double? DurationMs
    {
        get => _durationMs;
        set => SetField(ref _durationMs, value);
    }

    public double? TtfbMs
    {
        get => _ttfbMs;
        set => SetField(ref _ttfbMs, value);
    }

    public string ProcessDisplay =>
        string.IsNullOrEmpty(ProcessName) ? (ProcessId > 0 ? ProcessId.ToString() : "") : $"{ProcessName}:{ProcessId}";

    public event PropertyChangedEventHandler? PropertyChanged;

    private void SetField<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (Equals(field, value))
        {
            return;
        }

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        if (name is nameof(ProcessId) or nameof(ProcessName))
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ProcessDisplay)));
        }
    }
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
