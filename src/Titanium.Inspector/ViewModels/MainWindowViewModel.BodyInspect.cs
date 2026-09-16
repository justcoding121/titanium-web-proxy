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

    private bool _bodyPrettyMode = true;
    private string _bodyCaptureHint = "";
    private string _hexCaptureHint = "";
    private Bitmap? _bodyPreviewBitmap;
    private string? _composerBodyFilePath;
    private string _composerBodyFromFileHint = "";
    private string? _cachedPrettyBody;
    private long? _cachedPrettySessionId;

    public ICommand CopyHeadersCommand { get; private set; } = null!;
    public ICommand SetBodyPrettyCommand { get; private set; } = null!;
    public ICommand SetBodyRawCommand { get; private set; } = null!;
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

    public string BodyCaptureHint
    {
        get => _bodyCaptureHint;
        private set
        {
            if (SetField(ref _bodyCaptureHint, value))
            {
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ShowBodyCaptureHint)));
            }
        }
    }

    public bool ShowBodyCaptureHint => !string.IsNullOrEmpty(_bodyCaptureHint);

    public string HexCaptureHint
    {
        get => _hexCaptureHint;
        private set
        {
            if (SetField(ref _hexCaptureHint, value))
            {
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ShowHexCaptureHint)));
            }
        }
    }

    public bool ShowHexCaptureHint => !string.IsNullOrEmpty(_hexCaptureHint);

    public Bitmap? BodyPreviewBitmap
    {
        get => _bodyPreviewBitmap;
        private set
        {
            var previous = _bodyPreviewBitmap;
            if (!SetField(ref _bodyPreviewBitmap, value))
            {
                return;
            }

            previous?.Dispose();
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ShowBodyPreviewImage)));
        }
    }

    public bool ShowBodyPreviewImage => _bodyPreviewBitmap is not null;

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
        CopyHeadersCommand = Cmd(CopyHeadersAsync);
        SetBodyPrettyCommand = Cmd(() =>
        {
            BodyPrettyMode = true;
            return Task.CompletedTask;
        });
        SetBodyRawCommand = Cmd(() =>
        {
            BodyPrettyMode = false;
            return Task.CompletedTask;
        });
        SaveRequestBodyCommand = Cmd(() => SaveBodyAsync(isRequest: true));
        SaveResponseBodyCommand = Cmd(() => SaveBodyAsync(isRequest: false));
        LoadComposerBodyFileCommand = Cmd(LoadComposerBodyFileAsync);
    }

    private Task CopyHeadersAsync()
    {
        if (string.IsNullOrEmpty(SelectedHeaders))
        {
            SetGuardStatus("No headers to copy");
            return Task.CompletedTask;
        }

        return CopyHeadersToClipboardAsync();
    }

    private async Task CopyHeadersToClipboardAsync()
    {
        await CopyTextToClipboardAsync(SelectedHeaders).ConfigureAwait(false);
        await MarshalToUiAsync(() => SetOutcomeStatus("Headers copied", StatusSeverity.Success), StatusCancelToken)
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
        if (_selected is null)
        {
            SelectedBody = "";
            BodyCaptureHint = "";
            HexCaptureHint = "";
            BodyPreviewBitmap = null;
            NotifySaveBodyCanExecute();
            return;
        }

        BodyCaptureHint = BuildBodyCaptureHint(_selected);
        HexCaptureHint = BuildHexCaptureHint(_selected);
        SelectedBody = BuildSelectedBodyText(_selected);
        UpdateBodyPreviewImage(_selected);
        NotifySaveBodyCanExecute();
    }

    private void NotifySaveBodyCanExecute()
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CanSaveRequestBody)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CanSaveResponseBody)));
    }

    private static string BuildBodyCaptureHint(SessionSnapshot selected)
    {
        var req = InspectorBodyLimits.FormatCaptureBanner(
            selected.RequestBodyCapture,
            selected.RequestBodyOriginalSize,
            selected.RequestBodyBytes?.Length ?? selected.RequestBodyText?.Length ?? 0,
            streamOpen: false,
            forHex: false);
        var resp = InspectorBodyLimits.FormatCaptureBanner(
            selected.ResponseBodyCapture,
            selected.ResponseBodyOriginalSize ?? selected.BodySize,
            selected.ResponseBodyBytes?.Length ?? selected.ResponseBodyText?.Length ?? 0,
            selected.ResponseBodyStreamOpen,
            forHex: false);

        if (string.IsNullOrEmpty(req) && string.IsNullOrEmpty(resp))
        {
            return "";
        }

        if (string.IsNullOrEmpty(req))
        {
            return "Response: " + resp;
        }

        if (string.IsNullOrEmpty(resp))
        {
            return "Request: " + req;
        }

        return "Request: " + req + " · Response: " + resp;
    }

    private static string BuildHexCaptureHint(SessionSnapshot selected)
    {
        var respBytes = selected.ResponseBodyBytes?.Length ?? 0;
        var reqBytes = selected.RequestBodyBytes?.Length ?? 0;
        var captured = Math.Max(respBytes, reqBytes);
        if (captured <= 0)
        {
            return "";
        }

        return InspectorBodyLimits.FormatCaptureBanner(
            selected.ResponseBodyCapture != BodyCaptureState.None
                ? selected.ResponseBodyCapture
                : selected.RequestBodyCapture,
            selected.ResponseBodyOriginalSize ?? selected.RequestBodyOriginalSize ?? selected.BodySize,
            captured,
            selected.ResponseBodyStreamOpen,
            forHex: true);
    }

    private void UpdateBodyPreviewImage(SessionSnapshot selected)
    {
        if (!TryResolvePreviewImage(selected, out var bytes, out var contentType)
            || !InspectorBodyLimits.IsImageContentType(contentType))
        {
            BodyPreviewBitmap = null;
            return;
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
                BodyPreviewBitmap = null;
                if (string.IsNullOrEmpty(BodyCaptureHint))
                {
                    BodyCaptureHint = "Image too large to preview in Inspect";
                }

                return;
            }

            BodyPreviewBitmap = bitmap;
        }
        catch
        {
            BodyPreviewBitmap = null;
        }
    }

    private static bool TryResolvePreviewImage(
        SessionSnapshot selected, out byte[] bytes, out string? contentType)
    {
        bytes = [];
        contentType = null;
        if (InspectorBodyLimits.IsImageContentType(selected.ContentType)
            || LooksLikeImageHeaders(selected.ResponseHeadersText))
        {
            if (selected.ResponseBodyBytes is { Length: > 0 } responseBytes)
            {
                bytes = responseBytes;
                contentType = selected.ContentType;
                if (SessionInspectors.ParseHeaderBlock(selected.ResponseHeadersText)
                    .TryGetValue(ContentTypeHeaderName, out var responseType))
                {
                    contentType = responseType;
                }

                return true;
            }
        }

        if (LooksLikeImageHeaders(selected.RequestHeadersText)
            && selected.RequestBodyBytes is { Length: > 0 } requestBytes)
        {
            bytes = requestBytes;
            contentType = SessionInspectors.ParseHeaderBlock(selected.RequestHeadersText)
                .TryGetValue(ContentTypeHeaderName, out var requestType)
                ? requestType
                : contentType;
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

    private static string BuildSelectedBodyTextCore(SessionSnapshot selected, bool pretty)
    {
        if (InspectorBodyLimits.IsImageContentType(selected.ContentType)
            || LooksLikeImageHeaders(selected.ResponseHeadersText)
            || LooksLikeImageHeaders(selected.RequestHeadersText))
        {
            return FormatImageBodyInspectText(selected);
        }

        var raw = SessionInspectors.FormatLabeledBody(
            selected.RequestHeadersText,
            selected.ResponseHeadersText,
            selected.RequestBodyText,
            selected.ResponseBodyText,
            selected.RequestBodyBytes,
            selected.ResponseBodyBytes);

        if (!pretty)
        {
            return raw;
        }

        return TryFormatPrettyBodyText(selected, raw);
    }

    private static string FormatImageBodyInspectText(SessionSnapshot selected)
    {
        var sb = new StringBuilder();
        sb.AppendLine("=== Request ===");
        sb.AppendLine(selected.RequestBodyBytes is { Length: > 0 }
            ? $"(image · {SessionDisplayFormat.FormatByteSize(selected.RequestBodyBytes.Length)})"
            : EmptyBodyPlaceholder);
        sb.AppendLine();
        sb.AppendLine("=== Response ===");
        sb.Append(selected.ResponseBodyBytes is { Length: > 0 }
            ? $"(image · {SessionDisplayFormat.FormatByteSize(selected.ResponseBodyBytes.Length)} — see preview above)"
            : EmptyBodyPlaceholder);
        return sb.ToString();
    }

    private static string TryFormatPrettyBodyText(SessionSnapshot selected, string raw)
    {
        var reqCt = selected.ContentType;
        if (SessionInspectors.ParseHeaderBlock(selected.RequestHeadersText)
            .TryGetValue(ContentTypeHeaderName, out var requestCt))
        {
            reqCt = requestCt;
        }

        string? respCt = selected.ContentType;
        if (SessionInspectors.ParseHeaderBlock(selected.ResponseHeadersText)
            .TryGetValue(ContentTypeHeaderName, out var responseCt))
        {
            respCt = responseCt;
        }

        var reqPretty = InspectorBodyLimits.TryPrettyPrint(selected.RequestBodyText, reqCt);
        var respPretty = InspectorBodyLimits.TryPrettyPrint(selected.ResponseBodyText, respCt);
        if (reqPretty is null && respPretty is null)
        {
            return raw;
        }

        var prettySb = new StringBuilder();
        prettySb.AppendLine("=== Request ===");
        prettySb.AppendLine(reqPretty ?? selected.RequestBodyText ?? EmptyBodyPlaceholder);
        prettySb.AppendLine();
        prettySb.AppendLine("=== Response ===");
        prettySb.Append(respPretty ?? selected.ResponseBodyText ?? EmptyBodyPlaceholder);
        return prettySb.ToString();
    }
}
