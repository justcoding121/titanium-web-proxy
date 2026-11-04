namespace Titanium.Web.Proxy.Abstractions.Caching;

/// <summary>
/// Collects a streamed response body while it is relayed. Append and dispose run only when a cache slot
/// already holds a tee; a session with no cache slot never calls through this interface.
/// </summary>
public interface ICacheBodyTee : IDisposable
{
    /// <summary>Copies <paramref name="count"/> bytes from <paramref name="source"/> at <paramref name="offset"/>.</summary>
    void Append(byte[] source, int offset, int count);

    /// <summary>
    /// The accumulated body, or null when the tee overflowed or was released.
    /// Called only from a session slot that already holds this tee.
    /// </summary>
    byte[]? ToArray();
}
