using Titanium.Web.Proxy.Abstractions.Compression;
using Titanium.Web.Proxy.Abstractions.Routing;

namespace Titanium.Cli.Compression;

/// <summary>Registers the managed zstd codec when the loaded config names it.</summary>
internal static class ZstdCodecRegistration
{
    private static int _registered;

    internal static void RegisterIfConfigured(ResponseCompressionConfig? responseCompression)
    {
        if (!NeedsZstd(responseCompression))
            return;

        if (Interlocked.CompareExchange(ref _registered, 1, 0) != 0)
            return;

        ContentCodecs.Register(new ZstdContentCodec());
    }

    private static bool NeedsZstd(ResponseCompressionConfig? config) =>
        config is { Enabled: true, Algorithms: { } algorithms }
        && algorithms.Any(a => a.Trim().Equals("zstd", StringComparison.OrdinalIgnoreCase));
}
