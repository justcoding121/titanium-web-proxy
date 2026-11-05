namespace Titanium.Web.Proxy.Abstractions.Caching;

/// <summary>
/// Response-cache engine behind <c>IProxyMiddleware</c>. Core calls these members only after
/// a cache state or slot is already non-null. Header collections and sessions stay in core: conditional
/// validators come back as strings, and a cached reply is described by <see cref="CachedServe"/>.
/// </summary>
public interface IResponseCacheEngine
{
    /// <summary>Largest body this engine will store.</summary>
    int MaxEntryBytes { get; }

    /// <summary>
    /// Allocates the body tee for a streamed store. Called only after <see cref="OnOriginResponse"/>
    /// already decided to store, so a session with no cache state never calls it.
    /// </summary>
    ICacheBodyTee CreateBodyTee(long expectedLength);

    /// <summary>
    /// Called with an origin response's status and headers, before any of the body is relayed.
    /// <paramref name="headers"/> is a snapshot of name/value pairs.
    /// </summary>
    OriginDecision OnOriginResponse(
        ICacheRequestState state,
        int statusCode,
        IReadOnlyList<KeyValuePair<string, string>> headers,
        bool hasBody,
        long contentLength,
        bool chunked);

    /// <summary>
    /// Store <paramref name="body"/> for <paramref name="plan"/>, or drop it when the length disagrees.
    /// Always releases waiters for <paramref name="state"/>.
    /// </summary>
    void CompleteStore(ICacheRequestState state, CacheStorePlan plan, byte[]? body, long expectedLength);

    /// <summary>
    /// <c>If-None-Match</c> and <c>If-Modified-Since</c> to add on the way to the origin.
    /// Both null when this request is not a conditional revalidation.
    /// </summary>
    (string? IfNoneMatch, string? IfModifiedSince) ConditionalValidators(ICacheRequestState state);
}
