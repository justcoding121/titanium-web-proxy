using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Titanium.Inspector.Services;

/// <summary>Generates curl and fetch snippets from a captured <see cref="SessionSnapshot"/> (offline; no hot path).</summary>
public static class SessionRequestCodegen
{
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

    /// <summary>Builds a shell-safe curl command for the request side of the session.</summary>
    public static string ToCurl(SessionSnapshot session)
    {
        if (!CanGenerate(session))
        {
            throw new ArgumentException("Session has no replayable URL (or is a CONNECT tunnel).", nameof(session));
        }

        var sb = new StringBuilder();
        sb.Append("curl ").Append(ShellSingleQuote(session.Url));

        var method = string.IsNullOrWhiteSpace(session.Method) ? "GET" : session.Method.Trim().ToUpperInvariant();
        if (!string.Equals(method, "GET", StringComparison.Ordinal))
        {
            sb.Append(" \\\n  -X ").Append(ShellSingleQuote(method));
        }

        foreach (var (name, value) in EnumerateHeaders(session.RequestHeadersText))
        {
            sb.Append(" \\\n  -H ").Append(ShellSingleQuote(name + ": " + value));
        }

        var body = ResolveBody(session);
        if (body is not null)
        {
            sb.Append(" \\\n  --data-binary ").Append(ShellSingleQuote(body));
        }

        return sb.ToString();
    }

    /// <summary>Builds a JavaScript <c>fetch(...)</c> call for the request side of the session.</summary>
    public static string ToFetch(SessionSnapshot session)
    {
        if (!CanGenerate(session))
        {
            throw new ArgumentException("Session has no replayable URL (or is a CONNECT tunnel).", nameof(session));
        }

        var method = string.IsNullOrWhiteSpace(session.Method) ? "GET" : session.Method.Trim().ToUpperInvariant();
        var headers = EnumerateHeaders(session.RequestHeadersText).ToList();
        var body = ResolveBody(session);

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

    private static string? ResolveBody(SessionSnapshot session)
    {
        if (!string.IsNullOrEmpty(session.RequestBodyText))
        {
            return session.RequestBodyText;
        }

        if (session.RequestBodyBytes is { Length: > 0 })
        {
            return Encoding.UTF8.GetString(session.RequestBodyBytes);
        }

        return null;
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
