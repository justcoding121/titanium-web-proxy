using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using Titanium.Web.Proxy.Http;

namespace Titanium.Web.Proxy.Shared;

/// <summary>
///     Literals shared by Proxy Server
/// </summary>
internal static class ProxyConstants
{
    internal static readonly char DotSplit = '.';

    internal static readonly string NewLine = "\r\n";
    internal static readonly byte[] NewLineBytes = { (byte)'\r', (byte)'\n' };

    internal static readonly HashSet<string> ProxySupportedCompressions =
        new(StringComparer.OrdinalIgnoreCase)
        {
            KnownHeaders.ContentEncodingGzip.String,
            KnownHeaders.ContentEncodingDeflate.String,
            KnownHeaders.ContentEncodingBrotli.String
            // Note: "zstd" is intentionally excluded from ProxySupportedCompressions until a
            // strong-name-compatible .NET managed zstd library is approved for this assembly.
            // See Phase 6 / evaluate-zstd in the implementation plan.
            // To add zstd: install ZstdSharp.Port or equivalent, add "zstd" here, and wire
            // decompression through DecompressionFactory.
        };

    internal static readonly Regex CnRemoverRegex =
        new(@"^CN\s*=\s*", RegexOptions.IgnoreCase | RegexOptions.Compiled, TimeSpan.FromSeconds(1));
}