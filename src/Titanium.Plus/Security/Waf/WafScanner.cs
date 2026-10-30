using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Titanium.Web.Proxy.Http;

namespace Titanium.Plus.Security.Waf;

/// <summary>
/// Screens one request against a profile. One instance lives per thread (the scan is synchronous), so a request
/// that matches nothing allocates nothing: values are normalised in stack buffers or pooled arrays and rules read
/// spans.
/// </summary>
internal sealed class WafScanner
{
    private const int MaxScanChars = 64 * 1024;
    private const int MaxDecodePasses = 4;
    private const int StackChars = 256;

    [ThreadStatic]
    private static WafScanner? threadInstance;

    private WafOptions _options = null!;
    private WafProfile _profile = null!;
    private WafRuleSet _set = null!;
    private int _score;
    private bool _blockNow;
    private int _timeouts;
    private bool[] _matched = new bool[64];
    private List<WafMatch>? _matches;

    public static WafScanner ForThread() => threadInstance ??= new WafScanner();

    public WafVerdict Run(WafOptions options, WafProfile profile, in WafRequest request)
    {
        _options = options;
        _profile = profile;
        _set = profile.RuleSet;
        _score = 0;
        _blockNow = false;
        _timeouts = 0;
        _matches?.Clear();
        if (_matched.Length < _set.All.Length)
        {
            _matched = new bool[_set.All.Length];
        }
        else
        {
            Array.Clear(_matched, 0, _set.All.Length);
        }

        if (request.Target is { Length: > 0 } target)
        {
            ScanTarget(target);
        }

        if (request.Headers is { } headers)
        {
            ScanHeaders(headers);
        }

        if (!request.Body.IsEmpty)
        {
            ScanBody(request.Body.Span, request.ContentType);
        }

        return Finish();
    }

    private WafVerdict Finish()
    {
        var wouldBlock = _blockNow || (_score > 0 && _score >= _profile.BlockThreshold);
        var blocked = wouldBlock && _profile.Mode == WafMode.Block;
        var matches = _matches is { Count: > 0 } ? _matches.ToArray() : null;
        return new WafVerdict(_profile.Mode, _score, wouldBlock, blocked, matches, _timeouts);
    }

    // ---- request line -----------------------------------------------------------------------------------------

    private static bool IsSchemeName(ReadOnlySpan<char> text)
    {
        foreach (var c in text)
        {
            if (!char.IsAsciiLetterOrDigit(c) && c is not ('+' or '-' or '.'))
            {
                return false;
            }
        }

        return true;
    }

    private void ScanTarget(string target)
    {
        ReadOnlySpan<char> text = target;
        // Absolute-form (http://host/path?q): only when the whole target starts with a scheme, never for a URL inside a value.
        var scheme = text.IndexOf("://", StringComparison.Ordinal);
        if (scheme is > 0 and < 12 && text[0] != '/' && IsSchemeName(text[..scheme]))
        {
            var rest = text[(scheme + 3)..];
            var cut = rest.IndexOfAny('/', '?', '#');
            text = cut < 0 ? "/" : rest[cut..];
        }

        var hash = text.IndexOf('#');
        if (hash >= 0)
        {
            text = text[..hash];
        }

        var question = text.IndexOf('?');
        var path = question < 0 ? text : text[..question];
        ScanValue(path, WafTargets.Path, default, formEncoded: false);
        if (question >= 0)
        {
            ScanPairs(text[(question + 1)..], WafTargets.Query);
        }
    }

    private void ScanPairs(ReadOnlySpan<char> text, WafTargets target)
    {
        while (!text.IsEmpty)
        {
            var amp = text.IndexOf('&');
            var pair = amp < 0 ? text : text[..amp];
            text = amp < 0 ? default : text[(amp + 1)..];
            if (pair.IsEmpty)
            {
                continue;
            }

            var eq = pair.IndexOf('=');
            if (eq < 0)
            {
                // No "=": the fragment is the payload (?<script>alert(1)</script>).
                ScanValue(pair, target, default, formEncoded: true);
            }
            else
            {
                ScanValue(pair[(eq + 1)..], target, pair[..eq], formEncoded: true);
            }
        }
    }

    // ---- headers and cookies ----------------------------------------------------------------------------------

    private void ScanHeaders(HeaderCollection headers)
    {
        var hosts = 0;
        var contentLengths = 0;
        var contentLengthBad = false;
        string? firstContentLength = null;
        var transferEncoding = false;
        foreach (var header in headers)
        {
            var name = header.Name;
            switch (name.Length)
            {
                case 4 when name.Equals("Host", StringComparison.OrdinalIgnoreCase):
                    hosts++;
                    break;
                case 6 when name.Equals("Cookie", StringComparison.OrdinalIgnoreCase):
                    ScanCookies(header.Value);
                    continue;
                case 13 when name.Equals("Authorization", StringComparison.OrdinalIgnoreCase):
                    continue;
                case 14 when name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase):
                    contentLengths++;
                    var length = header.Value;
                    firstContentLength ??= length;
                    contentLengthBad |= !IsPlainLength(length) || !string.Equals(length, firstContentLength, StringComparison.Ordinal);
                    continue;
                case 17 when name.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase):
                    transferEncoding = true;
                    break;
                case 19 when name.Equals("Proxy-Authorization", StringComparison.OrdinalIgnoreCase):
                    continue;
            }

            ScanValue(header.Value, WafTargets.Header, name, formEncoded: false);
        }

        if (hosts > 1)
        {
            FireCheck(WafCheck.DuplicateHost, WafTargets.Header, "Host");
        }

        if (contentLengthBad)
        {
            FireCheck(WafCheck.InvalidContentLength, WafTargets.Header, "Content-Length");
        }

        if (contentLengths > 0 && transferEncoding)
        {
            FireCheck(WafCheck.ContentLengthWithTransferEncoding, WafTargets.Header, "Transfer-Encoding");
        }
    }

    private static bool IsPlainLength(string value) =>
        value.Length is > 0 and <= 18 && value.AsSpan().IndexOfAnyExceptInRange('0', '9') < 0;

    private void ScanCookies(string header)
    {
        ReadOnlySpan<char> text = header;
        while (!text.IsEmpty)
        {
            var semi = text.IndexOf(';');
            var pair = (semi < 0 ? text : text[..semi]).Trim();
            text = semi < 0 ? default : text[(semi + 1)..];
            if (pair.IsEmpty)
            {
                continue;
            }

            var eq = pair.IndexOf('=');
            if (eq < 0)
            {
                ScanValue(pair, WafTargets.Cookie, default, formEncoded: false);
            }
            else
            {
                ScanValue(pair[(eq + 1)..], WafTargets.Cookie, pair[..eq].Trim(), formEncoded: false);
            }
        }
    }

    // ---- one value --------------------------------------------------------------------------------------------

    private void ScanValue(ReadOnlySpan<char> raw, WafTargets target, ReadOnlySpan<char> name, bool formEncoded) // NOSONAR S3776 -- Decode passes share pooled buffers and rule targets.
    {
        if (raw.IsEmpty)
        {
            return;
        }

        var fullLength = raw.Length;
        if (raw.Length > MaxScanChars)
        {
            raw = raw[..MaxScanChars];
        }

        char[]? rentedA = null;
        char[]? rentedB = null;
        // Ternary keeps stackalloc in one expression (required by definite-assignment / escape rules).
        Span<char> a = raw.Length <= StackChars ? stackalloc char[StackChars] : (rentedA = ArrayPool<char>.Shared.Rent(raw.Length));
        Span<char> b = raw.Length <= StackChars ? stackalloc char[StackChars] : (rentedB = ArrayPool<char>.Shared.Rent(raw.Length));
        try
        {
            // The first slice is local so later slices of the stack buffers may be assigned to it.
            ReadOnlySpan<char> current = a[..0];
            current = raw;
            var nextIsA = true;
            var percentPasses = 0;
            for (var pass = 0; pass < MaxDecodePasses; pass++)
            {
                var percent = WafText.HasPercentEscape(current);
                var plus = formEncoded && pass == 0 && current.Contains('+');
                if (!percent && !plus)
                {
                    break;
                }

                if (percent)
                {
                    percentPasses++;
                }

                var dst = nextIsA ? a : b;
                current = dst[..WafText.UrlDecode(current, dst, plusAsSpace: formEncoded && pass == 0)];
                nextIsA = !nextIsA;
            }

            var sawNull = current.Contains('\0') || raw.Contains('\0');
            if (sawNull)
            {
                var dst = nextIsA ? a : b;
                current = dst[..WafText.RemoveNulls(current, dst)];
                nextIsA = !nextIsA;
            }

            if (current.Contains('&'))
            {
                var dst = nextIsA ? a : b;
                current = dst[..WafText.HtmlEntityDecode(current, dst)];
            }

            if (sawNull)
            {
                FireCheck(WafCheck.NullByte, target, name);
            }

            if (percentPasses >= 3)
            {
                FireCheck(WafCheck.EncodingDepth, target, name);
            }

            if (fullLength > _options.MaxArgLength && target is WafTargets.Query or WafTargets.Body)
            {
                FireCheck(WafCheck.OversizedArg, target, name);
            }

            RunRules(raw, current, target, name);
        }
        finally
        {
            if (rentedA is not null)
            {
                ArrayPool<char>.Shared.Return(rentedA);
            }

            if (rentedB is not null)
            {
                ArrayPool<char>.Shared.Return(rentedB);
            }
        }
    }

    private void RunRules(ReadOnlySpan<char> raw, ReadOnlySpan<char> normalised, WafTargets target, ReadOnlySpan<char> name)
    {
        var entries = _set.For(target);
        for (var i = 0; i < entries.Length; i++)
        {
            var entry = entries[i];
            if (_matched[entry.Index])
            {
                continue;
            }

            var rule = entry.Rule;
            if (target == WafTargets.Header && rule.HeaderNames is not null && !rule.ReadsHeader(name))
            {
                continue;
            }

            if (_profile.Exclusions.Length > 0 && IsExcluded(rule, target, name))
            {
                continue;
            }

            var hit = rule.HasTransforms
                ? rule.MatchesTransformed(raw, ref _timeouts)
                : rule.Matches(normalised, ref _timeouts);
            if (hit)
            {
                Fire(entry.Rule, entry.Index, target, name);
            }
        }
    }

    private bool IsExcluded(WafRule rule, WafTargets target, ReadOnlySpan<char> name)
    {
        var exclusions = _profile.Exclusions;
        for (var i = 0; i < exclusions.Length; i++)
        {
            if (exclusions[i].Applies(rule, target, name))
            {
                return true;
            }
        }

        return false;
    }

    private void FireCheck(WafCheck check, WafTargets target, ReadOnlySpan<char> name)
    {
        if (_set.Checks[(int)check] is not { } entry || _matched[entry.Index] || (entry.Rule.Targets & target) == 0)
        {
            return;
        }

        if (_profile.Exclusions.Length > 0 && IsExcluded(entry.Rule, target, name))
        {
            return;
        }

        Fire(entry.Rule, entry.Index, target, name);
    }

    private void Fire(WafRule rule, int index, WafTargets target, ReadOnlySpan<char> name)
    {
        _matched[index] = true;
        var score = rule.Action == WafAction.Log ? 0 : rule.Score ?? _options.WeightOf(rule.Severity);
        _score += score;
        if (rule.Action == WafAction.Block)
        {
            _blockNow = true;
        }

        (_matches ??= []).Add(new WafMatch(rule.Id, rule.Category, rule.Severity, target, name.IsEmpty ? null : name.ToString(), score));
    }

    // ---- bodies -----------------------------------------------------------------------------------------------

    private void ScanBody(ReadOnlySpan<byte> body, string? contentType)
    {
        switch (WafBodyPolicy.Classify(contentType))
        {
            case WafBodyKind.Urlencoded:
                ScanDecoded(body, urlencoded: true);
                break;
            case WafBodyKind.Json:
                ScanJson(body);
                break;
            case WafBodyKind.Multipart:
                ScanMultipart(body, contentType!);
                break;
        }
    }

    private void ScanDecoded(ReadOnlySpan<byte> body, bool urlencoded)
    {
        var chars = ArrayPool<char>.Shared.Rent(body.Length);
        try
        {
            var length = Encoding.UTF8.GetChars(body, chars);
            var text = chars.AsSpan(0, length);
            if (urlencoded)
            {
                ScanPairs(text, WafTargets.Body);
            }
            else
            {
                ScanValue(text, WafTargets.Body, default, formEncoded: false);
            }
        }
        finally
        {
            ArrayPool<char>.Shared.Return(chars);
        }
    }

    private void ScanJson(ReadOnlySpan<byte> body)
    {
        var buffer = ArrayPool<char>.Shared.Rent(body.Length * 2);
        try
        {
            var values = buffer.AsSpan(0, body.Length);
            var names = buffer.AsSpan(body.Length, body.Length);
            var nameLength = 0;
            var reader = new Utf8JsonReader(body, new JsonReaderOptions
            {
                MaxDepth = 32,
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip,
            });
            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.PropertyName)
                {
                    nameLength = reader.CopyString(names);
                }
                else if (reader.TokenType == JsonTokenType.String)
                {
                    var length = reader.CopyString(values);
                    ScanValue(values[..length], WafTargets.Body, names[..nameLength], formEncoded: false);
                }
            }
        }
        catch (JsonException)
        {
            // A body that claims to be JSON but is not still reaches the origin. Screen it as plain text.
            ScanDecoded(body, urlencoded: false);
        }
        finally
        {
            ArrayPool<char>.Shared.Return(buffer);
        }
    }

    private void ScanMultipart(ReadOnlySpan<byte> body, string contentType) // NOSONAR S3776 -- Multipart scanning walks boundaries and part headers in one pass.
    {
        var boundary = WafBodyPolicy.MultipartBoundary(contentType);
        if (boundary is null || boundary.Length > 200)
        {
            return;
        }

        Span<byte> delimiter = stackalloc byte[boundary.Length + 2];
        delimiter[0] = (byte)'-';
        delimiter[1] = (byte)'-';
        for (var i = 0; i < boundary.Length; i++)
        {
            if (boundary[i] > 0x7E)
            {
                return;
            }

            delimiter[i + 2] = (byte)boundary[i];
        }

        var position = body.IndexOf(delimiter);
        while (position >= 0)
        {
            position += delimiter.Length;
            if (position + 1 < body.Length && body[position] == '-' && body[position + 1] == '-')
            {
                return;
            }

            var eol = body[position..].IndexOf((byte)'\n');
            if (eol < 0)
            {
                return;
            }

            position += eol + 1;
            var rest = body[position..];
            var separator = 4;
            var headerEnd = rest.IndexOf("\r\n\r\n"u8);
            if (headerEnd < 0)
            {
                separator = 2;
                headerEnd = rest.IndexOf("\n\n"u8);
            }

            if (headerEnd < 0)
            {
                return;
            }

            var headerBytes = rest[..headerEnd];
            position += headerEnd + separator;
            var next = body[position..].IndexOf(delimiter);
            var contentEnd = next < 0 ? body.Length : position + next;
            var content = body[position..contentEnd];
            if (next >= 0)
            {
                content = TrimTrailingLineBreak(content);
            }

            ScanPart(headerBytes, content);
            position = next < 0 ? -1 : position + next;
        }
    }

    private void ScanPart(ReadOnlySpan<byte> headerBytes, ReadOnlySpan<byte> content)
    {
        if (content.IsEmpty || headerBytes.Length > 1024)
        {
            return;
        }

        Span<char> headers = stackalloc char[headerBytes.Length];
        Encoding.Latin1.GetChars(headerBytes, headers);
        if (!WafBodyPolicy.IsTextPart(headers, out var fieldName))
        {
            return;
        }

        var chars = ArrayPool<char>.Shared.Rent(content.Length);
        try
        {
            var length = Encoding.UTF8.GetChars(content, chars);
            ScanValue(chars.AsSpan(0, length), WafTargets.Body, fieldName, formEncoded: false);
        }
        finally
        {
            ArrayPool<char>.Shared.Return(chars);
        }
    }

    private static ReadOnlySpan<byte> TrimTrailingLineBreak(ReadOnlySpan<byte> content)
    {
        if (content.EndsWith("\r\n"u8))
        {
            return content[..^2];
        }

        return content.EndsWith("\n"u8) ? content[..^1] : content;
    }
}

internal enum WafBodyKind
{
    None,
    Urlencoded,
    Json,
    Multipart,
}

/// <summary>
/// Decides whether a request body is read, and what parser it gets. Only urlencoded forms, JSON, and multipart text
/// fields are ever screened, and only when the whole body is known to be small.
/// </summary>
public static class WafBodyPolicy
{
    internal static WafBodyKind Classify(string? contentType)
    {
        if (string.IsNullOrEmpty(contentType))
        {
            return WafBodyKind.None;
        }

        var span = contentType.AsSpan();
        var semi = span.IndexOf(';');
        var media = (semi < 0 ? span : span[..semi]).Trim();
        if (media.Equals("application/x-www-form-urlencoded", StringComparison.OrdinalIgnoreCase))
        {
            return WafBodyKind.Urlencoded;
        }

        if (media.Equals("application/json", StringComparison.OrdinalIgnoreCase) ||
            media.EndsWith("+json", StringComparison.OrdinalIgnoreCase))
        {
            return WafBodyKind.Json;
        }

        return media.Equals("multipart/form-data", StringComparison.OrdinalIgnoreCase)
            ? WafBodyKind.Multipart
            : WafBodyKind.None;
    }

    /// <summary>
    /// Says whether the body is read for screening: <see cref="WafBodyOutcome.Inspected"/>, or the reason it is not.
    /// <paramref name="contentLength"/> is -1 when the length is not declared.
    /// </summary>
    public static WafBodyOutcome Decide(bool hasBody, long contentLength, string? contentType, string? contentEncoding, int limit)
    {
        if (!hasBody || contentLength == 0)
        {
            return WafBodyOutcome.None;
        }

        if (limit <= 0)
        {
            return WafBodyOutcome.Disabled;
        }

        if (Classify(contentType) == WafBodyKind.None)
        {
            return WafBodyOutcome.NotEligible;
        }

        if (!string.IsNullOrWhiteSpace(contentEncoding) &&
            !contentEncoding.Trim().Equals("identity", StringComparison.OrdinalIgnoreCase))
        {
            return WafBodyOutcome.Encoded;
        }

        if (contentLength < 0)
        {
            return WafBodyOutcome.UnknownLength;
        }

        return contentLength > limit ? WafBodyOutcome.TooLarge : WafBodyOutcome.Inspected;
    }

    internal static string? MultipartBoundary(string contentType)
    {
        var span = contentType.AsSpan();
        var index = span.IndexOf("boundary=", StringComparison.OrdinalIgnoreCase);
        if (index < 0)
        {
            return null;
        }

        var value = span[(index + 9)..];
        if (!value.IsEmpty && value[0] == '"')
        {
            value = value[1..];
            var close = value.IndexOf('"');
            return close <= 0 ? null : value[..close].ToString();
        }

        var end = value.IndexOf(';');
        value = (end < 0 ? value : value[..end]).Trim();
        return value.IsEmpty ? null : value.ToString();
    }

    /// <summary>
    /// True for a multipart part that holds a text field. A part with a <c>filename</c>, or a content type that is
    /// not text, JSON, or a form, is an upload and is skipped.
    /// </summary>
    internal static bool IsTextPart(ReadOnlySpan<char> headers, out ReadOnlySpan<char> fieldName)
    {
        fieldName = default;
        if (headers.Contains("filename", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        fieldName = DispositionName(headers);
        var typeIndex = headers.IndexOf("content-type:", StringComparison.OrdinalIgnoreCase);
        if (typeIndex < 0)
        {
            return true;
        }

        var value = headers[(typeIndex + 13)..];
        var end = value.IndexOfAny('\r', '\n');
        value = (end < 0 ? value : value[..end]).Trim();
        return value.StartsWith("text/", StringComparison.OrdinalIgnoreCase) ||
               value.StartsWith("application/json", StringComparison.OrdinalIgnoreCase) ||
               value.StartsWith("application/x-www-form-urlencoded", StringComparison.OrdinalIgnoreCase);
    }

    private static ReadOnlySpan<char> DispositionName(ReadOnlySpan<char> headers)
    {
        var offset = 0;
        while (offset < headers.Length)
        {
            var found = headers[offset..].IndexOf("name=", StringComparison.OrdinalIgnoreCase);
            if (found < 0)
            {
                return default;
            }

            var at = offset + found;
            if (at > 0 && headers[at - 1] is not (' ' or ';' or '\t'))
            {
                offset = at + 5;
                continue;
            }

            var value = headers[(at + 5)..];
            if (!value.IsEmpty && value[0] == '"')
            {
                value = value[1..];
                var close = value.IndexOf('"');
                return close < 0 ? default : value[..close];
            }

            var end = value.IndexOfAny(';', '\r', '\n');
            return (end < 0 ? value : value[..end]).Trim();
        }

        return default;
    }

}
