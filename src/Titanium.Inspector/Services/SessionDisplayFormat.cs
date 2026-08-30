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

    public static double RoundMs(double milliseconds) => Math.Round(milliseconds, 1);

    /// <summary>Compact body-size label: B below 1 KB, else KB / MB (1024-based).</summary>
    public static string FormatByteSize(long? bytes)
    {
        if (bytes is null or < 0)
        {
            return "";
        }

        if (bytes < 1024)
        {
            return bytes + " B";
        }

        var kb = bytes.Value / 1024.0;
        if (kb < 1024)
        {
            var kbText = kb < 10
                ? kb.ToString("0.0", CultureInfo.InvariantCulture)
                : kb.ToString("0", CultureInfo.InvariantCulture);
            return kbText + " KB";
        }

        var mb = kb / 1024.0;
        var mbText = mb < 10
            ? mb.ToString("0.0", CultureInfo.InvariantCulture)
            : mb.ToString("0", CultureInfo.InvariantCulture);
        return mbText + " MB";
    }

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
