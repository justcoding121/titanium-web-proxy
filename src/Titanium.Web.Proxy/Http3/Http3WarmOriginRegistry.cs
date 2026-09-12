using System.Collections.Concurrent;

namespace Titanium.Web.Proxy.Http3;

/// <summary>
///     Tracks which origins currently have an established QUIC connection.
///     <para>
///         This is deliberately separate from <see cref="Http3OriginCapabilityCache" />, which answers
///         a different question. The capability cache says an origin <em>supports</em> HTTP/3; this
///         registry says a connection to it <em>exists right now</em>. The pool uses it to skip
///         redundant warm-up and to drop idle sockets; Auto route selection uses the capability
///         cache alone so a new CONNECT after Alt-Svc still takes HTTP/3.
///     </para>
/// </summary>
internal sealed class Http3WarmOriginRegistry
{
    private readonly ConcurrentDictionary<string, byte> _warm = new();

    internal bool IsWarm(string host, int port) => _warm.ContainsKey(Key(host, port));

    internal void Mark(string host, int port) => _warm[Key(host, port)] = 0;

    internal void Clear(string host, int port) => _warm.TryRemove(Key(host, port), out _);

    internal void ClearAll() => _warm.Clear();

    private static string Key(string host, int port) => $"{host}:{port}";
}
