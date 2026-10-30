using System.Net;

namespace Titanium.Gateway.Kube;

/// <summary>The answer to one Kubernetes API request.</summary>
public sealed record KubeResponse(HttpStatusCode Status, string Body)
{
    public bool IsSuccess => (int)Status is >= 200 and < 300;

    public bool IsNotFound => Status == HttpStatusCode.NotFound;

    public bool IsConflict => Status == HttpStatusCode.Conflict;
}

/// <summary>The API server refused a request, or a watch stream reported an error.</summary>
public sealed class KubeApiException : Exception
{
    public KubeApiException(HttpStatusCode status, string body)
        : base($"Kubernetes API answered {(int)status}: {Truncate(body)}")
    {
        Status = status;
        Body = body;
    }

    public HttpStatusCode Status { get; }

    public string Body { get; }

    private static string Truncate(string text) => text.Length <= 300 ? text : text[..300] + "...";
}

/// <summary>
///     The one door to the Kubernetes API. It is shaped like HTTP on purpose: informers, leader election and the
///     writers all speak REST paths and JSON, so tests can stand an in-memory API server behind it and the
///     production implementation (the official client) is a few lines of transport.
/// </summary>
public interface IKubeApi
{
    /// <summary>Sends one request. A non-2xx answer is returned, not thrown. Transport failures throw.</summary>
    Task<KubeResponse> SendAsync(HttpMethod method, string pathAndQuery, string? body, string? contentType, CancellationToken cancellationToken);

    /// <summary>Opens a watch (<c>?watch=true</c>) and returns the line-delimited JSON stream. A non-2xx answer throws <see cref="KubeApiException"/>.</summary>
    Task<Stream> OpenWatchAsync(string pathAndQuery, CancellationToken cancellationToken);
}
