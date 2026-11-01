using System.Collections.Concurrent;

namespace Titanium.Web.Proxy.Abstractions.Compression;

/// <summary>
///     Thread-safe registry of optional content codings. Core asks here for names it does not implement
///     natively; the CLI registers <c>zstd</c> when configured.
/// </summary>
public static class ContentCodecs
{
    private static readonly ConcurrentDictionary<string, IContentCodec> Codecs =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Registers <paramref name="codec" /> under <see cref="IContentCodec.Name" />.</summary>
    public static void Register(IContentCodec codec)
    {
        ArgumentNullException.ThrowIfNull(codec);
        if (string.IsNullOrWhiteSpace(codec.Name))
            throw new ArgumentException("Codec name is required.", nameof(codec));

        Codecs[codec.Name.Trim()] = codec;
    }

    /// <summary>True when a codec is registered for <paramref name="name" />.</summary>
    public static bool IsRegistered(string name) =>
        !string.IsNullOrWhiteSpace(name) && Codecs.ContainsKey(name.Trim());

    /// <summary>Looks up a registered codec.</summary>
    public static bool TryGet(string name, out IContentCodec? codec)
    {
        codec = null;
        if (string.IsNullOrWhiteSpace(name))
            return false;

        if (Codecs.TryGetValue(name.Trim(), out var found))
        {
            codec = found;
            return true;
        }

        return false;
    }

    /// <summary>Clears all registrations (unit tests only).</summary>
    internal static void ResetForTests() => Codecs.Clear();
}
