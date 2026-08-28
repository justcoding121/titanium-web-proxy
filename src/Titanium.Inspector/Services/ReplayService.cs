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
            ServerCertificateCustomValidationCallback = static (_, _, _, _) => true,
        };
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(60) };
        using var request = new HttpRequestMessage(new HttpMethod(editedMethod ?? session.Method), url);

        foreach (var line in (session.RequestHeadersText ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
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
        else if (session.RequestBodyBytes is { Length: > 0 })
        {
            request.Content = new ByteArrayContent(session.RequestBodyBytes);
        }

        using var response = await http.SendAsync(request, cancellationToken);
        var respBody = await response.Content.ReadAsStringAsync(cancellationToken);
        return new ReplayResult(true, (int)response.StatusCode, Truncate(respBody, 64 * 1024));
    }

    private static string Truncate(string text, int max)
        => text.Length <= max ? text : text[..max] + "…";
}

public readonly record struct ReplayResult(bool Ok, int StatusCode, string Message);
