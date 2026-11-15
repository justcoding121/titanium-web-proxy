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

/// <summary>On-screen clock. Stored instants stay UTC; only the text changes.</summary>
public enum InspectorClock
{
    /// <summary>The PC's time zone.</summary>
    Local = 0,

    /// <summary>Coordinated Universal Time.</summary>
    Utc = 1,
}

/// <summary>
/// Brief client→server protocol labels for the session grid (h1.1 → h2, h2 → h3).
/// </summary>
public static class SessionDisplayFormat
{
    /// <summary>
    /// Clock used when a format call does not pass one. The Options menu sets this.
    /// Tests that change it must put the previous value back.
    /// </summary>
    public static InspectorClock Clock { get; private set; } = InspectorClock.Local;

    /// <summary>Raised when <see cref="SetClock"/> changes the on-screen clock.</summary>
    public static event EventHandler? ClockChanged;

    /// <summary>Sets <see cref="Clock"/> and raises <see cref="ClockChanged"/> when the value changes.</summary>
    public static bool SetClock(InspectorClock clock)
    {
        if (Clock == clock)
        {
            return false;
        }

        Clock = clock;
        ClockChanged?.Invoke(null, EventArgs.Empty);
        return true;
    }

    /// <summary>Started column header tip for the active clock.</summary>
    public static string StartedColumnTip =>
        Clock == InspectorClock.Utc
            ? "When the request started (UTC). Hover a row for the full date and time."
            : "When the request started (this PC). Hover a row for the full date and time.";

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
    /// Started column text: <c>HH:mm:ss</c> when the session began today in the selected clock, otherwise
    /// with the date (<c>Jan 1, 23:00:00</c>) so a capture left running past midnight is not misread.
    /// UTC text ends in <c>Z</c>. Uses <see cref="Clock"/> and <see cref="TimeZoneInfo.Local"/>.
    /// </summary>
    public static string FormatStarted(DateTimeOffset startedUtc, DateTimeOffset nowUtc) =>
        FormatStarted(startedUtc, nowUtc, Clock, TimeZoneInfo.Local);

    /// <summary>Started column text in <paramref name="clock"/>, using <paramref name="zone"/> when the clock is this PC.</summary>
    public static string FormatStarted(
        DateTimeOffset startedUtc,
        DateTimeOffset nowUtc,
        InspectorClock clock,
        TimeZoneInfo zone)
    {
        var started = Project(startedUtc, clock, zone, out var fellBack);
        var now = Project(nowUtc, clock, zone, out _);
        var text = started.Date == now.Date
            ? started.ToString("HH:mm:ss", CultureInfo.CurrentCulture)
            : started.ToString("MMM d, HH:mm:ss", CultureInfo.CurrentCulture);
        return clock == InspectorClock.Utc || fellBack ? text + "Z" : text;
    }

    /// <summary>
    /// Full date-time for the Started tooltip. This PC includes the offset at that instant
    /// (<c>2026-01-01 21:04:05.000 -06:00</c>). UTC ends in <c>Z</c>.
    /// </summary>
    public static string FormatStartedFull(DateTimeOffset startedUtc) =>
        FormatStartedFull(startedUtc, Clock, TimeZoneInfo.Local);

    /// <summary>Tooltip text in <paramref name="clock"/>, using <paramref name="zone"/> when the clock is this PC.</summary>
    public static string FormatStartedFull(DateTimeOffset startedUtc, InspectorClock clock, TimeZoneInfo zone) =>
        FormatStamp(startedUtc, clock, zone, "yyyy-MM-dd HH:mm:ss.fff");

    /// <summary>
    /// Date and time to the second, with the zone. Used for the auto-tunnel learned row, which is not a dense column.
    /// </summary>
    public static string FormatAbsolute(DateTimeOffset instant) =>
        FormatAbsolute(instant, Clock, TimeZoneInfo.Local);

    /// <summary>Second-precision stamp in <paramref name="clock"/>.</summary>
    public static string FormatAbsolute(DateTimeOffset instant, InspectorClock clock, TimeZoneInfo zone) =>
        FormatStamp(instant, clock, zone, "yyyy-MM-dd HH:mm:ss");

    /// <summary>
    /// Status-line “since” text: <c>HH:mm</c> when the oldest session began today in the selected clock,
    /// otherwise with the date. UTC text ends in <c>Z</c>.
    /// </summary>
    public static string FormatSince(
        DateTimeOffset oldestUtc,
        DateTimeOffset nowUtc,
        InspectorClock clock,
        TimeZoneInfo zone)
    {
        var oldest = Project(oldestUtc, clock, zone, out var fellBack);
        var now = Project(nowUtc, clock, zone, out _);
        var text = oldest.Date == now.Date
            ? oldest.ToString("HH:mm", CultureInfo.CurrentCulture)
            : oldest.ToString("MMM d, HH:mm", CultureInfo.CurrentCulture);
        return clock == InspectorClock.Utc || fellBack ? text + "Z" : text;
    }

    /// <summary>
    /// Treats <see cref="DateTimeKind.Unspecified"/> as UTC. <see cref="DateTime.ToLocalTime"/> does not,
    /// so a learned time built without a kind would otherwise be shown unconverted.
    /// </summary>
    public static DateTimeOffset AsUtc(DateTime value)
    {
        var utc = value.Kind == DateTimeKind.Local
            ? value.ToUniversalTime()
            : DateTime.SpecifyKind(value, DateTimeKind.Utc);
        return new DateTimeOffset(utc, TimeSpan.Zero);
    }

    private static string FormatStamp(DateTimeOffset instant, InspectorClock clock, TimeZoneInfo zone, string pattern)
    {
        var shown = Project(instant, clock, zone, out var fellBack);
        if (clock == InspectorClock.Utc || fellBack)
        {
            return shown.UtcDateTime.ToString(pattern, CultureInfo.InvariantCulture) + "Z";
        }

        return shown.ToString(pattern + " zzz", CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Projects <paramref name="instant"/> into the clock. An instant the zone cannot represent
    /// (the edges of <see cref="DateTimeOffset"/>) falls back to UTC and sets <paramref name="fellBack"/>.
    /// </summary>
    private static DateTimeOffset Project(
        DateTimeOffset instant,
        InspectorClock clock,
        TimeZoneInfo zone,
        out bool fellBack)
    {
        if (clock == InspectorClock.Utc)
        {
            fellBack = false;
            return instant.ToUniversalTime();
        }

        try
        {
            fellBack = false;
            return TimeZoneInfo.ConvertTime(instant, zone);
        }
        catch (ArgumentException)
        {
            fellBack = true;
            return instant.ToUniversalTime();
        }
    }

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
