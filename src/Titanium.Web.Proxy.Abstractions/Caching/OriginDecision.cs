namespace Titanium.Web.Proxy.Abstractions.Caching;

/// <summary>What to do with an origin response the cache has already been asked about.</summary>
public enum OriginAction
{
    /// <summary>Relay the origin response untouched.</summary>
    Pass = 0,

    /// <summary>Send <see cref="OriginDecision.Serve"/> instead of the origin response.</summary>
    Serve = 1,

    /// <summary>The body is small and known: read it whole, then call <see cref="IResponseCacheEngine.CompleteStore"/>.</summary>
    StoreBuffered = 2,

    /// <summary>The body streams: tee it while relaying, then call <see cref="IResponseCacheEngine.CompleteStore"/>.</summary>
    StoreStreamed = 3,
}

/// <summary>A cached response ready to be written to a client.</summary>
public sealed class CachedServe
{
    /// <summary>Status code to write, including 304 when the client validators already match.</summary>
    public required int StatusCode { get; init; }

    /// <summary>Framing headers, in the order they should be written.</summary>
    public required List<KeyValuePair<string, string>> Headers { get; init; }

    /// <summary>Wire-encoded body. Empty for HEAD and 304.</summary>
    public required byte[] Body { get; init; }
}

/// <summary>
/// Opaque plan produced by <see cref="IResponseCacheEngine.OnOriginResponse"/> and passed back to
/// <see cref="IResponseCacheEngine.CompleteStore"/>. Core call sites do not read its fields.
/// </summary>
public abstract class CacheStorePlan
{
    /// <summary>Implementations live beside the cache engine.</summary>
    protected CacheStorePlan()
    {
    }
}

/// <summary>Decision returned before any of the origin body is relayed.</summary>
public readonly struct OriginDecision
{
    /// <summary>What the caller should do next. <see cref="OriginAction.Pass"/> is the default.</summary>
    public OriginAction Action { get; init; }

    /// <summary>Set when <see cref="Action"/> is <see cref="OriginAction.Serve"/>.</summary>
    public CachedServe? Serve { get; init; }

    /// <summary>Set when the body should be stored. Passed back to the engine unchanged.</summary>
    public CacheStorePlan? Plan { get; init; }

    /// <summary>Declared length for a buffered store, or -1 when the body is chunked.</summary>
    public long ContentLength { get; init; }

    /// <summary>Relay the origin response and store nothing.</summary>
    public static OriginDecision Pass => default;
}
