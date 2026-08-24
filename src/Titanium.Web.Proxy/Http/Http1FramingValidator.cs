using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using Titanium.Web.Proxy.Exceptions;
using Titanium.Web.Proxy.Extensions;

namespace Titanium.Web.Proxy.Http;

/// <summary>
///     Centralizes HTTP/1 framing validation - <c>Content-Length</c> and <c>Transfer-Encoding</c>
///     well-formedness, and the RFC 9112 §6.3 rule that a message must not carry both - so it runs once,
///     ahead of <see cref="RequestResponseBase.SetOriginalHeaders()" />, user callbacks, body reads,
///     forwarding, retries and connection pooling, instead of as scattered per-call-site checks that a
///     later normalization pass could run after the values callbacks/pooling logic already observed.
///     <para>
///         <see cref="Validate" /> takes a required <see cref="FramingSource" /> with no default value
///         and no parameterless overload, so a new call site cannot compile without an explicit choice.
///         Only the <c>Http1Wire*</c> sources run the rules below: <c>SynthesizedFromH2</c>/
///         <c>SynthesizedFromH3</c> messages are built from decoded pseudo-headers/frames, where length
///         framing is authoritative from the frame layer (not from these text headers) and where
///         <c>Transfer-Encoding</c> is forbidden outright except <c>trailers</c> (RFC 9113 §8.2.2) - a
///         different rule set owned by the H2/H3 layer itself, not by this type.
///     </para>
/// </summary>
internal static class Http1FramingValidator
{
    /// <summary>
    ///     Validates and (where recoverable) normalizes a wire-parsed HTTP/1 message's framing headers.
    ///     A no-op for synthesized HTTP/2/HTTP/3 sources - see the class remarks.
    /// </summary>
    /// <exception cref="Http1FramingException">
    ///     The message's <c>Content-Length</c>/<c>Transfer-Encoding</c> framing is ambiguous, or it
    ///     names a transfer coding this proxy does not implement. Only ever thrown for the
    ///     <c>Http1Wire*</c> sources, and only when <paramref name="allowAmbiguousFraming" /> is
    ///     <see langword="false" />.
    /// </exception>
    /// <param name="message">The message whose framing headers are being validated.</param>
    /// <param name="source">Which parser produced <paramref name="message" /> - see the class remarks.</param>
    /// <param name="allowAmbiguousFraming">
    ///     The single, explicitly named, off-by-default escape hatch from this validator's otherwise
    ///     unconditional enforcement, per the plan's rollout section: framing has no
    ///     <see cref="Options.PolicyMode" /> because there is no safe "detect but let it through"
    ///     action for an ambiguous message, so this is a distinct boolean, not a
    ///     <see cref="Options.PolicyFamily" /> member, and no <see cref="Options.ProxyProfile" /> ever
    ///     sets it - see <see cref="Options.ProxyPolicyModes.WithAllowAmbiguousFramingEnabled" />.
    ///     <see langword="true" /> makes this call a complete no-op for every
    ///     <see cref="FramingSource" />, relaying the message's <c>Content-Length</c>/
    ///     <c>Transfer-Encoding</c> headers exactly as received - including a genuinely ambiguous or
    ///     malformed combination - which is a request-smuggling primitive against whatever sits behind
    ///     this proxy. Exists only for security research that needs to observe how a client or origin
    ///     reacts to smuggling-shaped input relayed through the proxy.
    /// </param>
    internal static void Validate(RequestResponseBase message, FramingSource source,
        bool allowAmbiguousFraming = false)
    {
        if (allowAmbiguousFraming) return;

        switch (source)
        {
            case FramingSource.Http1Wire:
            case FramingSource.Http1WireTransparent:
            case FramingSource.Http1WireSocks:
                ValidateWireFraming(message.Headers);
                return;

            case FramingSource.SynthesizedFromH2:
            case FramingSource.SynthesizedFromH3:
                // Deliberately a no-op - see the class remarks. Kept as its own switch arm (rather than
                // folded into a `default`) so the enumerate-every-FramingSource structural guard test
                // fails loudly the moment a future enum member is added without anyone deciding which
                // side of this boundary it belongs on, instead of silently falling through to whichever
                // arm happens to be `default`.
                return;

            default:
                throw new ArgumentOutOfRangeException(nameof(source), source, "Unhandled FramingSource.");
        }
    }

    private static void ValidateWireFraming(HeaderCollection headers)
    {
        NormalizeContentLength(headers);
        ValidateAndNormalizeTransferEncoding(headers);

        // RFC 9112 §6.3: a message must not carry both fields. Once each has individually been
        // validated as well-formed above, remove Content-Length so only Transfer-Encoding continues to
        // drive framing/forwarding decisions - this closes the request-smuggling ambiguity while
        // staying interoperable with an origin that incorrectly sends both.
        if (headers.HeaderExists(KnownHeaders.TransferEncoding.String) &&
            headers.HeaderExists(KnownHeaders.ContentLength.String))
            headers.RemoveHeader(KnownHeaders.ContentLength);
    }

    /// <summary>
    ///     RFC 9112 §6.3: multiple <c>Content-Length</c> header lines, or the single-field list form
    ///     (<c>Content-Length: 42, 42</c>), are acceptable only when every value is identical, in which
    ///     case they are collapsed to one field carrying that value. Any other combination - conflicting
    ///     duplicate lines, a list containing differing values, or a value that is not a strict
    ///     <c>1*DIGIT</c> - is unrecoverable framing ambiguity: rejecting outright is the only safe
    ///     choice, because forwarding with one value used for framing while different bytes are actually
    ///     on the wire is exactly the request-smuggling primitive this check exists to close.
    /// </summary>
    private static void NormalizeContentLength(HeaderCollection headers) // NOSONAR S3776 -- This protocol/state-machine path shares mutable parsing or transport state; splitting it further would create disproportionate regression risk.
    {
        // Common path: one well-formed Content-Length. Do not allocate GetHeaders()'s List,
        // Split, or rewrite the header — that was a per-response tax on every tiny GET.
        if (headers.TryGetUniqueHeader(KnownHeaders.ContentLength, out var unique))
        {
            var raw = unique.Value;
            if (raw.IndexOf(',') < 0)
            {
                var token = raw.AsSpan().Trim();
                if (token.Length == raw.Length)
                {
                    if (!TryParseStrictDigits(raw, out _))
                        throw new Http1FramingException(
                            $"Ambiguous framing: Content-Length value '{raw}' is not a valid 1*DIGIT.",
                            HttpStatusCode.BadRequest);
                    return;
                }
            }
        }

        var entries = headers.GetHeaders(KnownHeaders.ContentLength.String);
        if (entries == null) return;

        long? normalized = null;

        foreach (var entry in entries)
        {
            foreach (var rawToken in entry.Value.Split(','))
            {
                var token = rawToken.Trim();

                if (!TryParseStrictDigits(token, out var value))
                    throw new Http1FramingException(
                        $"Ambiguous framing: Content-Length value '{token}' is not a valid 1*DIGIT.",
                        HttpStatusCode.BadRequest);

                if (normalized is { } existing && existing != value)
                    throw new Http1FramingException(
                        "Ambiguous framing: conflicting Content-Length values were received.",
                        HttpStatusCode.BadRequest);

                normalized = value;
            }
        }

        if (normalized is not { } finalValue) return;

        headers.RemoveHeader(KnownHeaders.ContentLength);
        headers.AddHeader(KnownHeaders.ContentLength, finalValue.ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>
    ///     Strict <c>1*DIGIT</c> per RFC 9112 §6.3. Deliberately stricter than the default
    ///     <see cref="long.TryParse(string, out long)" />/<see cref="NumberStyles.Integer" /> parse used
    ///     elsewhere in this codebase, which accepts a leading <c>+</c> and leading/trailing whitespace
    ///     that the grammar does not permit.
    /// </summary>
    private static bool TryParseStrictDigits(string token, out long value)
    {
        return long.TryParse(token, NumberStyles.None, CultureInfo.InvariantCulture, out value) && value >= 0;
    }

    /// <summary>
    ///     RFC 9112 §6.1: when present, <c>chunked</c> must be the last coding applied, so the recipient
    ///     can determine where the message ends without any further transformation first. Multiple
    ///     <c>Transfer-Encoding</c> header lines are combined into one ordered coding list before this
    ///     check. Any coding other than <c>chunked</c> is one this proxy does not itself decode, so per
    ///     RFC 9112 §6.1 ("a server that receives a request message with a transfer coding it does not
    ///     understand SHOULD respond with 501") it is rejected with 501 rather than treated as malformed.
    /// </summary>
    private static void ValidateAndNormalizeTransferEncoding(HeaderCollection headers)
    {
        var entries = headers.GetHeaders(KnownHeaders.TransferEncoding.String);
        if (entries == null) return;

        var codings = new List<string>();
        foreach (var entry in entries)
        {
            foreach (var rawToken in entry.Value.Split(','))
            {
                var token = rawToken.Trim();
                if (token.Length > 0) codings.Add(token);
            }
        }

        if (codings.Count == 0)
        {
            headers.RemoveHeader(KnownHeaders.TransferEncoding);
            return;
        }

        for (var i = 0; i < codings.Count; i++)
        {
            if (!codings[i].EqualsIgnoreCase(KnownHeaders.TransferEncodingChunked.String))
                throw new Http1FramingException(
                    $"Unsupported transfer coding '{codings[i]}'.", HttpStatusCode.NotImplemented);

            if (i != codings.Count - 1)
                throw new Http1FramingException(
                    "Ambiguous framing: 'chunked' must be the final transfer coding.",
                    HttpStatusCode.BadRequest);
        }

        // Normalize to a single canonical field: multiple identical "chunked" lines, or mixed casing,
        // collapse to exactly what every downstream consumer of IsChunked/OriginalIsChunked expects.
        headers.RemoveHeader(KnownHeaders.TransferEncoding);
        headers.AddHeader(KnownHeaders.TransferEncoding, KnownHeaders.TransferEncodingChunked);
    }
}
