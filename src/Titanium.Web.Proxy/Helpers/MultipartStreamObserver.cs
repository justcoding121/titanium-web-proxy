using System;
using System.Text;
using Titanium.Web.Proxy.Http;
using Titanium.Web.Proxy.Models;

namespace Titanium.Web.Proxy.Helpers;

/// <summary>
///     A pass-through observer that identifies multipart/form-data boundaries in a streaming
///     body without buffering the full content. Bytes are forwarded verbatim (parsing is
///     purely observational). Each part is reported via <see cref="OnPartHeaders"/> and
///     <see cref="OnPartComplete"/> callbacks.
///
///     Limits:
///     - Maximum boundary length: 70 bytes (RFC 2046 §5.1.1)
///     - Maximum number of headers per part: 100
///     - Maximum total header bytes per part: 8,192
///     - Maximum nesting depth: 1 (nested multipart is detected but not recursively parsed)
///     - Look-behind buffer: boundary.Length + 6 bytes (for CRLF--boundary)
/// </summary>
internal sealed class MultipartStreamObserver
{
    private const int MaxBoundaryLength = 70;

    private readonly byte[] _openingBoundary;        // CRLF + "--" + boundary + CRLF
    private readonly byte[] _closingBoundary;        // CRLF + "--" + boundary + "--"
    private readonly byte[] _initialOpeningBoundary; // "--" + boundary + CRLF, valid only at offset zero
    private readonly byte[] _initialClosingBoundary; // "--" + boundary + "--", valid only at offset zero
    private readonly Action<HeaderCollection>? _onPartHeaders;
    private readonly Action? _onPartComplete;

    // Look-behind ring buffer state
    private readonly byte[] _lookBehind;
    private int _lookBehindFill;
    private long _bytesObserved;

    private bool _inBody;    // past the preamble
    private bool _finished;  // closing boundary seen

    // Accumulates header lines for the current part
    private readonly byte[] _headerBuffer;
    private int _headerFill;
    private bool _inPartHeaders;

    private const int MaxPartHeaderBytesPerPart = 8192;

    private MultipartStreamObserver(
        string boundaryString,
        Action<HeaderCollection>? onPartHeaders,
        Action? onPartComplete)
    {
        if (boundaryString.Length > MaxBoundaryLength)
            throw new ArgumentException(
                $"Boundary '{boundaryString}' exceeds {MaxBoundaryLength} characters.",
                nameof(boundaryString));

        _onPartHeaders = onPartHeaders;
        _onPartComplete = onPartComplete;

        var boundaryBytes = Encoding.ASCII.GetBytes(boundaryString);

        var delimiter = new byte[4 + boundaryBytes.Length];
        delimiter[0] = (byte)'\r';
        delimiter[1] = (byte)'\n';
        delimiter[2] = (byte)'-';
        delimiter[3] = (byte)'-';
        Buffer.BlockCopy(boundaryBytes, 0, delimiter, 4, boundaryBytes.Length);

        _openingBoundary = Append(delimiter, (byte)'\r', (byte)'\n');
        _closingBoundary = Append(delimiter, (byte)'-', (byte)'-');

        var initialDelimiter = new byte[2 + boundaryBytes.Length];
        initialDelimiter[0] = (byte)'-';
        initialDelimiter[1] = (byte)'-';
        Buffer.BlockCopy(boundaryBytes, 0, initialDelimiter, 2, boundaryBytes.Length);
        _initialOpeningBoundary = Append(initialDelimiter, (byte)'\r', (byte)'\n');
        _initialClosingBoundary = Append(initialDelimiter, (byte)'-', (byte)'-');

        // The look-behind window must hold at least the closing boundary so we can detect it.
        _lookBehind = new byte[Math.Max(_openingBoundary.Length, _closingBoundary.Length)];

        _headerBuffer = new byte[MaxPartHeaderBytesPerPart];
    }

    private static byte[] Append(byte[] prefix, byte first, byte second)
    {
        var result = new byte[prefix.Length + 2];
        Buffer.BlockCopy(prefix, 0, result, 0, prefix.Length);
        result[prefix.Length] = first;
        result[prefix.Length + 1] = second;
        return result;
    }

    /// <summary>
    ///     Tries to create an observer for the given Content-Type value.  Returns
    ///     <see langword="null"/> if the content-type is not multipart or has no boundary.
    /// </summary>
    internal static MultipartStreamObserver? TryCreate(
        string? contentType,
        Action<HeaderCollection>? onPartHeaders,
        Action? onPartComplete)
    {
        if (contentType == null ||
            !contentType.StartsWith("multipart/", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var boundary = HttpHelper.GetBoundaryFromContentType(contentType);
        if (boundary.IsEmpty || string.IsNullOrWhiteSpace(boundary.ToString())) return null;

        try { return new MultipartStreamObserver(boundary.ToString(), onPartHeaders, onPartComplete); }
        catch { return null; }
    }

    /// <summary>
    ///     Process a chunk of body bytes. This call is purely observational — the caller is
    ///     responsible for forwarding the same bytes to the downstream writer.
    ///     Returns <see langword="true"/> while the observer is still active, and
    ///     <see langword="false"/> once the closing boundary has been seen.
    /// </summary>
    internal bool Observe(ReadOnlySpan<byte> chunk)
    {
        if (_finished) return false;

        foreach (var b in chunk)
        {
            ObserveByte(b);
            if (_finished) break;
        }

        return !_finished;
    }

    private void ObserveByte(byte b)
    {
        _bytesObserved++;

        // Feed the byte into the look-behind sliding window.
        if (_lookBehindFill < _lookBehind.Length)
        {
            _lookBehind[_lookBehindFill++] = b;
        }
        else
        {
            Buffer.BlockCopy(_lookBehind, 1, _lookBehind, 0, _lookBehind.Length - 1);
            _lookBehind[_lookBehind.Length - 1] = b;
        }

        // When in part-header parsing mode, also accumulate bytes for header decoding.
        bool wasParsingHeaders = _inPartHeaders;
        if (wasParsingHeaders)
        {
            if (_headerFill < _headerBuffer.Length)
                _headerBuffer[_headerFill++] = b;

            // Detect end-of-headers: \r\n\r\n
            bool endOfHeaders = _headerFill >= 4 &&
                _headerBuffer[_headerFill - 4] == '\r' &&
                _headerBuffer[_headerFill - 3] == '\n' &&
                _headerBuffer[_headerFill - 2] == '\r' &&
                _headerBuffer[_headerFill - 1] == '\n';

            // Treat a full buffer as a truncated header block so the observer never stalls.
            bool truncated = _headerFill == _headerBuffer.Length;

            if (endOfHeaders || truncated)
            {
                _inPartHeaders = false;
                var headers = ParsePartHeaders();
                _onPartHeaders?.Invoke(headers);
                _headerFill = 0;
            }
        }

        // Delimiter-looking bytes inside the MIME header section are header data,
        // not multipart delimiters. Resume boundary scanning only after the blank
        // line (or the bounded-header fallback) transitions into the part body.
        if (!wasParsingHeaders) CheckForBoundary();
    }

    private void CheckForBoundary()
    {
        // Closing delimiters are checked first. Opening delimiters include their trailing
        // CRLF, so MIME header collection starts at the first header byte rather than
        // accidentally treating the delimiter line ending as an empty header block.
        if (EndsWith(_closingBoundary) ||
            (_bytesObserved == _initialClosingBoundary.Length && EndsWith(_initialClosingBoundary)))
        {
            _finished = true;
            if (_inBody) _onPartComplete?.Invoke();
            return;
        }

        if (EndsWith(_openingBoundary) ||
            (_bytesObserved == _initialOpeningBoundary.Length && EndsWith(_initialOpeningBoundary)))
        {
            if (_inBody) _onPartComplete?.Invoke();
            _inBody = true;
            _inPartHeaders = true;
            _headerFill = 0;
        }
    }

    private bool EndsWith(byte[] token)
    {
        if (_lookBehindFill < token.Length) return false;

        var offset = _lookBehindFill - token.Length;
        for (var i = 0; i < token.Length; i++)
        {
            if (_lookBehind[offset + i] != token[i]) return false;
        }

        return true;
    }

    private HeaderCollection ParsePartHeaders()
    {
        var headers = new HeaderCollection();
        var text = Encoding.ASCII.GetString(_headerBuffer, 0, _headerFill);
        var lines = text.Split('\n');
        var count = 0;
        foreach (var rawLine in lines)
        {
            if (count >= 100) break;
            var line = rawLine.TrimEnd('\r');
            if (line.Length == 0) break;
            var colon = line.IndexOf(':');
            if (colon <= 0) continue;
            var name = line.Substring(0, colon).Trim();
            var value = line.Substring(colon + 1).Trim();
            headers.AddHeader(new HttpHeader(name, value));
            count++;
        }

        return headers;
    }
}
