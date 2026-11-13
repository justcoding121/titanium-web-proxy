using System.Globalization;

namespace Titanium.Inspector.Services;

/// <summary>HTTP status class for session-grid status coloring.</summary>
public enum HttpStatusClass
{
    Pending,
    Informational,
    Success,
    Redirection,
    ClientError,
    ServerError,
    Other,
}

/// <summary>
/// Brief client→server protocol labels for the session grid (h1.1 → h2, h2 → h3).
/// </summary>
public static class SessionDisplayFormat
{
    public static string FormatHttpProtocol(Version? version)
    {
        if (version is null || version.Major == 0)
        {
            return "?";
        }

        return version.Major >= 2
            ? "h" + version.Major
            : $"h{version.Major}.{version.Minor}";
    }

    public static string FormatClientServer(Version? clientVersion, Version? serverVersion)
    {
        var client = FormatHttpProtocol(clientVersion);
        var server = FormatHttpProtocol(serverVersion);
        return server == "?" ? client : client + " → " + server;
    }

    /// <summary>
    /// URL column text. The Host column already shows the host, so an absolute http(s)/ws(s) URL on
    /// that host is shown as path + query (<c>/api/items?x=1</c>), with a non-default port kept as a
    /// prefix (<c>:3000/api</c>) because Host has no port. A CONNECT target <c>host:443</c> has no path,
    /// so the cell is empty (the tooltip still shows <c>host:443</c>). Anything that does not match the Host (or has no Host) stays the full URL so no
    /// information is lost. The raw text is preserved; nothing is re-escaped.
    /// </summary>
    public static string FormatUrlForGrid(string? url, string? host)
    {
        if (string.IsNullOrEmpty(url) || string.IsNullOrEmpty(host))
        {
            return url ?? "";
        }

        if (Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
            uri.Scheme is "http" or "https" or "ws" or "wss")
        {
            return FormatAbsoluteUrlForGrid(url, host, uri);
        }

        // CONNECT request target: host:port (no scheme).
        if (url.Length > host.Length + 1 &&
            url.StartsWith(host, StringComparison.OrdinalIgnoreCase) &&
            url[host.Length] == ':' &&
            url.AsSpan(host.Length + 1).IndexOfAnyExceptInRange('0', '9') < 0)
        {
            // A tunnel has no path or query; host and port are already in the Host column and the tooltip.
            return "";
        }

        return url;
    }

    private static string FormatAbsoluteUrlForGrid(string url, string host, Uri uri)
    {
        if (!uri.Host.Equals(host, StringComparison.OrdinalIgnoreCase))
        {
            return url;
        }

        var authorityStart = url.IndexOf("://", StringComparison.Ordinal) + 3;
        var tailStart = url.AsSpan(authorityStart).IndexOfAny('/', '?', '#');
        var tail = tailStart < 0 ? "" : url[(authorityStart + tailStart)..];
        var fragment = tail.IndexOf('#', StringComparison.Ordinal);
        if (fragment >= 0)
        {
            tail = tail[..fragment];
        }

        if (tail.Length == 0 || tail[0] == '?')
        {
            tail = "/" + tail;
        }

        var port = uri.IsDefaultPort ? "" : ":" + uri.Port.ToString(CultureInfo.InvariantCulture);
        return port + tail;
    }

    /// <summary>
    /// Started column text in local time: <c>HH:mm:ss</c> when the session began today, otherwise with the
    /// date (<c>Oct 6, 14:05:03</c>) so a capture left running past midnight is not misread.
    /// </summary>
    public static string FormatStarted(DateTimeOffset startedUtc, DateTimeOffset nowUtc)
    {
        var started = startedUtc.ToLocalTime();
        return started.Date == nowUtc.ToLocalTime().Date
            ? started.ToString("HH:mm:ss", CultureInfo.CurrentCulture)
            : started.ToString("MMM d, HH:mm:ss", CultureInfo.CurrentCulture);
    }

    /// <summary>Full local date-time for the Started tooltip.</summary>
    public static string FormatStartedFull(DateTimeOffset startedUtc) =>
        startedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture);

    /// <summary>Media type without parameters (<c>application/json; charset=utf-8</c> -&gt; <c>application/json</c>).</summary>
    public static string FormatContentType(string? contentType)
    {
        if (string.IsNullOrWhiteSpace(contentType))
        {
            return "";
        }

        var semicolon = contentType.IndexOf(';', StringComparison.Ordinal);
        return (semicolon >= 0 ? contentType[..semicolon] : contentType).Trim();
    }

    /// <summary>
    /// Lowercase URL scheme (<c>https</c>, <c>wss</c>, …). Empty for CONNECT targets (<c>host:443</c>),
    /// relative URLs, and anything without <c>scheme://</c>.
    /// </summary>
    public static string GetScheme(string? url)
    {
        if (string.IsNullOrEmpty(url))
        {
            return "";
        }

        var separator = url.IndexOf("://", StringComparison.Ordinal);
        if (separator is <= 0 or > 16)
        {
            return "";
        }

        var scheme = url.AsSpan(0, separator);
        foreach (var c in scheme)
        {
            if (!char.IsAsciiLetterOrDigit(c) && c is not ('+' or '-' or '.'))
            {
                return "";
            }
        }

        return scheme.ToString().ToLowerInvariant();
    }

    public static double RoundMs(double milliseconds) => Math.Round(milliseconds, 1);

    /// <summary>
    /// Compact body-size label: B below 1 KB, else KB / MB (1024-based). The decimal mark follows
    /// <see cref="CultureInfo.CurrentCulture"/>. The unit is chosen from the rounded number, so
    /// 1024 KB is shown as 1.0 MB and 9.96 KB is shown as 10 KB.
    /// </summary>
    public static string FormatByteSize(long? bytes) =>
        FormatByteSize(bytes, CultureInfo.CurrentCulture);

    /// <summary>Same label as <see cref="FormatByteSize(long?)"/> in a chosen culture. Clipboard text uses the invariant culture.</summary>
    public static string FormatByteSize(long? bytes, CultureInfo culture)
    {
        if (bytes is null or < 0)
        {
            return "";
        }

        if (bytes < 1024)
        {
            return bytes.Value.ToString("0", culture) + " B";
        }

        var kb = RoundSize(bytes.Value / 1024d);
        if (kb < 1024d)
        {
            return FormatSizeNumber(kb, culture) + " KB";
        }

        var mb = RoundSize(bytes.Value / (1024d * 1024d));
        return FormatSizeNumber(mb, culture) + " MB";
    }

    /// <summary>
    /// Whole milliseconds for the Duration and Wait columns. Grouping follows
    /// <paramref name="culture"/> (<c>12,000</c> or <c>12.000</c>). Halves round away from zero.
    /// </summary>
    public static string FormatWholeMilliseconds(double? milliseconds, CultureInfo? culture = null)
    {
        if (milliseconds is null || double.IsNaN(milliseconds.Value) || double.IsInfinity(milliseconds.Value))
        {
            return "";
        }

        var rounded = Math.Round(milliseconds.Value, 0, MidpointRounding.AwayFromZero);
        return rounded.ToString("N0", culture ?? CultureInfo.CurrentCulture);
    }

    /// <summary>Whole milliseconds with invariant digits and no grouping, for text pasted into other tools.</summary>
    public static string FormatWholeMillisecondsInvariant(double? milliseconds)
    {
        if (milliseconds is null || double.IsNaN(milliseconds.Value) || double.IsInfinity(milliseconds.Value))
        {
            return "";
        }

        var rounded = Math.Round(milliseconds.Value, 0, MidpointRounding.AwayFromZero);
        return rounded.ToString("0", CultureInfo.InvariantCulture);
    }

    /// <summary>Fractional milliseconds for statistics (<c>12.5</c> or <c>12,5</c>). A trailing zero is omitted.</summary>
    public static string FormatFractionalMilliseconds(double milliseconds) =>
        milliseconds.ToString("0.#", CultureInfo.CurrentCulture);

    /// <summary>
    /// One decimal below 10, otherwise an integer. A value that rounds up to the next threshold
    /// (9.96 → 10, 1023.6 → 1024) is returned at that threshold so the caller can change units.
    /// </summary>
    private static double RoundSize(double value)
    {
        if (value < 10d)
        {
            var tenths = Math.Round(value, 1, MidpointRounding.AwayFromZero);
            return tenths >= 10d ? 10d : tenths;
        }

        return Math.Round(value, 0, MidpointRounding.AwayFromZero);
    }

    private static string FormatSizeNumber(double rounded, CultureInfo culture) =>
        rounded < 10d
            ? rounded.ToString("0.0", culture)
            : rounded.ToString("0", culture);

    public static HttpStatusClass GetStatusClass(int? statusCode)
    {
        if (statusCode is null)
        {
            return HttpStatusClass.Pending;
        }

        return statusCode.Value switch
        {
            >= 100 and <= 199 => HttpStatusClass.Informational,
            >= 200 and <= 299 => HttpStatusClass.Success,
            >= 300 and <= 399 => HttpStatusClass.Redirection,
            >= 400 and <= 499 => HttpStatusClass.ClientError,
            >= 500 and <= 599 => HttpStatusClass.ServerError,
            _ => HttpStatusClass.Other,
        };
    }
}
