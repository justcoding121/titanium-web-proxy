using System.Globalization;
using System.Text;

namespace Titanium.Inspector.Services;

/// <summary>Outcome of writing one session as raw HTTP text.</summary>
public readonly record struct RawHttpWriteResult(bool Truncated, long BytesWritten);

/// <summary>
/// Writes one session as HTTP/1.1 text. Bodies already in memory are copied with a cap.
/// Spilled bodies are read from the HAR file without loading them back onto the session.
/// </summary>
public static class SessionRawHttp
{
    public const long MaxBodyBytes = 32L * 1024 * 1024;

    public static RawHttpWriteResult Write(
        SessionSnapshot session,
        Stream output,
        Stream? spilledHar = null,
        long maxBodyBytes = MaxBodyBytes)
    {
        if (maxBodyBytes < 1)
        {
            maxBodyBytes = MaxBodyBytes;
        }

        var truncated = false;
        WriteAscii(output, RequestLine(session));
        WriteAscii(output, NormalizeHeaders(session.RequestHeadersText, session));
        WriteAscii(output, "\r\n");
        truncated |= WriteRequestBody(session, output, spilledHar, maxBodyBytes);

        if (session.StatusCode is int || !string.IsNullOrEmpty(session.ResponseHeadersText))
        {
            WriteAscii(output, "\r\n");
            var code = session.StatusCode ?? 0;
            var reason = Reason(code);
            WriteAscii(output, string.IsNullOrEmpty(reason)
                ? $"HTTP/1.1 {code.ToString(CultureInfo.InvariantCulture)}\r\n"
                : $"HTTP/1.1 {code.ToString(CultureInfo.InvariantCulture)} {reason}\r\n");
            WriteAscii(output, NormalizeNewlines(session.ResponseHeadersText));
            WriteAscii(output, "\r\n");
            truncated |= WriteResponseBody(session, output, spilledHar, maxBodyBytes);
        }

        return new RawHttpWriteResult(truncated, output.CanSeek ? output.Length : 0);
    }

    private static bool WriteRequestBody(SessionSnapshot session, Stream output, Stream? spilledHar, long maxBodyBytes)
    {
        if (session.RequestBodyBytes is { Length: > 0 } bytes)
        {
            return WriteCapped(output, bytes, maxBodyBytes);
        }

        if (!string.IsNullOrEmpty(session.RequestBodyText))
        {
            return WriteCapped(output, Encoding.UTF8.GetBytes(session.RequestBodyText), maxBodyBytes);
        }

        if (spilledHar is null || !session.BodiesOnDisk)
        {
            return false;
        }

        spilledHar.Position = 0;
        return HarBodyCopy.CopyProperty(spilledHar, "postData", "text", output, maxBodyBytes, encoding: null);
    }

    private static bool WriteResponseBody(SessionSnapshot session, Stream output, Stream? spilledHar, long maxBodyBytes)
    {
        if (session.ResponseBodyBytes is { Length: > 0 } bytes)
        {
            return WriteCapped(output, bytes, maxBodyBytes);
        }

        if (!string.IsNullOrEmpty(session.ResponseBodyText))
        {
            return WriteCapped(output, Encoding.UTF8.GetBytes(session.ResponseBodyText), maxBodyBytes);
        }

        if (spilledHar is null || !session.BodiesOnDisk)
        {
            return false;
        }

        spilledHar.Position = 0;
        return HarBodyCopy.CopyContent(spilledHar, output, maxBodyBytes);
    }

    private static bool WriteCapped(Stream output, byte[] bytes, long maxBodyBytes)
    {
        var count = bytes.Length;
        var truncated = count > maxBodyBytes;
        if (truncated)
        {
            count = (int)Math.Min(maxBodyBytes, int.MaxValue);
        }

        output.Write(bytes, 0, count);
        return truncated;
    }

    private static string RequestLine(SessionSnapshot session)
    {
        var method = string.IsNullOrWhiteSpace(session.Method) ? "GET" : session.Method.Trim().ToUpperInvariant();
        return $"{method} {RequestTarget(session)} HTTP/1.1\r\n";
    }

    private static string RequestTarget(SessionSnapshot session)
    {
        if (session.IsTunnel)
        {
            return string.IsNullOrWhiteSpace(session.Url) ? session.Host ?? "" : session.Url;
        }

        if (Uri.TryCreate(session.Url, UriKind.Absolute, out var uri))
        {
            return string.IsNullOrEmpty(uri.PathAndQuery) ? "/" : uri.PathAndQuery;
        }

        return string.IsNullOrEmpty(session.Url) ? "/" : session.Url;
    }

    private static string NormalizeHeaders(string? headerBlock, SessionSnapshot? hostFallback)
    {
        var text = NormalizeNewlines(headerBlock);
        if (hostFallback is not null
            && text.IndexOf("host:", StringComparison.OrdinalIgnoreCase) < 0
            && HostOf(hostFallback) is { Length: > 0 } host)
        {
            text = "Host: " + host + "\r\n" + text;
        }

        return text;
    }

    private static string NormalizeNewlines(string? headerBlock)
    {
        if (string.IsNullOrEmpty(headerBlock))
        {
            return "";
        }

        var text = headerBlock.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        text = text.Replace("\n", "\r\n", StringComparison.Ordinal);
        if (!text.EndsWith("\r\n", StringComparison.Ordinal))
        {
            text += "\r\n";
        }

        return text;
    }

    private static string? HostOf(SessionSnapshot session)
    {
        if (!string.IsNullOrWhiteSpace(session.Host))
        {
            return session.Host.Trim();
        }

        return Uri.TryCreate(session.Url, UriKind.Absolute, out var uri) ? uri.Host : null;
    }

    private static string Reason(int code) => code switch
    {
        200 => "OK",
        201 => "Created",
        204 => "No Content",
        301 => "Moved Permanently",
        302 => "Found",
        304 => "Not Modified",
        400 => "Bad Request",
        401 => "Unauthorized",
        403 => "Forbidden",
        404 => "Not Found",
        500 => "Internal Server Error",
        502 => "Bad Gateway",
        503 => "Service Unavailable",
        _ => "",
    };

    private static void WriteAscii(Stream output, string text)
    {
        if (text.Length == 0)
        {
            return;
        }

        var bytes = Encoding.UTF8.GetBytes(text);
        output.Write(bytes);
    }
}

/// <summary>
/// Copies one JSON string property out of a HAR document. The property value is decoded
/// into the destination and never stored as a session field. Bytes past the cap are skipped.
/// </summary>
internal static class HarBodyCopy
{
    public static bool CopyProperty(
        Stream har,
        string parent,
        string property,
        Stream destination,
        long maxBytes,
        string? encoding)
    {
        har.Position = 0;
        using var found = new MemoryStream();
        var scan = new Scanner(har);
        var truncated = scan.CopyFirst(parent, property, found, maxBytes);
        found.Position = 0;
        if (string.Equals(encoding, "base64", StringComparison.OrdinalIgnoreCase))
        {
            return WriteBase64(found, destination, maxBytes) || truncated;
        }

        found.CopyTo(destination);
        return truncated;
    }

    public static bool CopyContent(Stream har, Stream destination, long maxBytes)
    {
        har.Position = 0;
        var scan = new Scanner(har);
        using var text = new MemoryStream();
        var truncated = scan.CopyContentText(text, maxBytes, out var encoding);
        text.Position = 0;
        if (string.Equals(encoding, "base64", StringComparison.OrdinalIgnoreCase))
        {
            return WriteBase64(text, destination, maxBytes) || truncated;
        }

        text.CopyTo(destination);
        return truncated;
    }

    private static bool WriteBase64(Stream base64Text, Stream destination, long maxBytes)
    {
        var text = new StreamReader(base64Text, Encoding.UTF8).ReadToEnd();
        var chars = text.AsSpan();
        var quad = chars.Length - (chars.Length % 4);
        if (quad <= 0)
        {
            return text.Length > 0;
        }

        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(text[..quad]);
        }
        catch (FormatException)
        {
            var utf8 = Encoding.UTF8.GetBytes(text);
            destination.Write(utf8);
            return true;
        }

        var count = bytes.LongLength > maxBytes ? maxBytes : bytes.LongLength;
        destination.Write(bytes, 0, (int)count);
        return bytes.LongLength > maxBytes || text.Length != quad;
    }

    private sealed class Scanner
    {
        private readonly Stream _stream;
        private readonly byte[] _buf = new byte[8192];
        private int _pos;
        private int _len;

        public Scanner(Stream stream) => _stream = stream;

        public bool CopyFirst(string parent, string property, Stream destination, long maxBytes)
        {
            try
            {
                return ReadValue(destination, maxBytes, parent, property, out _);
            }
            catch (EndOfStreamException)
            {
                return false;
            }
        }

        public bool CopyContentText(Stream destination, long maxBytes, out string? encoding)
        {
            encoding = null;
            try
            {
                return ReadValue(destination, maxBytes, "content", "text", out encoding);
            }
            catch (EndOfStreamException)
            {
                return false;
            }
        }

        private bool ReadValue(
            Stream body,
            long maxBytes,
            string wantedParent,
            string wantedProperty,
            out string? contentEncoding)
        {
            contentEncoding = null;
            SkipWs();
            var kind = Peek();
            if (kind < 0)
            {
                return false;
            }

            if (kind == '{')
            {
                return ReadObject(body, maxBytes, wantedParent, wantedProperty, out contentEncoding);
            }

            if (kind == '[')
            {
                return ReadArray(body, maxBytes, wantedParent, wantedProperty, out contentEncoding);
            }

            if (kind == '"')
            {
                SkipString(body: null, 0, out _);
                return false;
            }

            SkipLiteral();
            return false;
        }

        private bool ReadObject(
            Stream body,
            long maxBytes,
            string wantedParent,
            string wantedProperty,
            out string? contentEncoding)
        {
            contentEncoding = null;
            Expect('{');
            var truncated = false;
            string? encoding = null;
            SkipWs();
            if (Peek() == '}')
            {
                Read();
                contentEncoding = encoding;
                return false;
            }

            while (true)
            {
                SkipWs();
                var name = ReadKey();
                SkipWs();
                Expect(':');
                SkipWs();
                truncated |= ReadObjectPropertyValue(
                    body,
                    maxBytes,
                    wantedParent,
                    wantedProperty,
                    name,
                    ref encoding,
                    out var nestedEncoding);
                if (nestedEncoding is not null)
                {
                    contentEncoding = nestedEncoding;
                }

                SkipWs();
                var sep = Read();
                if (sep == '}')
                {
                    break;
                }

                if (sep != ',')
                {
                    break;
                }
            }

            contentEncoding ??= encoding;
            return truncated;
        }

        private bool ReadObjectPropertyValue(
            Stream body,
            long maxBytes,
            string wantedParent,
            string wantedProperty,
            string name,
            ref string? encoding,
            out string? nestedEncoding)
        {
            nestedEncoding = null;
            var next = Peek();
            if (next == '{')
            {
                _parents.Push(name);
                var truncated = ReadObject(body, maxBytes, wantedParent, wantedProperty, out nestedEncoding);
                if (_parents.Count > 0)
                {
                    _parents.Pop();
                }

                return truncated;
            }

            if (next == '[')
            {
                _parents.Push(name);
                var truncated = ReadArray(body, maxBytes, wantedParent, wantedProperty, out nestedEncoding);
                if (_parents.Count > 0)
                {
                    _parents.Pop();
                }

                return truncated;
            }

            if (next == '"')
            {
                if (name.Equals(wantedProperty, StringComparison.Ordinal)
                    && wantedParent.Length > 0
                    && InParent(wantedParent))
                {
                    return SkipString(body, maxBytes, out _);
                }

                if (name.Equals("encoding", StringComparison.Ordinal) && InParent("content"))
                {
                    encoding = ReadKey();
                    return false;
                }

                SkipString(body: null, 0, out _);
                return false;
            }

            SkipLiteral();
            return false;
        }

        private bool ReadArray(
            Stream body,
            long maxBytes,
            string wantedParent,
            string wantedProperty,
            out string? contentEncoding)
        {
            contentEncoding = null;
            Expect('[');
            var truncated = false;
            SkipWs();
            if (Peek() == ']')
            {
                Read();
                return false;
            }

            while (true)
            {
                truncated |= ReadValue(body, maxBytes, wantedParent, wantedProperty, out var nested);
                if (nested is not null)
                {
                    contentEncoding = nested;
                }

                SkipWs();
                var sep = Read();
                if (sep == ']')
                {
                    break;
                }

                if (sep != ',')
                {
                    break;
                }
            }

            return truncated;
        }

        private readonly Stack<string> _parents = new();

        private bool InParent(string name) =>
            _parents.Any(parent => parent.Equals(name, StringComparison.Ordinal));

        private string ReadKey()
        {
            var sb = new StringBuilder();
            SkipStringTo(sb, 512);
            return sb.ToString();
        }

        private bool SkipString(Stream? body, long maxBytes, out bool truncated)
        {
            truncated = false;
            Expect('"');
            long written = 0;
            while (true)
            {
                var b = Read();
                if (b < 0)
                {
                    throw new EndOfStreamException();
                }

                if (b == '"')
                {
                    return truncated;
                }

                if (b == '\\')
                {
                    var esc = Read();
                    var decoded = esc switch
                    {
                        '"' => (byte)'"',
                        '\\' => (byte)'\\',
                        '/' => (byte)'/',
                        'b' => (byte)'\b',
                        'f' => (byte)'\f',
                        'n' => (byte)'\n',
                        'r' => (byte)'\r',
                        't' => (byte)'\t',
                        'u' => DecodeUnicode(),
                        _ => esc < 0 ? (byte)0 : (byte)esc,
                    };
                    truncated |= Emit(body, decoded, ref written, maxBytes);
                    continue;
                }

                truncated |= Emit(body, (byte)b, ref written, maxBytes);
            }
        }

        private void SkipStringTo(StringBuilder sb, int maxChars)
        {
            Expect('"');
            while (true)
            {
                var b = Read();
                if (b < 0)
                {
                    throw new EndOfStreamException();
                }

                if (b == '"')
                {
                    return;
                }

                if (b == '\\')
                {
                    AppendSkipStringEscape(sb, maxChars);
                    continue;
                }

                AppendSkipStringChar(sb, maxChars, b);
            }
        }

        private void AppendSkipStringEscape(StringBuilder sb, int maxChars)
        {
            var esc = Read();
            if (esc == 'u')
            {
                _ = DecodeUnicode();
            }

            if (sb.Length < maxChars && esc is >= 32 and <= 126)
            {
                sb.Append((char)esc);
            }
        }

        private static void AppendSkipStringChar(StringBuilder sb, int maxChars, int b)
        {
            if (sb.Length < maxChars && b is >= 32 and <= 126)
            {
                sb.Append((char)b);
            }
        }

        private byte DecodeUnicode()
        {
            Span<char> hex = stackalloc char[4];
            for (var i = 0; i < 4; i++)
            {
                var h = Read();
                hex[i] = h < 0 ? '0' : (char)h;
            }

            if (!ushort.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var code))
            {
                return (byte)'?';
            }

            return code <= 0x7F ? (byte)code : (byte)'?';
        }

        private static bool Emit(Stream? body, byte value, ref long written, long maxBytes)
        {
            if (body is null)
            {
                return false;
            }

            if (written >= maxBytes)
            {
                return true;
            }

            body.WriteByte(value);
            written++;
            return false;
        }

        private void SkipLiteral()
        {
            while (true)
            {
                var b = Peek();
                if (b < 0 || b is ',' or '}' or ']' or ' ' or '\t' or '\r' or '\n')
                {
                    return;
                }

                Read();
            }
        }

        private void SkipWs()
        {
            while (true)
            {
                var b = Peek();
                if (b is not (' ' or '\t' or '\r' or '\n'))
                {
                    return;
                }

                Read();
            }
        }

        private void Expect(char expected)
        {
            SkipWs();
            var b = Read();
            if (b != expected)
            {
                throw new EndOfStreamException();
            }
        }

        private int Peek()
        {
            if (_pos >= _len)
            {
                _len = _stream.Read(_buf, 0, _buf.Length);
                _pos = 0;
                if (_len == 0)
                {
                    return -1;
                }
            }

            return _buf[_pos];
        }

        private int Read()
        {
            var value = Peek();
            if (value >= 0)
            {
                _pos++;
            }

            return value;
        }
    }
}
