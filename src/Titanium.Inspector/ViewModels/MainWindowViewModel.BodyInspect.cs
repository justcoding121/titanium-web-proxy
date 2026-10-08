using System.ComponentModel;
using System.Text;
using System.Windows.Input;
using Avalonia.Media.Imaging;
using Titanium.Inspector.Services;

namespace Titanium.Inspector.ViewModels;

public sealed partial class MainWindowViewModel
{
    private const string ContentTypeHeaderName = "Content-Type";
    private const string EmptyBodyPlaceholder = "(empty)";
    private const string NoRequestBodyPlaceholder = "No request body";
    private const string WaitingForResponsePlaceholder = "Waiting for response…";
    private const string NoResponseReceivedPrefix = "No response received";
    private const string PrettyPrintFailureHint = "Cannot pretty-print (body truncated or invalid)";
    private const string ImageTooLargeHint = "Image too large to preview in Inspect";

    private bool _bodyPrettyMode = true;
    private bool _bodyHexMode;
    private string _requestBodyCaptureHint = "";
    private string _responseBodyCaptureHint = "";
    private Bitmap? _requestBodyPreviewBitmap;
    private Bitmap? _responseBodyPreviewBitmap;
    private string? _composerBodyFilePath;
    private string _composerBodyFromFileHint = "";
    private string? _cachedBodyText;
    private long? _cachedBodySessionId;
    private int _cachedBodyKey = -1;
    private BodyStamp _cachedBodyStamp;

    /// <summary>Inputs that change a rendered body. Compared by value so a stale cache is never reused.</summary>
    private readonly record struct BodyStamp(
        int TextLength, int BytesLength, int HeadersLength, BodyCaptureState Capture, bool Missing,
        bool Finished, string? Failure);

    public ICommand CopyRequestHeadersCommand { get; private set; } = null!;
    public ICommand CopyResponseHeadersCommand { get; private set; } = null!;
    public ICommand SaveRequestBodyCommand { get; private set; } = null!;
    public ICommand SaveResponseBodyCommand { get; private set; } = null!;
    public ICommand LoadComposerBodyFileCommand { get; private set; } = null!;

    public bool BodyPrettyMode
    {
        get => _bodyPrettyMode;
        set
        {
            if (SetField(ref _bodyPrettyMode, value) && _selected is not null)
            {
                RefreshBodyInspector();
            }
        }
    }

    /// <summary>Byte view of the body tabs. Pretty stays as set and is restored when this is cleared.</summary>
    public bool BodyHexMode
    {
        get => _bodyHexMode;
        set
        {
            if (!SetField(ref _bodyHexMode, value))
            {
                return;
            }

            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(BodyPrettyEnabled)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(BodyPrettyToolTip)));
            if (_selected is not null)
            {
                RefreshBodyInspector();
            }
        }
    }

    public bool BodyPrettyEnabled => !_bodyHexMode;

    /// <summary>Pretty and Hex only mean something for HTTP bodies, not for opaque CONNECT tunnels.</summary>
    public bool ShowBodyModeToggles => _selected is { IsTunnel: false };

    public string BodyPrettyToolTip =>
        _bodyHexMode ? "Not applicable in hex view" : "Indent JSON, XML, and HTML";

    public string RequestBodyCaptureHint
    {
        get => _requestBodyCaptureHint;
        private set
        {
            if (SetField(ref _requestBodyCaptureHint, value))
            {
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ShowRequestBodyCaptureHint)));
            }
        }
    }

    public bool ShowRequestBodyCaptureHint => !string.IsNullOrEmpty(_requestBodyCaptureHint);

    public string ResponseBodyCaptureHint
    {
        get => _responseBodyCaptureHint;
        private set
        {
            if (SetField(ref _responseBodyCaptureHint, value))
            {
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ShowResponseBodyCaptureHint)));
            }
        }
    }

    public bool ShowResponseBodyCaptureHint => !string.IsNullOrEmpty(_responseBodyCaptureHint);

    public Bitmap? RequestBodyPreviewBitmap
    {
        get => _requestBodyPreviewBitmap;
        private set => SetPreviewBitmap(isRequest: true, value);
    }

    public Bitmap? ResponseBodyPreviewBitmap
    {
        get => _responseBodyPreviewBitmap;
        private set => SetPreviewBitmap(isRequest: false, value);
    }

    public bool ShowRequestBodyPreviewImage => _requestBodyPreviewBitmap is not null;

    public bool ShowResponseBodyPreviewImage => _responseBodyPreviewBitmap is not null;

    public bool CanSaveRequestBody =>
        _selected is not null
        && (_selected.RequestBodyBytes is { Length: > 0 }
            || !string.IsNullOrEmpty(_selected.RequestBodyText))
        && _selected.RequestBodyCapture != BodyCaptureState.NotCaptured;

    public bool CanSaveResponseBody =>
        _selected is not null
        && (_selected.ResponseBodyBytes is { Length: > 0 }
            || !string.IsNullOrEmpty(_selected.ResponseBodyText))
        && _selected.ResponseBodyCapture != BodyCaptureState.NotCaptured;

    public string? ComposerBodyFilePath
    {
        get => _composerBodyFilePath;
        set
        {
            if (!SetField(ref _composerBodyFilePath, value))
            {
                return;
            }

            ComposerBodyFromFileHint = string.IsNullOrWhiteSpace(value)
                ? ""
                : $"Body from file: {value} (sent as stream; not loaded into the editor)";
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasComposerBodyFile)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ComposerBodyEditorEnabled)));
        }
    }

    public string ComposerBodyFromFileHint
    {
        get => _composerBodyFromFileHint;
        private set => SetField(ref _composerBodyFromFileHint, value);
    }

    public bool HasComposerBodyFile => !string.IsNullOrWhiteSpace(_composerBodyFilePath);

    public bool ComposerBodyEditorEnabled => !HasComposerBodyFile;

    private void WireBodyInspectCommands()
    {
        CopyRequestHeadersCommand = Cmd(() =>
            CopyInspectTextAsync(SelectedRequestHeaders, "Headers copied", "No headers to copy"));
        CopyResponseHeadersCommand = Cmd(() =>
            CopyInspectTextAsync(SelectedResponseHeaders, "Headers copied", "No headers to copy"));
        SaveRequestBodyCommand = Cmd(() => SaveBodyAsync(isRequest: true));
        SaveResponseBodyCommand = Cmd(() => SaveBodyAsync(isRequest: false));
        LoadComposerBodyFileCommand = Cmd(LoadComposerBodyFileAsync);
    }

    private Task CopyInspectTextAsync(string text, string success, string empty)
    {
        if (string.IsNullOrEmpty(text))
        {
            SetGuardStatus(empty);
            return Task.CompletedTask;
        }

        return CopyInspectTextToClipboardAsync(text, success);
    }

    private async Task CopyInspectTextToClipboardAsync(string text, string success)
    {
        await CopyTextToClipboardAsync(text).ConfigureAwait(false);
        await MarshalToUiAsync(() => SetOutcomeStatus(success, StatusSeverity.Success), StatusCancelToken)
            .ConfigureAwait(false);
    }

    private async Task SaveBodyAsync(bool isRequest)
    {
        if (_selected is null)
        {
            SetGuardStatus("Select a session first");
            return;
        }

        await _store.EnsureBodiesLoadedAsync(_selected, StatusCancelToken).ConfigureAwait(false);
        if (!TryGetSaveableBody(_selected, isRequest, out var bytes, out var capture, out var original))
        {
            SetGuardStatus("Body not captured — nothing to save");
            return;
        }

        var headers = SessionInspectors.ParseHeaderBlock(
            isRequest ? _selected.RequestHeadersText : _selected.ResponseHeadersText);
        headers.TryGetValue("Content-Disposition", out var disposition);
        headers.TryGetValue(ContentTypeHeaderName, out var contentType);
        var suggested = InspectorBodyLimits.SuggestBodyFileName(
            _selected.Url, disposition, contentType ?? _selected.ContentType, isRequest);

        var path = await _pathPicker.PickSavePathAsync(
            isRequest ? "Save request body" : "Save response body",
            suggested,
            "All files",
            "*.*").ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        await File.WriteAllBytesAsync(path, bytes, StatusCancelToken).ConfigureAwait(false);
        await ReportBodySavedAsync(bytes, capture, original).ConfigureAwait(false);
    }

    private static bool TryGetSaveableBody(
        SessionSnapshot selected,
        bool isRequest,
        out byte[] bytes,
        out BodyCaptureState capture,
        out long? original)
    {
        var rawBytes = isRequest ? selected.RequestBodyBytes : selected.ResponseBodyBytes;
        var text = isRequest ? selected.RequestBodyText : selected.ResponseBodyText;
        capture = isRequest ? selected.RequestBodyCapture : selected.ResponseBodyCapture;
        original = isRequest ? selected.RequestBodyOriginalSize : selected.ResponseBodyOriginalSize;

        if (capture == BodyCaptureState.NotCaptured
            || (rawBytes is null or { Length: 0 } && string.IsNullOrEmpty(text)))
        {
            bytes = [];
            return false;
        }

        bytes = rawBytes ?? Encoding.UTF8.GetBytes(text ?? "");
        return true;
    }

    private Task ReportBodySavedAsync(byte[] bytes, BodyCaptureState capture, long? original)
    {
        var incomplete = capture is BodyCaptureState.Truncated or BodyCaptureState.Streaming
                         || (original is long o && o > bytes.Length);
        return MarshalToUiAsync(() =>
        {
            SetOutcomeStatus(
                incomplete
                    ? $"Saved incomplete body ({SessionDisplayFormat.FormatByteSize(bytes.Length)} of {SessionDisplayFormat.FormatByteSize(original ?? bytes.Length)})"
                    : $"Saved body ({SessionDisplayFormat.FormatByteSize(bytes.Length)})",
                incomplete ? StatusSeverity.Warning : StatusSeverity.Success,
                toastImportant: incomplete);
        }, StatusCancelToken);
    }

    private async Task LoadComposerBodyFileAsync()
    {
        var path = await _pathPicker.PickOpenPathAsync("Load body from file", "All files", "*.*")
            .ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        var info = new FileInfo(path);
        if (!info.Exists)
        {
            SetGuardStatus("File not found");
            return;
        }

        if (info.Length <= InspectorBodyLimits.MaxBodyBytes)
        {
            var text = await File.ReadAllTextAsync(path, StatusCancelToken).ConfigureAwait(false);
            await MarshalToUiAsync(() =>
            {
                ComposerBodyFilePath = null;
                ComposerBody = text;
                if (text.Length > InspectorBodyLimits.MaxInlineToolBodyChars)
                {
                    SetOutcomeStatus(
                        $"Body is large ({SessionDisplayFormat.FormatByteSize(text.Length)}); editor may feel slow",
                        StatusSeverity.Warning);
                }
                else
                {
                    SetOutcomeStatus("Composer body loaded from file", StatusSeverity.Success);
                }
            }, StatusCancelToken).ConfigureAwait(false);
            return;
        }

        await MarshalToUiAsync(() =>
        {
            ComposerBody = "";
            ComposerBodyFilePath = path;
            SetOutcomeStatus(
                $"Large file will be streamed on Send ({SessionDisplayFormat.FormatByteSize(info.Length)})",
                StatusSeverity.Success);
        }, StatusCancelToken).ConfigureAwait(false);
    }

    private void RefreshBodyInspector()
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ShowBodyModeToggles)));
        if (_selected is null)
        {
            SelectedRequestBody = "";
            SelectedResponseBody = "";
            RequestBodyCaptureHint = "";
            ResponseBodyCaptureHint = "";
            RequestBodyPreviewBitmap = null;
            ResponseBodyPreviewBitmap = null;
            NotifySaveBodyCanExecute();
            return;
        }

        RequestBodyCaptureHint = BuildSideCaptureHint(_selected, isRequest: true, _bodyHexMode);
        ResponseBodyCaptureHint = BuildSideCaptureHint(_selected, isRequest: false, _bodyHexMode);
        UpdateBodyPreviewImages(_selected);
        NotifySaveBodyCanExecute();

        if (_selectedInspectTabIndex == (int)InspectTab.RequestBody)
        {
            SelectedRequestBody = InspectorDisplayText.ForTextBox(BuildSelectedBodyText(_selected, isRequest: true));
        }
        else if (_selectedInspectTabIndex == (int)InspectTab.ResponseBody)
        {
            SelectedResponseBody = InspectorDisplayText.ForTextBox(BuildSelectedBodyText(_selected, isRequest: false));
        }
    }

    private void NotifySaveBodyCanExecute()
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CanSaveRequestBody)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CanSaveResponseBody)));
    }

    /// <summary>Shown in Inspect when the body spill file was removed by the disk-cache budget.</summary>
    public const string BodiesMissingFromDiskHint =
        "Saved session data removed — disk cache limit reached. Headers in the list are still available. Raise the limit under Options → Session retention…";

    private static string BuildSideCaptureHint(SessionSnapshot selected, bool isRequest, bool hex)
    {
        if (BodiesMissing(selected))
        {
            return BodiesMissingFromDiskHint;
        }

        if (selected.IsTunnel)
        {
            // The body box already explains the tunnel (FormatTunnelBodyInspectText); no banner needed.
            return "";
        }

        var capture = isRequest ? selected.RequestBodyCapture : selected.ResponseBodyCapture;
        var original = isRequest
            ? selected.RequestBodyOriginalSize
            : selected.ResponseBodyOriginalSize ?? selected.BodySize;
        var captured = isRequest
            ? selected.RequestBodyBytes?.Length ?? selected.RequestBodyText?.Length ?? 0
            : selected.ResponseBodyBytes?.Length ?? selected.ResponseBodyText?.Length ?? 0;
        var streamOpen = !isRequest && selected.ResponseBodyStreamOpen;
        return InspectorBodyLimits.FormatCaptureBanner(capture, original, captured, streamOpen, forHex: hex);
    }

    private void UpdateBodyPreviewImages(SessionSnapshot selected)
    {
        RequestBodyPreviewBitmap = TryCreatePreviewBitmap(selected, isRequest: true);
        ResponseBodyPreviewBitmap = TryCreatePreviewBitmap(selected, isRequest: false);
    }

    private Bitmap? TryCreatePreviewBitmap(SessionSnapshot selected, bool isRequest)
    {
        if (!TryResolveSidePreview(selected, isRequest, out var bytes, out var contentType)
            || !InspectorBodyLimits.IsImageContentType(contentType))
        {
            return null;
        }

        try
        {
            using var ms = new MemoryStream(bytes);
            var bitmap = new Bitmap(ms);
            if (bitmap.PixelSize.Width > InspectorBodyLimits.MaxDecodedImageEdgePx
                || bitmap.PixelSize.Height > InspectorBodyLimits.MaxDecodedImageEdgePx
                || (long)bitmap.PixelSize.Width * bitmap.PixelSize.Height > InspectorBodyLimits.MaxDecodedImagePixels)
            {
                bitmap.Dispose();
                if (string.IsNullOrEmpty(CaptureHint(isRequest)))
                {
                    SetCaptureHint(isRequest, ImageTooLargeHint);
                }

                return null;
            }

            return bitmap;
        }
        catch
        {
            return null;
        }
    }

    private static bool TryResolveSidePreview(
        SessionSnapshot selected, bool isRequest, out byte[] bytes, out string? contentType)
    {
        bytes = [];
        contentType = null;
        if (isRequest)
        {
            if (LooksLikeImageHeaders(selected.RequestHeadersText)
                && selected.RequestBodyBytes is { Length: > 0 } requestBytes)
            {
                bytes = requestBytes;
                contentType = HeaderContentType(selected.RequestHeadersText);
                return true;
            }

            return false;
        }

        if ((InspectorBodyLimits.IsImageContentType(selected.ContentType)
                || LooksLikeImageHeaders(selected.ResponseHeadersText))
            && selected.ResponseBodyBytes is { Length: > 0 } responseBytes)
        {
            bytes = responseBytes;
            contentType = HeaderContentType(selected.ResponseHeadersText) ?? selected.ContentType;
            return true;
        }

        return false;
    }

    private static bool LooksLikeImageHeaders(string? headersText)
    {
        var headers = SessionInspectors.ParseHeaderBlock(headersText);
        return headers.TryGetValue(ContentTypeHeaderName, out var ct)
               && InspectorBodyLimits.IsImageContentType(ct);
    }

    private string BuildSelectedBodyText(SessionSnapshot selected, bool isRequest)
    {
        var onTab = IsSelectedBodyTab(isRequest);
        var pretty = _bodyPrettyMode && !_bodyHexMode && onTab;
        var stamp = StampFor(selected, isRequest);
        var key = BodyCacheKey(isRequest, pretty, _bodyHexMode);
        if (_cachedBodySessionId == selected.Id
            && _cachedBodyKey == key
            && _cachedBodyStamp == stamp
            && _cachedBodyText is not null)
        {
            if (pretty)
            {
                MaybeSetPrettyPrintFailureHint(selected, isRequest);
            }

            return _cachedBodyText;
        }

        var body = BuildSelectedBodyTextCore(selected, isRequest, pretty, _bodyHexMode);
        if (pretty)
        {
            MaybeSetPrettyPrintFailureHint(selected, isRequest);
        }

        body = AppendTranscodePrefix(selected, body, isRequest, _bodyHexMode);
        _cachedBodySessionId = selected.Id;
        _cachedBodyKey = key;
        _cachedBodyStamp = stamp;
        _cachedBodyText = body;
        return body;
    }

    private static string BuildSelectedBodyTextCore(SessionSnapshot selected, bool isRequest, bool pretty, bool hex)
    {
        if (BodiesMissing(selected))
        {
            return "(session data removed — disk cache limit reached)";
        }

        if (selected.IsTunnel)
        {
            return FormatTunnelBodyInspectText(selected);
        }

        var headers = isRequest ? selected.RequestHeadersText : selected.ResponseHeadersText;
        var text = isRequest ? selected.RequestBodyText : selected.ResponseBodyText;
        var bytes = isRequest ? selected.RequestBodyBytes : selected.ResponseBodyBytes;
        if (TryFormatHexBody(headers, text, bytes, hex) is { } hexText)
        {
            return hexText;
        }

        if (SideIsImage(selected, isRequest))
        {
            return FormatImageBodyInspectText(selected, isRequest);
        }

        if (isRequest && RequestBodyAbsent(selected))
        {
            return NoRequestBodyPlaceholder;
        }

        if (!isRequest && ResponseNotStarted(selected))
        {
            return NoResponseText(selected);
        }

        if (hex)
        {
            return EmptyBodyPlaceholder;
        }

        var raw = SessionInspectors.FormatBody(headers, text, bytes);
        if (!pretty)
        {
            return raw;
        }

        return InspectorBodyLimits.TryPrettyPrint(text, ContentTypeFor(selected, isRequest)) ?? raw;
    }

    /// <summary>
    ///     Hex of a side that has bytes (raw, or UTF-8 of text-only captures). Null when hex is off
    ///     or there is nothing to dump, so later placeholders (image, no body, no response) still apply.
    /// </summary>
    private static string? TryFormatHexBody(string? headers, string? text, byte[]? bytes, bool hex)
    {
        if (!hex)
        {
            return null;
        }

        if (bytes is not { Length: > 0 })
        {
            bytes = string.IsNullOrEmpty(text) ? null : Encoding.UTF8.GetBytes(text);
        }

        return bytes is { Length: > 0 } ? SessionInspectors.FormatHex(headers, bytes) : null;
    }

    private static string MissingImageBodyText(SessionSnapshot selected, bool isRequest)
    {
        if (isRequest)
        {
            return NoRequestBodyPlaceholder;
        }

        return ResponseNotStarted(selected) ? NoResponseText(selected) : EmptyBodyPlaceholder;
    }

    private static string FormatTunnelBodyInspectText(SessionSnapshot selected)
    {
        var wire = selected.BodySize ?? selected.SentBytes + selected.ReceivedBytes;
        var sb = new StringBuilder();
        sb.AppendLine("CONNECT tunnel — no HTTP message body.");
        sb.Append("Encrypted traffic on the wire: ");
        sb.Append(SessionDisplayFormat.FormatByteSize(wire));
        sb.Append(" (sent ");
        sb.Append(SessionDisplayFormat.FormatByteSize(selected.SentBytes));
        sb.Append(", received ");
        sb.Append(SessionDisplayFormat.FormatByteSize(selected.ReceivedBytes));
        sb.Append(')');
        return sb.ToString();
    }

    private static string FormatImageBodyInspectText(SessionSnapshot selected, bool isRequest)
    {
        var bytes = isRequest ? selected.RequestBodyBytes : selected.ResponseBodyBytes;
        if (bytes is not { Length: > 0 })
        {
            return MissingImageBodyText(selected, isRequest);
        }

        var size = SessionDisplayFormat.FormatByteSize(bytes.Length);
        return isRequest
            ? $"(image · {size})"
            : $"(image · {size} — see preview above)";
    }

    private void MaybeSetPrettyPrintFailureHint(SessionSnapshot selected, bool isRequest)
    {
        var text = isRequest ? selected.RequestBodyText : selected.ResponseBodyText;
        var capture = isRequest ? selected.RequestBodyCapture : selected.ResponseBodyCapture;
        var ct = ContentTypeFor(selected, isRequest);
        if (!InspectorBodyLimits.IsPrettyPrintableContentType(ct)
            || InspectorBodyLimits.TryPrettyPrint(text, ct) is not null
            || !(capture is BodyCaptureState.Truncated || !string.IsNullOrWhiteSpace(text)))
        {
            return;
        }

        var hint = CaptureHint(isRequest);
        if (string.IsNullOrEmpty(hint))
        {
            SetCaptureHint(isRequest, PrettyPrintFailureHint);
        }
        else if (!hint.Contains("pretty-print", StringComparison.OrdinalIgnoreCase))
        {
            SetCaptureHint(isRequest, hint + " · " + PrettyPrintFailureHint);
        }
    }

    private static string AppendTranscodePrefix(SessionSnapshot selected, string body, bool isRequest, bool hex)
    {
        if (!selected.IsTranscoded || hex)
        {
            return body;
        }

        var sb = new StringBuilder();
        sb.AppendLine(isRequest ? "=== Client (JSON/REST) ===" : "=== Client response (JSON) ===");
        sb.AppendLine(string.IsNullOrEmpty(body) ? EmptyBodyPlaceholder : body);
        var upstream = isRequest ? selected.UpstreamRequestBodyBytes : selected.UpstreamResponseBodyBytes;
        if (upstream is { Length: > 0 })
        {
            sb.AppendLine();
            sb.AppendLine("=== Upstream gRPC frames (see Hex view) ===");
            if (selected.GrpcFrames is { Count: > 0 } frames)
            {
                foreach (var frame in frames)
                {
                    sb.Append("frame compressed=").Append(frame.Compressed)
                        .Append(" len=").Append(frame.Length)
                        .Append(" preview=").AppendLine(frame.HexPreview);
                }
            }
        }

        return sb.ToString().TrimEnd();
    }

    private void SetPreviewBitmap(bool isRequest, Bitmap? value)
    {
        if (isRequest)
        {
            var previous = _requestBodyPreviewBitmap;
            if (!SetField(ref _requestBodyPreviewBitmap, value, nameof(RequestBodyPreviewBitmap)))
            {
                return;
            }

            previous?.Dispose();
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ShowRequestBodyPreviewImage)));
            return;
        }

        var previousResponse = _responseBodyPreviewBitmap;
        if (!SetField(ref _responseBodyPreviewBitmap, value, nameof(ResponseBodyPreviewBitmap)))
        {
            return;
        }

        previousResponse?.Dispose();
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ShowResponseBodyPreviewImage)));
    }

    private string CaptureHint(bool isRequest) =>
        isRequest ? _requestBodyCaptureHint : _responseBodyCaptureHint;

    private void SetCaptureHint(bool isRequest, string value)
    {
        if (isRequest)
        {
            RequestBodyCaptureHint = value;
        }
        else
        {
            ResponseBodyCaptureHint = value;
        }
    }

    private void ClearBodyInspectCache()
    {
        _cachedBodyText = null;
        _cachedBodySessionId = null;
        _cachedBodyKey = -1;
        _cachedBodyStamp = default;
    }

    private bool IsSelectedBodyTab(bool isRequest) =>
        _selectedInspectTabIndex == (isRequest ? (int)InspectTab.RequestBody : (int)InspectTab.ResponseBody);

    private static bool BodiesMissing(SessionSnapshot selected) =>
        selected.BodiesMissingFromDisk
        && selected.RequestBodyBytes is null
        && selected.ResponseBodyBytes is null
        && selected.RequestBodyText is null
        && selected.ResponseBodyText is null;

    private static bool RequestBodyAbsent(SessionSnapshot selected) =>
        string.IsNullOrEmpty(selected.RequestBodyText)
        && selected.RequestBodyBytes is null or { Length: 0 };

    /// <summary>
    ///     Placeholder for a side with no response: "Waiting…" while the session is still in flight, a
    ///     "No response received — reason" once it has ended without one (origin refused/reset, timeout,
    ///     client gave up, capture stopped).
    /// </summary>
    internal static string NoResponseText(SessionSnapshot selected)
    {
        if (selected.DurationMs is null && string.IsNullOrEmpty(selected.FailureReason))
        {
            return WaitingForResponsePlaceholder;
        }

        return string.IsNullOrEmpty(selected.FailureReason)
            ? NoResponseReceivedPrefix
            : $"{NoResponseReceivedPrefix} — {selected.FailureReason}";
    }

    internal static bool IsNoResponseText(string? text) =>
        text is not null
        && (text == WaitingForResponsePlaceholder
            || text.StartsWith(NoResponseReceivedPrefix, StringComparison.Ordinal));

    private static bool ResponseNotStarted(SessionSnapshot selected) =>
        selected.StatusCode is null or 0
        && string.IsNullOrEmpty(selected.ResponseHeadersText)
        && string.IsNullOrEmpty(selected.ResponseBodyText)
        && selected.ResponseBodyBytes is null or { Length: 0 }
        && selected.ResponseBodyCapture == BodyCaptureState.None
        && !selected.ResponseBodyStreamOpen;

    private static bool SideIsImage(SessionSnapshot selected, bool isRequest) =>
        isRequest
            ? LooksLikeImageHeaders(selected.RequestHeadersText)
            : InspectorBodyLimits.IsImageContentType(selected.ContentType)
              || LooksLikeImageHeaders(selected.ResponseHeadersText);

    private static string? ContentTypeFor(SessionSnapshot selected, bool isRequest)
    {
        var fromHeaders = HeaderContentType(isRequest ? selected.RequestHeadersText : selected.ResponseHeadersText);
        if (!string.IsNullOrEmpty(fromHeaders))
        {
            return fromHeaders;
        }

        return isRequest ? null : selected.ContentType;
    }

    private static string? HeaderContentType(string? headersText)
    {
        var headers = SessionInspectors.ParseHeaderBlock(headersText);
        return headers.TryGetValue(ContentTypeHeaderName, out var ct) ? ct : null;
    }

    private static BodyStamp StampFor(SessionSnapshot selected, bool isRequest)
    {
        var text = isRequest ? selected.RequestBodyText : selected.ResponseBodyText;
        var bytes = isRequest ? selected.RequestBodyBytes : selected.ResponseBodyBytes;
        var headers = isRequest ? selected.RequestHeadersText : selected.ResponseHeadersText;
        var capture = isRequest ? selected.RequestBodyCapture : selected.ResponseBodyCapture;
        return new BodyStamp(
            text?.Length ?? -1,
            bytes?.Length ?? -1,
            headers?.Length ?? -1,
            capture,
            selected.BodiesMissingFromDisk,
            selected.DurationMs is not null,
            selected.FailureReason);
    }

    private static int BodyCacheKey(bool isRequest, bool pretty, bool hex) =>
        (isRequest ? 1 : 0) | (pretty ? 2 : 0) | (hex ? 4 : 0);
}
