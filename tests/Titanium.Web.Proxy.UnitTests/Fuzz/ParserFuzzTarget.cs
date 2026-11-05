using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Titanium.Web.Proxy.EventArguments;
using Titanium.Web.Proxy.Exceptions;
using Titanium.Web.Proxy.Helpers;
using Titanium.Web.Proxy.Http;
using Titanium.Web.Proxy.Models;
using Titanium.Web.Proxy.Network.Ssl;
using Titanium.Web.Proxy.Network.Tcp;
using Titanium.Web.Proxy.Options;
using Titanium.Web.Proxy.StreamExtended.BufferPool;

namespace Titanium.Web.Proxy.UnitTests.Fuzz;

/// <summary>What one run of the HTTP/1 request parser pipeline produced.</summary>
internal sealed class Http1ParseOutcome
{
    /// <summary>The pipeline refused the input with one of the documented rejection exceptions.</summary>
    public bool Rejected { get; init; }

    /// <summary>Simple type name of the rejection (or of the unexpected exception).</summary>
    public string? ExceptionType { get; init; }

    /// <summary>The request as it would be handed to the forwarding path; null when rejected before headers finished.</summary>
    public Request? Request { get; init; }

    /// <summary>The body was relayed in full (declared length reached, or the zero chunk and trailer were seen).</summary>
    public bool BodyComplete { get; init; }

    /// <summary>
    ///     Everything that is wrong with an accepted request, or with an input that crashed or hung the parser.
    ///     Empty means the run honoured every safety invariant.
    /// </summary>
    public IReadOnlyList<string> Violations { get; init; } = [];
}

/// <summary>
///     Deterministic fuzz target for the HTTP/1 request path and the TLS ClientHello SNI parser. It runs the
///     same components the proxy runs on a client's first bytes - <c>HttpStream</c> line reader,
///     <c>Request.ParseRequestLine</c>, <c>HeaderParser</c> under the default header budget,
///     <c>Http1FramingValidator</c> and the chunked/length body relay - and then checks the invariants that
///     keep the proxy from forwarding an ambiguous request.
///     <para>
///         <see cref="Http1Request(byte[])" /> and <see cref="ClientHello(byte[])" /> are the entry points an external
///         fuzzer (for example a SharpFuzz harness) wraps: they swallow the documented rejections and throw
///         <see cref="FuzzInvariantException" /> for anything else. See <c>tests/fuzz/README.md</c>.
///     </para>
/// </summary>
internal static class ParserFuzzTarget
{
    private static readonly TimeSpan CaseDeadline = TimeSpan.FromSeconds(10);

    private static readonly Lazy<ProxyServer> SharedProxy = new(() => new ProxyServer(false, false, false));

    /// <summary>SharpFuzz-style entry point. Throws <see cref="FuzzInvariantException" /> on a violation.</summary>
    public static void Http1Request(byte[] data)
    {
        var outcome = ParseHttp1Request(data);
        if (outcome.Violations.Count > 0)
            throw new FuzzInvariantException(string.Join("; ", outcome.Violations));
    }

    /// <summary>SharpFuzz-style entry point. Throws <see cref="FuzzInvariantException" /> on a violation.</summary>
    public static void ClientHello(byte[] data)
    {
        var violations = CheckClientHello(data);
        if (violations.Count > 0)
            throw new FuzzInvariantException(string.Join("; ", violations));
    }

    public static Http1ParseOutcome ParseHttp1Request(byte[] wire)
    {
        // The pipeline only ever awaits an in-memory stream, so it completes synchronously; the deadline
        // turns a parser that spins into a reported violation instead of a hung test run.
        using var deadline = new CancellationTokenSource(CaseDeadline);
        var task = Task.Run(() => ParseHttp1RequestAsync(wire, deadline.Token));
        if (!task.Wait(CaseDeadline + TimeSpan.FromSeconds(5)))
        {
            return new Http1ParseOutcome
            {
                ExceptionType = "Hang",
                Violations = [$"parser did not finish within {CaseDeadline.TotalSeconds:0}s"]
            };
        }

        return task.Result;
    }

    private static async Task<Http1ParseOutcome> ParseHttp1RequestAsync(byte[] wire, CancellationToken token)
    {
        var proxy = SharedProxy.Value;
        var limits = proxy.ResourceLimits;
        using var reader = new HttpStream(proxy, new MemoryStream(wire), new DefaultBufferPool(), token);
        Request? request = null;

        try
        {
            var requestLine = await reader.ReadLineAsync(token);
            if (requestLine == null) throw new EndOfStreamException("no request line");

            Request.ParseRequestLine(requestLine, out var method, out var requestUri, out var version);
            request = new Request { Method = method, RequestUriString8 = requestUri, HttpVersion = version };

            var budget = new HeaderBlockBudget(limits.MaxHeaderCount, limits.MaxHeaderAggregateBytes,
                PolicyMode.Enforce, isRequest: true);
            if (!await HeaderParser.TryReadHeadersAsync(reader, request.Headers, token, budget))
                throw new OperationCanceledException(token);

            Http1FramingValidator.Validate(request, FramingSource.Http1Wire);
        }
        catch (Exception ex) when (IsDocumentedRejection(ex))
        {
            return new Http1ParseOutcome { Rejected = true, ExceptionType = ex.GetType().Name, Request = request };
        }
        catch (Exception ex)
        {
            return Crash(ex, request);
        }

        var violations = new List<string>();
        CheckFramingInvariants(request, violations);

        bool bodyComplete;
        try
        {
            bodyComplete = await RelayBodyAsync(proxy, reader, request, token);
        }
        catch (Exception ex) when (IsDocumentedRejection(ex))
        {
            return new Http1ParseOutcome
            {
                Rejected = true,
                ExceptionType = ex.GetType().Name,
                Request = request,
                Violations = violations
            };
        }
        catch (Exception ex)
        {
            return Crash(ex, request);
        }

        return new Http1ParseOutcome { Request = request, BodyComplete = bodyComplete, Violations = violations };
    }

    private static Http1ParseOutcome Crash(Exception ex, Request? request)
    {
        return new Http1ParseOutcome
        {
            ExceptionType = ex.GetType().Name,
            Request = request,
            Violations = [$"unexpected {ex.GetType().Name}: {ex.Message}"]
        };
    }

    private static bool IsDocumentedRejection(Exception ex)
    {
        // IOException: the declared body length was not reached before the connection ended.
        return ex is FormatException or Http1FramingException or ProxyHttpException
            or HeaderLimitsExceededException or EndOfStreamException or IOException;
    }

    private static async Task<bool> RelayBodyAsync(ProxyServer proxy, HttpStream reader, Request request,
        CancellationToken token)
    {
        var chunked = request.IsChunked;
        var length = request.ContentLength;
        if (!chunked && length <= 0) return true;

        var forwarded = new MemoryStream();
        using var writer = new HttpStream(proxy, forwarded, new DefaultBufferPool(), token, leaveOpen: true);
        using var session = NewSession(proxy);

        await reader.CopyBodyAsync(writer, chunked, chunked ? -1 : length, isRequest: true, session, token);
        await writer.FlushAsync(token);

        if (!chunked) return forwarded.Length == length;

        // A complete chunked relay ends with the zero-size chunk, optional trailers and the blank line.
        var text = Encoding.Latin1.GetString(forwarded.ToArray());
        return text.EndsWith("\r\n\r\n", StringComparison.Ordinal) &&
               (text.StartsWith("0\r\n", StringComparison.Ordinal) ||
                text.Contains("\r\n0\r\n", StringComparison.Ordinal));
    }

    private static SessionEventArgs NewSession(ProxyServer proxy)
    {
        var endPoint = new TransparentProxyEndPoint(IPAddress.Loopback, 0, false);
        var connection = new QuicClientConnection(proxy, new IPEndPoint(IPAddress.Loopback, 4433),
            new IPEndPoint(IPAddress.Loopback, 12345));
        var cts = new CancellationTokenSource();
        var stream = new HttpClientStream(proxy, connection, Stream.Null, proxy.BufferPool, cts.Token);
        return new SessionEventArgs(proxy, endPoint, stream, null, cts);
    }

    /// <summary>
    ///     Invariants of an accepted request: whatever the parser let through must have exactly one
    ///     unambiguous framing, and no header may carry bytes that a different peer could read as a line break.
    /// </summary>
    internal static void CheckFramingInvariants(Request request, List<string> violations)
    {
        var headers = request.Headers;

        var contentLengths = headers.GetHeaders("Content-Length");
        var transferEncodings = headers.GetHeaders("Transfer-Encoding");

        if (contentLengths is { Count: > 1 })
            violations.Add($"{contentLengths.Count} Content-Length fields survive normalization");

        if (contentLengths is { Count: > 0 } && transferEncodings is { Count: > 0 })
            violations.Add("both Content-Length and Transfer-Encoding survive normalization");

        if (transferEncodings is { Count: > 0 })
        {
            if (transferEncodings.Count > 1)
                violations.Add($"{transferEncodings.Count} Transfer-Encoding fields survive normalization");
            else if (!string.Equals(transferEncodings[0].Value, "chunked", StringComparison.Ordinal))
                violations.Add($"Transfer-Encoding is '{transferEncodings[0].Value}', expected the canonical 'chunked'");
        }

        foreach (var header in headers)
        {
            var name = header.Name;
            var value = header.Value;

            if (name.Length == 0) violations.Add("empty header name accepted");

            foreach (var c in name)
            {
                if (c is ' ' or '\t' or '\r' or '\n' or '\0' or ':')
                    violations.Add($"header name '{Escape(name)}' contains a delimiter byte");
            }

            foreach (var c in value)
            {
                if (c is '\r' or '\n' or '\0')
                    violations.Add($"value of '{name}' contains a CR, LF or NUL byte: '{Escape(value)}'");
            }
        }
    }

    /// <summary>Checks the SNI parser: never throws, only reports a name that is real, bounded printable ASCII.</summary>
    internal static List<string> CheckClientHello(byte[] data)
    {
        var violations = new List<string>();
        try
        {
            var found = ClientHelloSniParser.TryGetServerName(data, out var name, out var result);

            if (found != (result == ClientHelloSniParser.ParseResult.Found))
                violations.Add($"return value {found} disagrees with result {result}");

            if (found != (name != null))
                violations.Add("a name is returned if and only if the result is Found");

            if (name != null)
            {
                if (name.Length is 0 or > 255)
                    violations.Add($"name length {name.Length} is outside 1..255");
                foreach (var c in name)
                {
                    if (c is < '!' or > '~')
                    {
                        violations.Add($"name contains a non-printable or non-ASCII character: '{Escape(name)}'");
                        break;
                    }
                }
            }

            // Parsing is a pure function of the bytes.
            var again = ClientHelloSniParser.TryGetServerName(data, out var nameAgain, out var resultAgain);
            if (again != found || resultAgain != result || nameAgain != name)
                violations.Add("a second parse of the same bytes gave a different answer");
        }
        catch (Exception ex)
        {
            violations.Add($"unexpected {ex.GetType().Name}: {ex.Message}");
        }

        return violations;
    }

    internal static string Escape(string text)
    {
        var sb = new StringBuilder();
        foreach (var c in text)
        {
            sb.Append(c switch
            {
                '\r' => "\\r",
                '\n' => "\\n",
                '\t' => "\\t",
                '\0' => "\\0",
                < ' ' or > '~' => $"\\x{(int)c:x2}",
                _ => c.ToString()
            });
        }

        return sb.ToString();
    }
}

/// <summary>An accepted input broke a parser-safety invariant, or the parser crashed or hung.</summary>
internal sealed class FuzzInvariantException(string message) : Exception(message);
