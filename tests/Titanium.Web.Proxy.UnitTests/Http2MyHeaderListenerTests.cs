using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Http;
using Titanium.Web.Proxy.Http2;
using Titanium.Web.Proxy.Models;

namespace Titanium.Web.Proxy.UnitTests;

/// <summary>
///     Unit tests for <see cref="Http2Helper.MyHeaderListener" /> — the HPACK decode listener
///     that enforces RFC 7540 §8.1.2 / RFC 9113 §8.2.1 header-block semantics.
///     <para>
///         These tests cover the validation paths in <c>Http2Helper.Copy.Headers.cs</c> (the
///         <c>ProcessCompleteHeaderBlockAsync</c> pipeline) without spinning up a full proxy.
///     </para>
/// </summary>
[TestClass]
public class Http2MyHeaderListenerTests
{
    // -------------------------------------------------------------------------
    // Helper: create and populate a listener via its AddHeader API.
    // -------------------------------------------------------------------------

    private static Http2Helper.MyHeaderListener MakeRequestListener()
    {
        var target = new HeaderCollection();
        return new Http2Helper.MyHeaderListener(target, isRequest: true);
    }

    private static Http2Helper.MyHeaderListener MakeResponseListener()
    {
        var target = new HeaderCollection();
        return new Http2Helper.MyHeaderListener(target, isRequest: false);
    }

    private static ByteString B(string s) => new ByteString(Encoding.ASCII.GetBytes(s));

    private static void Add(Http2Helper.MyHeaderListener listener, string name, string value = "v")
        => listener.AddHeader(B(name), B(value), sensitive: false);

    // =========================================================================
    // Well-formed request blocks
    // =========================================================================

    [TestMethod]
    public void RequestBlock_AllRequiredPseudos_IsWellFormed()
    {
        var l = MakeRequestListener();
        Add(l, ":method", "GET");
        Add(l, ":scheme", "https");
        Add(l, ":authority", "example.com");
        Add(l, ":path", "/");
        Add(l, "x-custom", "value");

        Assert.IsFalse(l.HasMalformedHeader, l.MalformedReason);
        Assert.AreEqual("GET", l.Method.ToString());
        Assert.AreEqual("/", l.Path.ToString());
    }

    [TestMethod]
    public void ResponseBlock_StatusOnly_IsWellFormed()
    {
        var l = MakeResponseListener();
        Add(l, ":status", "200");

        Assert.IsFalse(l.HasMalformedHeader, l.MalformedReason);
        Assert.AreEqual("200", l.Status.ToString());
    }

    // =========================================================================
    // Uppercase header name — RFC 9113 §8.2.1
    // =========================================================================

    [TestMethod]
    public void RequestBlock_UppercaseHeaderName_SetsHasMalformed()
    {
        var l = MakeRequestListener();
        Add(l, ":method", "GET");
        Add(l, ":scheme", "https");
        Add(l, ":authority", "host");
        Add(l, ":path", "/");
        Add(l, "X-Upper", "v"); // uppercase — forbidden

        Assert.IsTrue(l.HasMalformedHeader, "Uppercase header name must trigger HasMalformedHeader.");
        StringAssert.Contains(l.MalformedReason!, "uppercase",
            "MalformedReason must mention uppercase characters.");
    }

    // =========================================================================
    // CR/LF/NUL in header values — RFC 9113 §8.2.1
    // =========================================================================

    [TestMethod]
    public void RequestBlock_CrInHeaderValue_SetsHasMalformed()
    {
        var l = MakeRequestListener();
        Add(l, ":method", "GET");
        Add(l, ":scheme", "https");
        Add(l, ":authority", "host");
        Add(l, ":path", "/");
        Add(l, "x-inject", "value\rheader-inject"); // CR

        Assert.IsTrue(l.HasMalformedHeader, "CR in header value must trigger HasMalformedHeader.");
    }

    [TestMethod]
    public void RequestBlock_LfInHeaderValue_SetsHasMalformed()
    {
        var l = MakeRequestListener();
        Add(l, ":method", "GET");
        Add(l, ":scheme", "https");
        Add(l, ":authority", "host");
        Add(l, ":path", "/");
        Add(l, "x-inject", "value\nmore"); // LF

        Assert.IsTrue(l.HasMalformedHeader);
    }

    [TestMethod]
    public void RequestBlock_NulInHeaderName_SetsHasMalformed()
    {
        var l = MakeRequestListener();
        Add(l, ":method", "GET");
        Add(l, ":scheme", "https");
        Add(l, ":authority", "host");
        Add(l, ":path", "/");
        // NUL byte in name via raw bytes
        var nameWithNul = new ByteString(new byte[] { (byte)'x', 0, (byte)'y' });
        l.AddHeader(nameWithNul, B("v"), sensitive: false);

        Assert.IsTrue(l.HasMalformedHeader, "NUL in header name must trigger HasMalformedHeader.");
    }

    // =========================================================================
    // Duplicate pseudo-headers — RFC 7540 §8.1.2.1
    // =========================================================================

    [TestMethod]
    public void RequestBlock_DuplicateMethod_SetsHasMalformed()
    {
        var l = MakeRequestListener();
        Add(l, ":method", "GET");
        Add(l, ":method", "POST"); // duplicate

        Assert.IsTrue(l.HasMalformedHeader);
        StringAssert.Contains(l.MalformedReason!, "duplicate");
    }

    [TestMethod]
    public void RequestBlock_DuplicatePath_SetsHasMalformed()
    {
        var l = MakeRequestListener();
        Add(l, ":method", "GET");
        Add(l, ":scheme", "https");
        Add(l, ":authority", "host");
        Add(l, ":path", "/a");
        Add(l, ":path", "/b"); // duplicate

        Assert.IsTrue(l.HasMalformedHeader);
        StringAssert.Contains(l.MalformedReason!, "duplicate");
    }

    [TestMethod]
    public void ResponseBlock_DuplicateStatus_SetsHasMalformed()
    {
        var l = MakeResponseListener();
        Add(l, ":status", "200");
        Add(l, ":status", "404"); // duplicate

        Assert.IsTrue(l.HasMalformedHeader);
        StringAssert.Contains(l.MalformedReason!, "duplicate");
    }

    // =========================================================================
    // Pseudo-header after regular header — RFC 7540 §8.1.2.1
    // =========================================================================

    [TestMethod]
    public void RequestBlock_PseudoAfterRegularHeader_SetsHasMalformed()
    {
        var l = MakeRequestListener();
        Add(l, ":method", "GET");
        Add(l, ":scheme", "https");
        Add(l, "x-regular", "value"); // regular header first
        Add(l, ":authority", "host"); // pseudo after regular — forbidden

        Assert.IsTrue(l.HasMalformedHeader,
            "Pseudo-header after a regular header must set HasMalformedHeader.");
        StringAssert.Contains(l.MalformedReason!, "after");
    }

    // =========================================================================
    // Wrong-direction pseudo-headers
    // =========================================================================

    [TestMethod]
    public void RequestBlock_StatusPseudo_SetsHasMalformed()
    {
        // :status is a response pseudo-header; in a request block it is malformed.
        var l = MakeRequestListener();
        Add(l, ":method", "GET");
        Add(l, ":status", "200"); // wrong direction

        Assert.IsTrue(l.HasMalformedHeader);
        StringAssert.Contains(l.MalformedReason!, ":status");
    }

    [TestMethod]
    public void ResponseBlock_MethodPseudo_SetsHasMalformed()
    {
        // :method is a request pseudo-header; in a response block it is malformed.
        var l = MakeResponseListener();
        Add(l, ":status", "200");
        Add(l, ":method", "GET"); // wrong direction

        Assert.IsTrue(l.HasMalformedHeader);
        StringAssert.Contains(l.MalformedReason!, ":method");
    }

    [TestMethod]
    public void ResponseBlock_PathPseudo_SetsHasMalformed()
    {
        var l = MakeResponseListener();
        Add(l, ":status", "200");
        Add(l, ":path", "/injected"); // wrong direction

        Assert.IsTrue(l.HasMalformedHeader);
        StringAssert.Contains(l.MalformedReason!, ":path");
    }

    // =========================================================================
    // Unknown pseudo-headers — RFC 7540 §8.1.2.1
    // =========================================================================

    [TestMethod]
    public void RequestBlock_UnknownPseudo_SetsHasMalformed()
    {
        var l = MakeRequestListener();
        Add(l, ":method", "GET");
        Add(l, ":scheme", "https");
        Add(l, ":unknown-ext", "v"); // unknown pseudo

        Assert.IsTrue(l.HasMalformedHeader);
        StringAssert.Contains(l.MalformedReason!, "unknown pseudo-header");
    }

    // =========================================================================
    // ResetForDecode clears state for reuse
    // =========================================================================

    [TestMethod]
    public void ResetForDecode_ClearsMalformedStateForNextBlock()
    {
        // Build a malformed block first.
        var target = new HeaderCollection();
        var l = new Http2Helper.MyHeaderListener(target, isRequest: true);
        Add(l, ":method", "GET");
        Add(l, ":method", "POST"); // duplicate → malformed

        Assert.IsTrue(l.HasMalformedHeader, "First block must be malformed.");

        // Reset for the next block.
        var target2 = new HeaderCollection();
        l.ResetForDecode(target2);

        Assert.IsFalse(l.HasMalformedHeader,
            "ResetForDecode must clear HasMalformedHeader for the next block.");
        Assert.IsNull(l.MalformedReason,
            "ResetForDecode must clear MalformedReason.");

        // A well-formed block after reset must succeed.
        Add(l, ":method", "GET");
        Add(l, ":scheme", "https");
        Add(l, ":authority", "host");
        Add(l, ":path", "/");
        Assert.IsFalse(l.HasMalformedHeader, "Well-formed block after reset must pass.");
    }

    // =========================================================================
    // :protocol pseudo-header (RFC 8441 extended CONNECT)
    // =========================================================================

    [TestMethod]
    public void RequestBlock_Protocol_IsRecordedForConnect()
    {
        var l = MakeRequestListener();
        Add(l, ":method", "CONNECT");
        Add(l, ":scheme", "https");
        Add(l, ":authority", "host");
        Add(l, ":path", "/");
        Add(l, ":protocol", "websocket");

        Assert.IsFalse(l.HasMalformedHeader, l.MalformedReason);
        Assert.AreEqual("websocket", l.Protocol.ToString());
    }

    [TestMethod]
    public void ResponseBlock_Protocol_SetsHasMalformed()
    {
        var l = MakeResponseListener();
        Add(l, ":status", "200");
        Add(l, ":protocol", "websocket"); // only valid in requests

        Assert.IsTrue(l.HasMalformedHeader,
            ":protocol in a response block must be malformed.");
    }

    [TestMethod]
    public void RequestBlock_DuplicateProtocol_SetsHasMalformed()
    {
        var l = MakeRequestListener();
        Add(l, ":method", "CONNECT");
        Add(l, ":protocol", "websocket");
        Add(l, ":protocol", "webtransport"); // duplicate

        Assert.IsTrue(l.HasMalformedHeader);
        StringAssert.Contains(l.MalformedReason!, "duplicate");
    }

    // =========================================================================
    // Well-formed blocks after several headers — no false positives
    // =========================================================================

    [TestMethod]
    public void RequestBlock_ManyHeaders_NoneUppercaseOrCtl_IsClean()
    {
        var l = MakeRequestListener();
        Add(l, ":method", "PUT");
        Add(l, ":scheme", "https");
        Add(l, ":authority", "api.example.com");
        Add(l, ":path", "/v1/resource");
        Add(l, "content-type", "application/json");
        Add(l, "content-length", "42");
        Add(l, "accept", "application/json");
        Add(l, "accept-encoding", "gzip, deflate, br");
        Add(l, "user-agent", "test-client/1.0");

        Assert.IsFalse(l.HasMalformedHeader,
            $"Well-formed request block must not set HasMalformedHeader: {l.MalformedReason}");
    }
}
