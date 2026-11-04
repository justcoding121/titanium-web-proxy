namespace Titanium.Web.Proxy.Abstractions.Caching;

/// <summary>
/// What core keeps for one in-flight cacheable request between middleware and the origin response.
/// Core reads <see cref="Owner"/> and calls <see cref="Abandon"/>; it does not inspect the store's private fields.
/// </summary>
public interface ICacheRequestState
{
    /// <summary>The engine that created this state.</summary>
    IResponseCacheEngine Owner { get; }

    /// <summary>Releases requests waiting behind this fetch. Idempotent; safe from any exit path.</summary>
    void Abandon();
}
