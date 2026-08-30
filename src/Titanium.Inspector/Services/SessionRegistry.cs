using System.Collections.ObjectModel;

namespace Titanium.Inspector.Services;

/// <summary>
/// Thin facade over <see cref="SessionStore"/> kept for existing wiring and tests.
/// Retention / eviction live in the store (not a separate 50k LRU).
/// </summary>
public sealed class SessionRegistry : IDisposable
{
    /// <summary>
    /// Test / headless default: no disk spill (avoids LocalAppData writers).
    /// Desktop App constructs with <see cref="SessionStoreOptions.FromSettings"/>.
    /// </summary>
    public SessionRegistry()
        : this(new SessionStoreOptions { SpillBodiesToDisk = false })
    {
    }

    public SessionRegistry(SessionStoreOptions? options, string? cacheDirectory = null)
        : this(new SessionStore(options, cacheDirectory))
    {
    }

    public SessionRegistry(SessionStore store)
    {
        Store = store;
    }

    public SessionStore Store { get; }

    public ObservableCollection<SessionSnapshot> VisibleSessions => Store.Sessions;

    public void Add(SessionSnapshot snapshot) => Store.Add(snapshot);

    public SessionSnapshot? TryGet(long id) => Store.TryGet(id);

    public void Dispose() => Store.Dispose();
}
