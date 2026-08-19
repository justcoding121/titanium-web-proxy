using System;
using System.Collections.Generic;

namespace Titanium.Web.Proxy.Http3.Qpack;

/// <summary>
///     QPACK static table (RFC 9204 Appendix A). 99 entries, indexed 0–98.
/// </summary>
internal static class QpackStaticTable
{
    private const string Method = ":method";
    private const string Status = ":status";
    private const string CacheControl = "cache-control";
    private const string ContentType = "content-type";

    public static readonly (string Name, string Value)[] Entries =
    {
        /* 00 */ (":authority", ""),
        /* 01 */ (":path", "/"),
        /* 02 */ ("age", "0"),
        /* 03 */ ("content-disposition", ""),
        /* 04 */ ("content-length", "0"),
        /* 05 */ ("cookie", ""),
        /* 06 */ ("date", ""),
        /* 07 */ ("etag", ""),
        /* 08 */ ("if-modified-since", ""),
        /* 09 */ ("if-none-match", ""),
        /* 10 */ ("last-modified", ""),
        /* 11 */ ("link", ""),
        /* 12 */ ("location", ""),
        /* 13 */ ("referer", ""),
        /* 14 */ ("set-cookie", ""),
        /* 15 */ (Method, "CONNECT"),
        /* 16 */ (Method, "DELETE"),
        /* 17 */ (Method, "GET"),
        /* 18 */ (Method, "HEAD"),
        /* 19 */ (Method, "OPTIONS"),
        /* 20 */ (Method, "POST"),
        /* 21 */ (Method, "PUT"),
        /* 22 */ (":scheme", "http"),
        /* 23 */ (":scheme", "https"),
        /* 24 */ (Status, "103"),
        /* 25 */ (Status, "200"),
        /* 26 */ (Status, "304"),
        /* 27 */ (Status, "404"),
        /* 28 */ (Status, "503"),
        /* 29 */ ("accept", "*/*"),
        /* 30 */ ("accept", "application/dns-message"),
        /* 31 */ ("accept-encoding", "gzip, deflate, br"),
        /* 32 */ ("accept-ranges", "bytes"),
        /* 33 */ ("access-control-allow-headers", CacheControl),
        /* 34 */ ("access-control-allow-headers", ContentType),
        /* 35 */ ("access-control-allow-origin", "*"),
        /* 36 */ (CacheControl, "max-age=0"),
        /* 37 */ (CacheControl, "max-age=2592000"),
        /* 38 */ (CacheControl, "max-age=604800"),
        /* 39 */ (CacheControl, "no-cache"),
        /* 40 */ (CacheControl, "no-store"),
        /* 41 */ (CacheControl, "public, max-age=31536000"),
        /* 42 */ ("content-encoding", "br"),
        /* 43 */ ("content-encoding", "gzip"),
        /* 44 */ (ContentType, "application/dns-message"),
        /* 45 */ (ContentType, "application/javascript"),
        /* 46 */ (ContentType, "application/json"),
        /* 47 */ (ContentType, "application/x-www-form-urlencoded"),
        /* 48 */ (ContentType, "image/gif"),
        /* 49 */ (ContentType, "image/jpeg"),
        /* 50 */ (ContentType, "image/png"),
        /* 51 */ (ContentType, "text/css"),
        /* 52 */ (ContentType, "text/html; charset=utf-8"),
        /* 53 */ (ContentType, "text/plain"),
        /* 54 */ (ContentType, "text/plain;charset=utf-8"),
        /* 55 */ ("range", "bytes=0-"),
        /* 56 */ ("strict-transport-security", "max-age=31536000"),
        /* 57 */ ("strict-transport-security", "max-age=31536000; includesubdomains"),
        /* 58 */ ("strict-transport-security", "max-age=31536000; includesubdomains; preload"),
        /* 59 */ ("vary", "accept-encoding"),
        /* 60 */ ("vary", "origin"),
        /* 61 */ ("x-content-type-options", "nosniff"),
        /* 62 */ ("x-xss-protection", "1; mode=block"),
        /* 63 */ (Status, "100"),
        /* 64 */ (Status, "204"),
        /* 65 */ (Status, "206"),
        /* 66 */ (Status, "302"),
        /* 67 */ (Status, "400"),
        /* 68 */ (Status, "403"),
        /* 69 */ (Status, "421"),
        /* 70 */ (Status, "425"),
        /* 71 */ (Status, "500"),
        /* 72 */ ("accept-language", ""),
        /* 73 */ ("access-control-allow-credentials", "FALSE"),
        /* 74 */ ("access-control-allow-credentials", "TRUE"),
        /* 75 */ ("access-control-allow-headers", "*"),
        /* 76 */ ("access-control-allow-methods", "get"),
        /* 77 */ ("access-control-allow-methods", "get, post, options"),
        /* 78 */ ("access-control-allow-methods", "options"),
        /* 79 */ ("access-control-expose-headers", "content-length"),
        /* 80 */ ("access-control-request-headers", ContentType),
        /* 81 */ ("access-control-request-method", "get"),
        /* 82 */ ("access-control-request-method", "post"),
        /* 83 */ ("alt-svc", "clear"),
        /* 84 */ ("authorization", ""),
        /* 85 */ ("content-security-policy", "script-src 'none'; object-src 'none'; base-uri 'none'"),
        /* 86 */ ("early-data", "1"),
        /* 87 */ ("expect-ct", ""),
        /* 88 */ ("forwarded", ""),
        /* 89 */ ("if-range", ""),
        /* 90 */ ("origin", ""),
        /* 91 */ ("purpose", "prefetch"),
        /* 92 */ ("server", ""),
        /* 93 */ ("timing-allow-origin", "*"),
        /* 94 */ ("upgrade-insecure-requests", "1"),
        /* 95 */ ("user-agent", ""),
        /* 96 */ ("x-forwarded-for", ""),
        /* 97 */ ("x-frame-options", "deny"),
        /* 98 */ ("x-frame-options", "sameorigin"),
    };

    private static readonly Dictionary<(string Name, string Value), int> ExactIndex;
    private static readonly Dictionary<string, int> NameOnlyIndex;

    static QpackStaticTable()
    {
        ExactIndex = new Dictionary<(string, string), int>(Entries.Length);
        NameOnlyIndex = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < Entries.Length; i++)
        {
            var e = Entries[i];
            ExactIndex[(e.Name, e.Value)] = i;
            NameOnlyIndex.TryAdd(e.Name, i);
        }
    }

    /// <summary>Exact static-table index, or -1.</summary>
    public static int FindExact(string name, string value) =>
        ExactIndex.TryGetValue((name, value), out var i) ? i : -1;

    /// <summary>First static-table index for <paramref name="name"/>, or -1.</summary>
    public static int FindName(string name) =>
        NameOnlyIndex.TryGetValue(name, out var i) ? i : -1;
}
