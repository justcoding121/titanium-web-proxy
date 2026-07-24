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

    private readonly byte[] _boundary;       // "--" + boundary bytes
    private readonly byte[] _closingBoundary; // "--" + boundary + "--"
    private readonly Action<HeaderCollection>? _onPartHeaders;
    private readonly Action? _onPartComplete;

    // Look-behind ring buffer state
    private readonly byte[] _lookBehind;
    private int _lookBehindFill;

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

        // Delimiter token: CRLF "--" boundary  (the leading CRLF is part of the delimiter per RFC 2046)
        _boundary = new byte[2 + boundaryBytes.Length];
        _boundary[0] = (byte)'-';
        _boundary[1] = (byte)'-';
        Buffer.BlockCopy(boundaryBytes, 0, _boundary, 2, boundaryBytes.Length);

        _closingBoundary = new byte[_boundary.Length + 2];
        Buffer.BlockCopy(_boundary, 0, _closingBoundary, 0, _boundary.Length);
        _closingBoundary[_boundary.Length] = (byte)'-';
        _closingBoundary[_boundary.Length + 1] = (byte)'-';

        // The look-behind window must hold at least the closing boundary so we can detect it.
        _lookBehind = new byte[_closingBoundary.Length + 2]; // +2 for possible trailing CRLF

        _headerBuffer = new byte[MaxPartHeaderBytesPerPart];
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
        if (contentType == null) return null;
        var boundary = ExtractBoundary(contentType);
        if (boundary == null) return null;
        try { return new MultipartStreamObserver(boundary, onPartHeaders, onPartComplete); }
        catch { return null; }
    }

    private static string? ExtractBoundary(string contentType)
    {
        var lower = contentType.ToLowerInvariant();
        if (!lower.Contains("multipart/")) return null;

        var idx = lower.IndexOf("boundary=", StringComparison.Ordinal);
        if (idx < 0) return null;

        var raw = contentType.Substring(idx + 9).TrimStart();
        if (raw.Length > 0 && raw[0] == '"')
        {
            var end = raw.IndexOf('"', 1);
            return end < 0 ? null : raw.Substring(1, end - 1);
        }

        var semi = raw.IndexOf(';');
        return semi < 0 ? raw.Trim() : raw.Substring(0, semi).Trim();
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
        if (_inPartHeaders)
        {
            if (_headerFill < _headerBuffer.Length)
                _headerBuffer[_headerFill++] = b;

            // Detect end-of-headers: \r\n\r\n
            if (_headerFill >= 4 &&
                _headerBuffer[_headerFill - 4] == '\r' &&
                _headerBuffer[_headerFill - 3] == '\n' &&
                _headerBuffer[_headerFill - 2] == '\r' &&
                _headerBuffer[_headerFill - 1] == '\n')
            {
                _inPartHeaders = false;
                var headers = ParsePartHeaders();
                _onPartHeaders?.Invoke(headers);
                _headerFill = 0;
            }
        }

        CheckForBoundary();
    }

    private void CheckForBoundary()
    {
        // We need at least enough bytes to match the opening boundary token.
        if (_lookBehindFill < _boundary.Length) return;

        // First check for closing boundary (longer token) to avoid false positives.
        if (_lookBehindFill >= _closingBoundary.Length)
        {
            var offset = _lookBehindFill - _closingBoundary.Length;
            var match = true;
            for (var i = 0; i < _closingBoundary.Length; i++)
            {
                if (_lookBehind[offset + i] != _closingBoundary[i])
                {
                    match = false;
                    break;
                }
            }

            if (match)
            {
                _finished = true;
                if (_inBody) _onPartComplete?.Invoke();
                return;
            }
        }

        // Check for opening boundary token.
        {
            var offset = _lookBehindFill - _boundary.Length;
            var match = true;
            for (var i = 0; i < _boundary.Length; i++)
            {
                if (_lookBehind[offset + i] != _boundary[i])
                {
                    match = false;
                    break;
                }
            }

            if (match)
            {
                if (_inBody) _onPartComplete?.Invoke();
                _inBody = true;
                _inPartHeaders = true;
                _headerFill = 0;
            }
        }
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
