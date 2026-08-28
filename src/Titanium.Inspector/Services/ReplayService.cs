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

        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(60) };
        using var request = new HttpRequestMessage(new HttpMethod(editedMethod ?? session.Method), url);

        var headerBlock = editedHeaders ?? session.RequestHeadersText ?? "";
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

        using var response = await http.SendAsync(request, cancellationToken);
        var respBody = await response.Content.ReadAsStringAsync(cancellationToken);
        var respHeaders = new StringBuilder();
        foreach (var h in response.Headers)
        {
            respHeaders.Append(h.Key).Append(": ").Append(string.Join(", ", h.Value)).AppendLine();
        }

        foreach (var h in response.Content.Headers)
        {
            respHeaders.Append(h.Key).Append(": ").Append(string.Join(", ", h.Value)).AppendLine();
        }

        return new ReplayResult(
            true,
            (int)response.StatusCode,
            Truncate(respBody, 64 * 1024),
            respHeaders.ToString(),
            Truncate(respBody, InterceptionService.MaxBodyTextChars));
    }

    private static string Truncate(string text, int max)
        => text.Length <= max ? text : text[..max] + "…";
}

public readonly record struct ReplayResult(
    bool Ok,
    int StatusCode,
    string Message,
    string? ResponseHeaders = null,
    string? ResponseBody = null);
