using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace Titanium.Inspector.Services;

/// <summary>How Inspector retained a request or response body for the session grid.</summary>
public enum BodyCaptureState
{
    /// <summary>No body expected, or not yet known.</summary>
    None = 0,

    /// <summary>Full body kept (within preview caps).</summary>
    Complete = 1,

    /// <summary>Body was larger than the preview cap; only a prefix is stored.</summary>
    Truncated = 2,

    /// <summary>Known huge Content-Length — not buffered (download must not stall).</summary>
    NotCaptured = 3,

    /// <summary>Endless / SSE-style stream — relayed; preview may fill asynchronously.</summary>
    Streaming = 4,
}

/// <summary>Shared Inspector body/preview limits (capture UI, Composer, AutoResponder).</summary>
public static class InspectorBodyLimits
{
    public const int MaxBodyBytes = 2 * 1024 * 1024;
    public const int MaxBodyTextChars = 256 * 1024;
    public const int MaxHexBytes = 4096;
    public const int MaxInlineToolBodyChars = 256 * 1024;
    public const int MaxScriptChars = 32 * 1024;
    public const int MaxMapLocalFileBytes = 32 * 1024 * 1024;
    public const int MaxDecodedImageEdgePx = 4096;
    public const long MaxDecodedImagePixels = 16L * 1024 * 1024;
    public const int TeeUiCoalesceMs = 200;

    public static byte[]? TruncateBytes(byte[]? body)
    {
        if (body is null || body.Length == 0)
        {
            return body;
        }

        return body.Length <= MaxBodyBytes ? body : body.AsSpan(0, MaxBodyBytes).ToArray();
    }

    public static string TruncateText(string text)
        => text.Length <= MaxBodyTextChars ? text : text[..MaxBodyTextChars] + "…";

    public static bool IsImageContentType(string? contentType)
    {
        if (string.IsNullOrWhiteSpace(contentType))
        {
            return false;
        }

        var ct = contentType.Split(';', 2)[0].Trim();
        return ct.StartsWith("image/", StringComparison.OrdinalIgnoreCase)
               && !ct.Equals("image/svg+xml", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsPrettyPrintableContentType(string? contentType)
    {
        if (string.IsNullOrWhiteSpace(contentType))
        {
            return false;
        }

        var ct = contentType.Split(';', 2)[0].Trim();
        return ct.Contains("json", StringComparison.OrdinalIgnoreCase)
               || ct.Contains("xml", StringComparison.OrdinalIgnoreCase)
               || ct.StartsWith("text/html", StringComparison.OrdinalIgnoreCase)
               || ct.Equals("application/xhtml+xml", StringComparison.OrdinalIgnoreCase);
    }

    public static bool LooksLikeSseContentType(string? contentType) =>
        !string.IsNullOrEmpty(contentType)
        && contentType.Contains("text/event-stream", StringComparison.OrdinalIgnoreCase);

    public static string FormatCaptureBanner(
        BodyCaptureState state,
        long? originalSize,
        int capturedBytes,
        bool streamOpen,
        bool forHex)
    {
        var capturedLabel = SessionDisplayFormat.FormatByteSize(capturedBytes);
        var originalLabel = originalSize is > 0
            ? SessionDisplayFormat.FormatByteSize(originalSize)
            : null;

        if (forHex && capturedBytes > 0)
        {
            var hexShown = Math.Min(capturedBytes, MaxHexBytes);
            var hexLabel = SessionDisplayFormat.FormatByteSize(hexShown);
            if (capturedBytes > MaxHexBytes)
            {
                return $"Hex shows first {hexLabel} of {capturedLabel} captured";
            }
        }

        return state switch
        {
            BodyCaptureState.Truncated when originalLabel is not null =>
                $"Showing first {capturedLabel} of {originalLabel}",
            BodyCaptureState.Truncated =>
                $"Showing first {capturedLabel} (body truncated)",
            BodyCaptureState.NotCaptured when originalLabel is not null =>
                $"Body not captured ({originalLabel}) — streamed so the download would not stall",
            BodyCaptureState.NotCaptured =>
                "Body not captured — streamed so the download would not stall",
            BodyCaptureState.Streaming when streamOpen =>
                $"Streaming · showing first {capturedLabel} captured so far",
            BodyCaptureState.Streaming =>
                $"Streaming ended · captured {capturedLabel}",
            BodyCaptureState.Complete => "",
            _ => "",
        };
    }

    public static BodyCaptureState InferFromBytes(byte[]? bytes, long? originalSize)
    {
        if (bytes is null)
        {
            return originalSize is > MaxBodyBytes
                ? BodyCaptureState.NotCaptured
                : BodyCaptureState.None;
        }

        if (originalSize is long orig && orig > bytes.Length)
        {
            return BodyCaptureState.Truncated;
        }

        if (bytes.Length >= MaxBodyBytes && originalSize is null or > MaxBodyBytes)
        {
            return BodyCaptureState.Truncated;
        }

        return BodyCaptureState.Complete;
    }

    /// <summary>Pretty-print JSON / XML / HTML source. Returns null when formatting is not applicable or fails.</summary>
    public static string? TryPrettyPrint(string? text, string? contentType)
    {
        if (string.IsNullOrWhiteSpace(text) || IsImageContentType(contentType))
        {
            return null;
        }

        var ct = contentType?.Split(';', 2)[0].Trim() ?? "";
        try
        {
            if (ct.Contains("json", StringComparison.OrdinalIgnoreCase)
                || (string.IsNullOrEmpty(ct) && LooksLikeJson(text)))
            {
                using var doc = System.Text.Json.JsonDocument.Parse(text);
                var pretty = System.Text.Json.JsonSerializer.Serialize(
                    doc.RootElement,
                    new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
                return TruncateText(pretty);
            }

            if (ct.StartsWith("text/html", StringComparison.OrdinalIgnoreCase))
            {
                return TruncateText(IndentMarkupSource(text));
            }

            if (ct.Contains("xml", StringComparison.OrdinalIgnoreCase)
                || ct.Equals("application/xhtml+xml", StringComparison.OrdinalIgnoreCase))
            {
                return TruncateText(PrettyPrintXml(text));
            }
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
        catch (XmlException)
        {
            return null;
        }

        return null;
    }

    private static bool LooksLikeJson(string text)
    {
        var t = text.AsSpan().TrimStart();
        return t.Length > 0 && (t[0] == '{' || t[0] == '[');
    }

    private static string PrettyPrintXml(string text)
    {
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            IgnoreWhitespace = false,
        };
        using var reader = XmlReader.Create(new StringReader(text), settings);
        var doc = XDocument.Load(reader, LoadOptions.PreserveWhitespace);
        return doc.Declaration is null
            ? doc.ToString()
            : doc.Declaration + Environment.NewLine + doc.ToString();
    }

    /// <summary>Best-effort indent of HTML/markup source without executing or validating as XML.</summary>
    private static string IndentMarkupSource(string text)
    {
        var sb = new StringBuilder(text.Length + 64);
        var depth = 0;
        var i = 0;
        while (i < text.Length)
        {
            if (text[i] == '<')
            {
                var end = text.IndexOf('>', i);
                if (end < 0)
                {
                    sb.Append(text.AsSpan(i));
                    break;
                }

                var tag = text.AsSpan(i, end - i + 1);
                var isClosing = tag.Length > 1 && tag[1] == '/';
                var isSelfClosing = tag.EndsWith("/>", StringComparison.Ordinal)
                                    || tag.StartsWith("<!", StringComparison.Ordinal)
                                    || tag.StartsWith("<?", StringComparison.Ordinal);
                if (isClosing)
                {
                    depth = Math.Max(0, depth - 1);
                }

                if (sb.Length > 0 && sb[^1] != '\n')
                {
                    sb.AppendLine();
                }

                sb.Append(' ', depth * 2);
                sb.Append(tag);
                if (!isClosing && !isSelfClosing)
                {
                    depth++;
                }

                i = end + 1;
                continue;
            }

            var next = text.IndexOf('<', i);
            if (next < 0)
            {
                sb.Append(text.AsSpan(i).Trim());
                break;
            }

            var slice = text.AsSpan(i, next - i).Trim();
            if (slice.Length > 0)
            {
                if (sb.Length > 0 && sb[^1] != '\n')
                {
                    sb.AppendLine();
                }

                sb.Append(' ', depth * 2);
                sb.Append(slice);
            }

            i = next;
        }

        return sb.ToString();
    }

    public static string SuggestBodyFileName(string? url, string? contentDisposition, string? contentType, bool isRequest)
    {
        if (!string.IsNullOrWhiteSpace(contentDisposition))
        {
            const string marker = "filename=";
            var idx = contentDisposition.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (idx >= 0)
            {
                var name = contentDisposition[(idx + marker.Length)..].Trim().Trim('"', '\'');
                if (name.Length > 0)
                {
                    return SanitizeFileName(name);
                }
            }
        }

        if (!string.IsNullOrWhiteSpace(url) && Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            var leaf = Path.GetFileName(uri.AbsolutePath);
            if (!string.IsNullOrWhiteSpace(leaf) && leaf != "/" && leaf.Contains('.', StringComparison.Ordinal))
            {
                return SanitizeFileName(leaf);
            }
        }

        var ext = ExtensionForContentType(contentType);
        return (isRequest ? "request" : "response") + ext;
    }

    private static string ExtensionForContentType(string? contentType)
    {
        if (string.IsNullOrWhiteSpace(contentType))
        {
            return ".bin";
        }

        var ct = contentType.Split(';', 2)[0].Trim().ToLowerInvariant();
        return ct switch
        {
            "application/json" or "text/json" => ".json",
            "text/html" => ".html",
            "text/plain" => ".txt",
            "text/xml" or "application/xml" => ".xml",
            "image/png" => ".png",
            "image/jpeg" => ".jpg",
            "image/gif" => ".gif",
            "image/webp" => ".webp",
            "image/bmp" => ".bmp",
            "application/octet-stream" => ".bin",
            _ when ct.Contains("javascript", StringComparison.Ordinal) => ".js",
            _ when ct.Contains("css", StringComparison.Ordinal) => ".css",
            _ => ".bin",
        };
    }

    private static string SanitizeFileName(string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars())
        {
            name = name.Replace(c, '_');
        }

        return string.IsNullOrWhiteSpace(name) ? "body.bin" : name;
    }
}
