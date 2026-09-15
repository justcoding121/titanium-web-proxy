using System.Net.Http.Headers;
using System.Text;

namespace Titanium.Inspector.Services;

/// <summary>Replays a captured session with optional header/body edits.</summary>
public static class ReplayService
{
    public static async Task<ReplayResult> ReplayAsync(
        SessionSnapshot session,
        string? editedUrl = null,
        string? editedMethod = null,
        string? editedBody = null,
        string? editedHeaders = null,
        string? bodyFilePath = null,
        bool ignoreServerCertificateErrors = false,
        CancellationToken cancellationToken = default)
    {
        var url = editedUrl ?? session.Url;
        if (string.IsNullOrWhiteSpace(url) || session.IsTunnel)
        {
            return new ReplayResult(false, 0, "Cannot replay CONNECT/tunnel or empty URL.");
        }

        using var handler = new HttpClientHandler
        {
            AllowAutoRedirect = false,
        };
        if (ignoreServerCertificateErrors)
        {
            // Opt-in only when Inspector setting "ignore server certificate errors" is enabled (MITM lab hosts).
#pragma warning disable S4830
            handler.ServerCertificateCustomValidationCallback = static (_, _, _, _) => true;
#pragma warning restore S4830
        }

        var timeout = string.IsNullOrWhiteSpace(bodyFilePath)
            ? TimeSpan.FromSeconds(60)
            : TimeSpan.FromMinutes(10);
        using var http = new HttpClient(handler) { Timeout = timeout };
        using var request = new HttpRequestMessage(new HttpMethod(editedMethod ?? session.Method), url);

        ApplyEditedHeaders(request, editedHeaders ?? session.RequestHeadersText ?? "");
        await using var fileStream = await AttachBodyAsync(request, session, editedBody, bodyFilePath, cancellationToken)
            .ConfigureAwait(false);

        using var response = await http.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);

        var respHeaders = new StringBuilder();
        foreach (var h in response.Headers)
        {
            respHeaders.Append(h.Key).Append(": ").Append(string.Join(", ", h.Value)).AppendLine();
        }

        foreach (var h in response.Content.Headers)
        {
            respHeaders.Append(h.Key).Append(": ").Append(string.Join(", ", h.Value)).AppendLine();
        }

        await using var respStream = await response.Content.ReadAsStreamAsync(cancellationToken)
            .ConfigureAwait(false);
        var (respBytes, respSeen, truncated) = await ReadPreviewAsync(respStream, cancellationToken)
            .ConfigureAwait(false);
        var respBody = Encoding.UTF8.GetString(respBytes);

        return new ReplayResult(
            true,
            (int)response.StatusCode,
            Truncate(respBody, 64 * 1024),
            respHeaders.ToString(),
            InspectorBodyLimits.TruncateText(respBody),
            respBytes,
            respSeen,
            truncated ? BodyCaptureState.Truncated : BodyCaptureState.Complete);
    }

    private static void ApplyEditedHeaders(HttpRequestMessage request, string headerBlock)
    {
        foreach (var line in headerBlock.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var idx = line.IndexOf(':');
            if (idx <= 0)
            {
                continue;
            }

            var name = line[..idx].Trim();
            var value = line[(idx + 1)..].Trim();
            if (name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase)
                || name.Equals("Host", StringComparison.OrdinalIgnoreCase)
                || name.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!request.Headers.TryAddWithoutValidation(name, value))
            {
                request.Content ??= new ByteArrayContent(Array.Empty<byte>());
                request.Content.Headers.TryAddWithoutValidation(name, value);
            }
        }
    }

    private static async Task<FileStream?> AttachBodyAsync(
        HttpRequestMessage request,
        SessionSnapshot session,
        string? editedBody,
        string? bodyFilePath,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(bodyFilePath))
        {
            var path = bodyFilePath.Trim();
            if (!File.Exists(path))
            {
                throw new FileNotFoundException("Composer body file not found.", path);
            }

            var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.Asynchronous);
            var content = new StreamContent(fs);
            content.Headers.ContentLength = fs.Length;
            if (!string.IsNullOrEmpty(session.ContentType))
            {
                content.Headers.ContentType = MediaTypeHeaderValue.Parse(session.ContentType);
            }

            request.Content = content;
            return fs;
        }

        var bodyText = editedBody ?? session.RequestBodyText;
        if (!string.IsNullOrEmpty(bodyText))
        {
            var bytes = Encoding.UTF8.GetBytes(bodyText);
            request.Content = new ByteArrayContent(bytes);
            if (!string.IsNullOrEmpty(session.ContentType))
            {
                request.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(session.ContentType);
            }
        }
        else if (editedBody is null && session.RequestBodyBytes is { Length: > 0 })
        {
            request.Content = new ByteArrayContent(session.RequestBodyBytes);
        }

        await Task.CompletedTask.ConfigureAwait(false);
        return null;
    }

    private static async Task<(byte[] Bytes, long Seen, bool Truncated)> ReadPreviewAsync(
        Stream stream,
        CancellationToken cancellationToken)
    {
        using var ms = new MemoryStream();
        var buffer = new byte[8192];
        long seen = 0;
        while (true)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken)
                .ConfigureAwait(false);
            if (read <= 0)
            {
                break;
            }

            seen += read;
            if (ms.Length < InspectorBodyLimits.MaxBodyBytes)
            {
                var toWrite = (int)Math.Min(read, InspectorBodyLimits.MaxBodyBytes - ms.Length);
                ms.Write(buffer, 0, toWrite);
            }
        }

        return (ms.ToArray(), seen, seen > ms.Length);
    }

    private static string Truncate(string text, int max)
        => text.Length <= max ? text : text[..max] + "…";
}

public readonly record struct ReplayResult(
    bool Ok,
    int StatusCode,
    string Message,
    string? ResponseHeaders = null,
    string? ResponseBody = null,
    byte[]? ResponseBodyBytes = null,
    long? ResponseBodyOriginalSize = null,
    BodyCaptureState ResponseBodyCapture = BodyCaptureState.None);
