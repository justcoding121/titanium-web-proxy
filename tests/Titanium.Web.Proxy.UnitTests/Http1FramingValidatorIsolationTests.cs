using System;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Exceptions;
using Titanium.Web.Proxy.Http;
using Titanium.Web.Proxy.Models;

namespace Titanium.Web.Proxy.UnitTests;

/// <summary>
///     Proves the <see cref="FramingSource" />/<see cref="Http1FramingValidator" /> boundary holds:
///     the HTTP/1 wire-framing rules (duplicate/list-form <c>Content-Length</c>, strict-digit parsing,
///     non-final <c>chunked</c>, unsupported transfer codings) run for every wire-parsed source and
///     never for a message synthesized from HTTP/2 or HTTP/3 frames.
/// </summary>
[TestClass]
public class Http1FramingValidatorIsolationTests
{
    private static readonly FramingSource[] WireSources =
    {
        FramingSource.Http1Wire, FramingSource.Http1WireTransparent, FramingSource.Http1WireSocks
    };

    private static readonly FramingSource[] SynthesizedSources =
    {
        FramingSource.SynthesizedFromH2, FramingSource.SynthesizedFromH3
    };

    private static Request MakeRequest()
    {
        return new Request { Method = "GET", HttpVersion = HttpHeader.Version11 };
    }

    // ---- Positive cases: the wire validator runs and enforces every rule, for every wire source ----

    [TestMethod]
    public void WireSources_RejectConflictingDuplicateContentLength()
    {
        foreach (var source in WireSources)
        {
            var request = MakeRequest();
            request.Headers.AddHeader("Content-Length", "42");
            request.Headers.AddHeader("Content-Length", "43");

            Assert.ThrowsExactly<Http1FramingException>(() => Http1FramingValidator.Validate(request, source),
                $"source={source}");
        }
    }

    [TestMethod]
    public void WireSources_AcceptIdenticalDuplicateContentLength_AndNormalizeToOneHeader()
    {
        foreach (var source in WireSources)
        {
            var request = MakeRequest();
            request.Headers.AddHeader("Content-Length", "42");
            request.Headers.AddHeader("Content-Length", "42");

            Http1FramingValidator.Validate(request, source);

            Assert.AreEqual(42, request.ContentLength, $"source={source}");
            Assert.AreEqual(1, request.Headers.GetHeaders("Content-Length")!.Count, $"source={source}");
        }
    }

    [TestMethod]
    public void WireSources_RejectListFormContentLengthWithDifferingValues()
    {
        foreach (var source in WireSources)
        {
            var request = MakeRequest();
            request.Headers.AddHeader("Content-Length", "42, 43");

            Assert.ThrowsExactly<Http1FramingException>(() => Http1FramingValidator.Validate(request, source),
                $"source={source}");
        }
    }

    [TestMethod]
    public void WireSources_AcceptListFormContentLengthWithIdenticalValues()
    {
        foreach (var source in WireSources)
        {
            var request = MakeRequest();
            request.Headers.AddHeader("Content-Length", "42, 42");

            Http1FramingValidator.Validate(request, source);

            Assert.AreEqual(42, request.ContentLength, $"source={source}");
        }
    }

    [TestMethod]
    public void WireSources_RejectNonFinalChunkedCoding()
    {
        foreach (var source in WireSources)
        {
            var request = MakeRequest();
            request.Headers.AddHeader("Transfer-Encoding", "chunked, gzip");

            Assert.ThrowsExactly<Http1FramingException>(() => Http1FramingValidator.Validate(request, source),
                $"source={source}");
        }
    }

    [TestMethod]
    public void WireSources_RejectUnsupportedTransferCoding_With501()
    {
        foreach (var source in WireSources)
        {
            var request = MakeRequest();
            request.Headers.AddHeader("Transfer-Encoding", "gzip");

            var ex = Assert.ThrowsExactly<Http1FramingException>(
                () => Http1FramingValidator.Validate(request, source), $"source={source}");
            Assert.AreEqual(System.Net.HttpStatusCode.NotImplemented, ex.StatusCode, $"source={source}");
        }
    }

    [TestMethod]
    public void WireSources_RejectNegativeStrictDigitContentLength()
    {
        // "-1" is not a valid 1*DIGIT token; the default NumberStyles.Integer parse used elsewhere in
        // this codebase would accept a leading '-' or '+', which RFC 9112 §6.3 does not permit.
        foreach (var source in WireSources)
        {
            var request = MakeRequest();
            request.Headers.AddHeader("Content-Length", "-1");

            Assert.ThrowsExactly<Http1FramingException>(() => Http1FramingValidator.Validate(request, source),
                $"source={source}");
        }
    }

    [TestMethod]
    public void WireSources_RejectLeadingPlusContentLength()
    {
        foreach (var source in WireSources)
        {
            var request = MakeRequest();
            request.Headers.AddHeader("Content-Length", "+42");

            Assert.ThrowsExactly<Http1FramingException>(() => Http1FramingValidator.Validate(request, source),
                $"source={source}");
        }
    }

    [TestMethod]
    public void WireSources_StripContentLength_WhenTransferEncodingAlsoPresent()
    {
        // RFC 9112 §6.3: once both fields are individually well-formed, Content-Length must be
        // removed so only Transfer-Encoding continues to drive framing.
        foreach (var source in WireSources)
        {
            var request = MakeRequest();
            request.Headers.AddHeader("Content-Length", "42");
            request.Headers.AddHeader("Transfer-Encoding", "chunked");

            Http1FramingValidator.Validate(request, source);

            Assert.IsFalse(request.Headers.HeaderExists("Content-Length"), $"source={source}");
            Assert.IsTrue(request.IsChunked, $"source={source}");
        }
    }

    [TestMethod]
    public void OriginResponsePath_AppliesTheSameRules()
    {
        // "The origin response path through ResponseHandler.cs applies the same rules" - exercised
        // directly against a Response instance rather than the network handler.
        var response = new Response { StatusCode = 200, HttpVersion = HttpHeader.Version11 };
        response.Headers.AddHeader("Content-Length", "1");
        response.Headers.AddHeader("Content-Length", "2");

        Assert.ThrowsExactly<Http1FramingException>(
            () => Http1FramingValidator.Validate(response, FramingSource.Http1Wire));
    }

    // ---- Bypass cases: synthesized sources never run the wire validator and never mutate headers ----

    [TestMethod]
    public void SynthesizedSources_DoNotRejectConflictingDuplicateContentLength()
    {
        foreach (var source in SynthesizedSources)
        {
            var request = MakeRequest();
            request.Headers.AddHeader("Content-Length", "42");
            request.Headers.AddHeader("Content-Length", "43");

            Http1FramingValidator.Validate(request, source); // must not throw

            Assert.AreEqual(2, request.Headers.GetHeaders("Content-Length")!.Count, $"source={source}");
        }
    }

    [TestMethod]
    public void SynthesizedSources_DoNotMutateAnyHeader()
    {
        foreach (var source in SynthesizedSources)
        {
            var request = MakeRequest();
            request.Headers.AddHeader("Content-Length", "42, 43");
            request.Headers.AddHeader("Transfer-Encoding", "chunked, gzip");
            request.Headers.AddHeader("te", "trailers");

            var before = request.Headers.GetAllHeaders().Select(h => (h.Name, h.Value)).ToList();

            Http1FramingValidator.Validate(request, source);

            var after = request.Headers.GetAllHeaders().Select(h => (h.Name, h.Value)).ToList();
            CollectionAssert.AreEquivalent(before, after, $"source={source}");
        }
    }

    [TestMethod]
    public void SynthesizedFromH2_H1ToH2RequestConstruction_NeverRunsTheWireValidator()
    {
        // "H1-to-H2 request construction in Http11ToHttp2BridgeHandler.cs" bypass case: a message
        // this bridge is about to translate into h2 pseudo-headers must not be rejected by rules that
        // only make sense on the wire.
        var request = MakeRequest();
        request.Headers.AddHeader("Content-Length", "1, 2"); // would be rejected on any wire source

        Http1FramingValidator.Validate(request, FramingSource.SynthesizedFromH2);
    }

    [TestMethod]
    public void SynthesizedFromH2_H2ToH1RequestConstruction_NeverRunsTheWireValidator()
    {
        // "H2-to-H1 request construction in Http2ToHttp11BridgeHandler.cs" bypass case.
        var request = MakeRequest();
        request.Headers.AddHeader("Transfer-Encoding", "gzip"); // would be 501 on any wire source

        Http1FramingValidator.Validate(request, FramingSource.SynthesizedFromH2);
    }

    [TestMethod]
    public void SynthesizedFromH3_H2ToH3ResponseConstruction_NeverRunsTheWireValidator()
    {
        // "H2-to-H3 response construction in Http2ToHttp3BridgeHandler.cs" bypass case.
        var response = new Response { StatusCode = 200, HttpVersion = HttpHeader.Version11 };
        response.Headers.AddHeader("Content-Length", "1");
        response.Headers.AddHeader("Content-Length", "2");

        Http1FramingValidator.Validate(response, FramingSource.SynthesizedFromH3);
    }

    // ---- Protocol-pollution cases: the specific regressions this boundary exists to prevent ----

    [TestMethod]
    public void TeTrailers_SurvivesSynthesizedSourcesUnmodified()
    {
        // "te: trailers" is legal under RFC 9113 §8.2.2 for h2/h3 and must never be seen by the H1
        // rule that rejects unsupported transfer codings with 501 - that rule only inspects
        // "Transfer-Encoding", a different header, but this proves the guarantee end-to-end.
        foreach (var source in SynthesizedSources)
        {
            var request = MakeRequest();
            request.Headers.AddHeader("te", "trailers");

            Http1FramingValidator.Validate(request, source);

            Assert.AreEqual("trailers", request.Headers.GetFirstHeader("te")!.Value, $"source={source}");
        }
    }

    [TestMethod]
    public void SynthesizedMessageWithNoLengthHeaders_IsNotRewrittenToChunked_AndNotRejected()
    {
        foreach (var source in SynthesizedSources)
        {
            var request = MakeRequest();

            Http1FramingValidator.Validate(request, source); // must not throw

            Assert.IsFalse(request.Headers.HeaderExists("Transfer-Encoding"), $"source={source}");
            Assert.IsFalse(request.Headers.HeaderExists("Content-Length"), $"source={source}");
        }
    }

    [TestMethod]
    public void SynthesizedMessage_ContentLengthIsNotStripped_WhenTransferEncodingAlsoPresent()
    {
        // The H1 rule that strips Content-Length when Transfer-Encoding is present must not fire on
        // synthesized messages, where H2/H3 length semantics (not these text headers) are
        // authoritative.
        foreach (var source in SynthesizedSources)
        {
            var request = MakeRequest();
            request.Headers.AddHeader("Content-Length", "42");
            request.Headers.AddHeader("Transfer-Encoding", "chunked");

            Http1FramingValidator.Validate(request, source);

            Assert.IsTrue(request.Headers.HeaderExists("Content-Length"), $"source={source}");
        }
    }

    // ---- Structural guard: every FramingSource maps to exactly one validator/no-op ----

    [TestMethod]
    public void EveryFramingSourceValue_IsHandledExplicitly()
    {
        // Adding a new FramingSource member without deciding which side of the wire/synthesized
        // boundary it belongs on must fail this test, converting a future silent bypass into a build
        // failure rather than an unnoticed gap.
        var allValues = Enum.GetValues<FramingSource>().ToList();

        CollectionAssert.AreEquivalent(WireSources.Concat(SynthesizedSources).ToList(), allValues,
            "A new FramingSource member was added without updating this test's Wire/Synthesized " +
            "classification (and, most likely, without updating Http1FramingValidator.Validate).");

        foreach (var source in allValues)
        {
            // Must not throw ArgumentOutOfRangeException for any declared enum member - only an
            // unmapped one would fall through to Validate's `default` arm.
            var request = MakeRequest();
            Http1FramingValidator.Validate(request, source);
        }
    }
}
