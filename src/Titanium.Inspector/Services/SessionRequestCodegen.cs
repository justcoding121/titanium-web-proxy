using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Titanium.Inspector.Services;

/// <summary>Generates curl and fetch snippets from a captured <see cref="SessionSnapshot"/> (offline; no hot path).</summary>
public static class SessionRequestCodegen
{
    private const string NoReplayableUrlMessage = "Session has no replayable URL (or is a CONNECT tunnel).";

    private static readonly JsonSerializerOptions JsStringOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static readonly HashSet<string> SkippedHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Content-Length",
        "Transfer-Encoding",
        "Host",
        "Connection",
        "Proxy-Connection",
        "Keep-Alive",
        "Proxy-Authorization",
    };

    /// <summary>True when the session can be turned into curl/fetch (non-tunnel with a URL).</summary>
    public static bool CanGenerate(SessionSnapshot? session) =>
        session is not null
        && !session.IsTunnel
        && !string.IsNullOrWhiteSpace(session.Url);

    /// <summary>
    /// Parsed request used by every generator so header skipping and body choice stay in one place.
    /// </summary>
    public readonly record struct CapturedRequest(
        string Method,
        string Url,
        IReadOnlyList<(string Name, string Value)> Headers,
        string? BodyText,
        byte[]? BodyBytes);

    /// <summary>True when <paramref name="session"/> can be turned into a request snippet.</summary>
    public static bool TryGetRequest(SessionSnapshot? session, out CapturedRequest request)
    {
        request = default;
        if (!CanGenerate(session) || session is null)
        {
            return false;
        }

        var method = string.IsNullOrWhiteSpace(session.Method) ? "GET" : session.Method.Trim().ToUpperInvariant();
        var bodyText = string.IsNullOrEmpty(session.RequestBodyText) ? null : session.RequestBodyText;
        var bodyBytes = bodyText is null && session.RequestBodyBytes is { Length: > 0 }
            ? session.RequestBodyBytes
            : null;
        request = new CapturedRequest(
            method,
            session.Url,
            EnumerateHeaders(session.RequestHeadersText).ToArray(),
            bodyText,
            bodyBytes);
        return true;
    }

    /// <summary>Builds a shell-safe curl command for the request side of the session.</summary>
    public static string ToCurl(SessionSnapshot session)
    {
        if (!TryGetRequest(session, out var request))
        {
            throw new ArgumentException(NoReplayableUrlMessage, nameof(session));
        }

        var sb = new StringBuilder();
        sb.Append("curl ").Append(ShellSingleQuote(request.Url));
        if (!string.Equals(request.Method, "GET", StringComparison.Ordinal))
        {
            sb.Append(" \\\n  -X ").Append(ShellSingleQuote(request.Method));
        }

        foreach (var (name, value) in request.Headers)
        {
            sb.Append(" \\\n  -H ").Append(ShellSingleQuote(name + ": " + value));
        }

        var body = request.BodyText ?? (request.BodyBytes is { Length: > 0 } bytes
            ? Encoding.UTF8.GetString(bytes)
            : null);
        if (body is not null)
        {
            sb.Append(" \\\n  --data-binary ").Append(ShellSingleQuote(body));
        }

        return sb.ToString();
    }

    /// <summary>Builds a JavaScript <c>fetch(...)</c> call for the request side of the session.</summary>
    public static string ToFetch(SessionSnapshot session)
    {
        if (!TryGetRequest(session, out var request))
        {
            throw new ArgumentException(NoReplayableUrlMessage, nameof(session));
        }

        var method = request.Method;
        var headers = request.Headers;
        var body = request.BodyText ?? (request.BodyBytes is { Length: > 0 } bytes
            ? Encoding.UTF8.GetString(bytes)
            : null);

        var sb = new StringBuilder();
        sb.Append("fetch(").Append(JsonSerializer.Serialize(session.Url, JsStringOptions));

        var needsInit = !string.Equals(method, "GET", StringComparison.Ordinal)
                        || headers.Count > 0
                        || body is not null;
        if (!needsInit)
        {
            sb.Append(");");
            return sb.ToString();
        }

        sb.Append(", {\n");
        sb.Append("  \"method\": ").Append(JsonSerializer.Serialize(method, JsStringOptions));

        if (headers.Count > 0)
        {
            sb.Append(",\n  \"headers\": {\n");
            for (var i = 0; i < headers.Count; i++)
            {
                var (name, value) = headers[i];
                sb.Append("    ")
                    .Append(JsonSerializer.Serialize(name, JsStringOptions))
                    .Append(": ")
                    .Append(JsonSerializer.Serialize(value, JsStringOptions));
                if (i < headers.Count - 1)
                {
                    sb.Append(',');
                }

                sb.Append('\n');
            }

            sb.Append("  }");
        }

        if (body is not null)
        {
            sb.Append(",\n  \"body\": ").Append(JsonSerializer.Serialize(body, JsStringOptions));
        }

        sb.Append("\n});");
        return sb.ToString();
    }

    private static IEnumerable<(string Name, string Value)> EnumerateHeaders(string? headerBlock)
    {
        if (string.IsNullOrWhiteSpace(headerBlock))
        {
            yield break;
        }

        foreach (var line in headerBlock.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var idx = line.IndexOf(':');
            if (idx <= 0)
            {
                continue;
            }

            var name = line[..idx].Trim();
            var value = line[(idx + 1)..].Trim();
            if (name.Length == 0 || SkippedHeaders.Contains(name))
            {
                continue;
            }

            yield return (name, value);
        }
    }

    /// <summary>How many rows <see cref="ToTable"/> will copy before it stops and reports the rest.</summary>
    public const int MaxTableRows = 50_000;

    /// <summary>PowerShell <c>Invoke-WebRequest</c>. Content-Type and User-Agent use their own parameters.</summary>
    public static string ToPowerShell(SessionSnapshot session)
    {
        if (!TryGetRequest(session, out var request))
        {
            throw new ArgumentException(NoReplayableUrlMessage, nameof(session));
        }

        var (contentType, userAgent, headers) = PartitionPowerShellHeaders(request.Headers);
        var sb = new StringBuilder();
        sb.Append("Invoke-WebRequest -Uri ").Append(PowerShellQuote(request.Url));
        sb.Append(" -Method ").Append(PowerShellQuote(request.Method));
        if (contentType is not null)
        {
            sb.Append(" -ContentType ").Append(PowerShellQuote(contentType));
        }

        if (userAgent is not null)
        {
            sb.Append(" -UserAgent ").Append(PowerShellQuote(userAgent));
        }

        AppendPowerShellHeaderBlock(sb, headers);
        AppendPowerShellBody(sb, request);
        sb.Append(" -UseBasicParsing");
        return sb.ToString();
    }

    private static (string? ContentType, string? UserAgent, List<(string Name, string Value)> Headers) PartitionPowerShellHeaders(
        IEnumerable<(string Name, string Value)> requestHeaders)
    {
        string? contentType = null;
        string? userAgent = null;
        var headers = new List<(string Name, string Value)>();
        foreach (var header in requestHeaders)
        {
            if (header.Name.Equals("Content-Type", StringComparison.OrdinalIgnoreCase))
            {
                contentType = header.Value;
            }
            else if (header.Name.Equals("User-Agent", StringComparison.OrdinalIgnoreCase))
            {
                userAgent = header.Value;
            }
            else
            {
                headers.Add(header);
            }
        }

        return (contentType, userAgent, headers);
    }

    private static void AppendPowerShellHeaderBlock(StringBuilder sb, List<(string Name, string Value)> headers)
    {
        if (headers.Count == 0)
        {
            return;
        }

        sb.Append(" -Headers @{ ");
        for (var i = 0; i < headers.Count; i++)
        {
            if (i > 0)
            {
                sb.Append("; ");
            }

            sb.Append(PowerShellQuote(headers[i].Name))
                .Append(" = ")
                .Append(PowerShellQuote(headers[i].Value));
        }

        sb.Append(" }");
    }

    private static void AppendPowerShellBody(StringBuilder sb, CapturedRequest request)
    {
        if (request.BodyText is not null)
        {
            sb.Append(" -Body ").Append(PowerShellQuote(request.BodyText));
            return;
        }

        if (request.BodyBytes is not { Length: > 0 } bytes)
        {
            return;
        }

        sb.Append(" -Body ([byte[]](");
        for (var i = 0; i < bytes.Length; i++)
        {
            if (i > 0)
            {
                sb.Append(',');
            }

            sb.Append(bytes[i].ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        sb.Append("))");
    }

    /// <summary>Python <c>requests.request</c>. Binary bodies use a <c>data=</c> bytes literal.</summary>
    public static string ToPython(SessionSnapshot session)
    {
        if (!TryGetRequest(session, out var request))
        {
            throw new ArgumentException(NoReplayableUrlMessage, nameof(session));
        }

        var sb = new StringBuilder();
        sb.Append("import requests\nrequests.request(");
        sb.Append(PythonString(request.Method));
        sb.Append(", ");
        sb.Append(PythonString(request.Url));
        if (request.Headers.Count > 0)
        {
            sb.Append(", headers={");
            for (var i = 0; i < request.Headers.Count; i++)
            {
                if (i > 0)
                {
                    sb.Append(", ");
                }

                var header = request.Headers[i];
                sb.Append(PythonString(header.Name)).Append(": ").Append(PythonString(header.Value));
            }

            sb.Append('}');
        }

        if (request.BodyBytes is { Length: > 0 } bytes)
        {
            sb.Append(", data=").Append(PythonBytes(bytes));
        }
        else if (request.BodyText is not null)
        {
            sb.Append(", data=").Append(PythonString(request.BodyText));
        }

        sb.Append(')');
        return sb.ToString();
    }

    /// <summary>
    /// Tab-separated rows for a spreadsheet. Cells that start with <c>=</c>, <c>+</c>, <c>-</c>, or <c>@</c>
    /// are prefixed so the sheet treats them as text. At most <see cref="MaxTableRows"/> data rows.
    /// </summary>
    public static string ToTable(IReadOnlyList<SessionSnapshot> rows, out int written, out bool truncated)
    {
        var count = rows.Count;
        truncated = count > MaxTableRows;
        written = truncated ? MaxTableRows : count;
        var sb = new StringBuilder();
        sb.Append("Id\tMethod\tStatus\tURL\tComment\r\n");
        for (var i = 0; i < written; i++)
        {
            var row = rows[i];
            sb.Append(SpreadsheetCell(row.Id.ToString(System.Globalization.CultureInfo.InvariantCulture)));
            sb.Append('\t').Append(SpreadsheetCell(row.Method));
            sb.Append('\t').Append(SpreadsheetCell(
                row.StatusCode?.ToString(System.Globalization.CultureInfo.InvariantCulture)));
            sb.Append('\t').Append(SpreadsheetCell(row.Url));
            sb.Append('\t').Append(SpreadsheetCell(row.Comment));
            sb.Append("\r\n");
        }

        return sb.ToString();
    }

    /// <summary>Neutralize a spreadsheet cell: no tabs or newlines, and no formula trigger as the first character.</summary>
    internal static string SpreadsheetCell(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return "";
        }

        var text = value.Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ');
        return text[0] is '=' or '+' or '-' or '@' ? "'" + text : text;
    }

    /// <summary>PowerShell single quotes. An embedded quote is doubled.</summary>
    internal static string PowerShellQuote(string value) =>
        "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";

    /// <summary>Python single-quoted literal. Controls, quotes, and backslashes are escaped.</summary>
    internal static string PythonString(string value)
    {
        var sb = new StringBuilder(value.Length + 2);
        sb.Append('\'');
        foreach (var ch in value)
        {
            switch (ch)
            {
                case '\\':
                    sb.Append("\\\\");
                    break;
                case '\'':
                    sb.Append("\\'");
                    break;
                case '\r':
                    sb.Append("\\r");
                    break;
                case '\n':
                    sb.Append("\\n");
                    break;
                case '\t':
                    sb.Append("\\t");
                    break;
                default:
                    if (ch < ' ')
                    {
                        sb.Append("\\x").Append(((int)ch).ToString("x2", System.Globalization.CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        sb.Append(ch);
                    }

                    break;
            }
        }

        sb.Append('\'');
        return sb.ToString();
    }

    /// <summary>Python <c>bytes</c> literal. Every non-printable byte is <c>\xHH</c>.</summary>
    internal static string PythonBytes(ReadOnlySpan<byte> bytes)
    {
        var sb = new StringBuilder(bytes.Length + 3);
        sb.Append("b'");
        foreach (var b in bytes)
        {
            if (b is (>= 32 and <= 126) and not ((byte)'\\' or (byte)'\''))
            {
                sb.Append((char)b);
            }
            else if (b == (byte)'\\')
            {
                sb.Append("\\\\");
            }
            else if (b == (byte)'\'')
            {
                sb.Append("\\'");
            }
            else
            {
                sb.Append("\\x").Append(b.ToString("x2", System.Globalization.CultureInfo.InvariantCulture));
            }
        }

        sb.Append('\'');
        return sb.ToString();
    }

    /// <summary>POSIX single-quote escaping: wrap in <c>'...'</c>, with embedded quotes as <c>'\''</c>.</summary>
    internal static string ShellSingleQuote(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return "''";
        }

        return "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'";
    }
}
