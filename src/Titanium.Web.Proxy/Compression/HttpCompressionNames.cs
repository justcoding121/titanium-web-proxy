using Titanium.Web.Proxy.Abstractions.Compression;
using Titanium.Web.Proxy.Http;

namespace Titanium.Web.Proxy.Compression;

internal static class HttpCompressionNames
{
    internal static string WireToken(HttpCompression algorithm) => algorithm switch
    {
        HttpCompression.Brotli => KnownHeaders.ContentEncodingBrotli.String,
        HttpCompression.Gzip => KnownHeaders.ContentEncodingGzip.String,
        HttpCompression.Zstd => "zstd",
        _ => string.Empty
    };

    internal static bool TryParse(string? name, out HttpCompression algorithm)
    {
        algorithm = HttpCompression.Unsupported;
        if (string.IsNullOrWhiteSpace(name))
            return false;

        switch (name.Trim().ToLowerInvariant())
        {
            case "br":
                algorithm = HttpCompression.Brotli;
                return true;
            case "gzip":
                algorithm = HttpCompression.Gzip;
                return true;
            case "zstd":
                algorithm = HttpCompression.Zstd;
                return ContentCodecs.IsRegistered("zstd");
            default:
                return false;
        }
    }

    internal static bool IsBuiltIn(HttpCompression algorithm) =>
        algorithm is HttpCompression.Gzip or HttpCompression.Brotli or HttpCompression.Deflate;
}
