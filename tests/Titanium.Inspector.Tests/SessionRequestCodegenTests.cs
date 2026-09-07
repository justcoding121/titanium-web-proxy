using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Inspector.Services;

namespace Titanium.Inspector.Tests;

[TestClass]
public class SessionRequestCodegenTests
{
    [TestMethod]
    public void CanGenerate_RejectsTunnelAndEmptyUrl()
    {
        Assert.IsFalse(SessionRequestCodegen.CanGenerate(null));
        Assert.IsFalse(SessionRequestCodegen.CanGenerate(new SessionSnapshot { Url = "" }));
        Assert.IsFalse(SessionRequestCodegen.CanGenerate(new SessionSnapshot
        {
            Url = "https://example.com/",
            IsTunnel = true,
        }));
        Assert.IsTrue(SessionRequestCodegen.CanGenerate(new SessionSnapshot
        {
            Url = "https://example.com/",
        }));
    }

    [TestMethod]
    public void ToCurl_Get_WithHeaders()
    {
        var snap = new SessionSnapshot
        {
            Method = "GET",
            Url = "https://api.example/v1?x=1",
            RequestHeadersText =
                "Host: api.example\r\n" +
                "Accept: application/json\r\n" +
                "Content-Length: 0\r\n" +
                "X-Trace: a'b\r\n",
        };

        var curl = SessionRequestCodegen.ToCurl(snap);
        StringAssert.StartsWith(curl, "curl 'https://api.example/v1?x=1'");
        Assert.IsFalse(curl.Contains("-X ", StringComparison.Ordinal));
        StringAssert.Contains(curl, "-H 'Accept: application/json'");
        StringAssert.Contains(curl, "-H 'X-Trace: a'\\''b'");
        Assert.IsFalse(curl.Contains("Host:", StringComparison.Ordinal));
        Assert.IsFalse(curl.Contains("Content-Length", StringComparison.Ordinal));
    }

    [TestMethod]
    public void ToCurl_Post_WithBody()
    {
        var snap = new SessionSnapshot
        {
            Method = "POST",
            Url = "https://api.example/items",
            RequestHeadersText = "Content-Type: application/json\n",
            RequestBodyText = "{\"n\":1}",
        };

        var curl = SessionRequestCodegen.ToCurl(snap);
        StringAssert.Contains(curl, "-X 'POST'");
        StringAssert.Contains(curl, "-H 'Content-Type: application/json'");
        StringAssert.Contains(curl, "--data-binary '{\"n\":1}'");
    }

    [TestMethod]
    public void ToCurl_UsesBodyBytesWhenTextMissing()
    {
        var snap = new SessionSnapshot
        {
            Method = "PUT",
            Url = "https://api.example/b",
            RequestBodyBytes = System.Text.Encoding.UTF8.GetBytes("raw-bytes"),
        };

        var curl = SessionRequestCodegen.ToCurl(snap);
        StringAssert.Contains(curl, "--data-binary 'raw-bytes'");
    }

    [TestMethod]
    public void ToFetch_SimpleGet()
    {
        var snap = new SessionSnapshot
        {
            Method = "GET",
            Url = "https://example.com/",
        };

        Assert.AreEqual("fetch(\"https://example.com/\");", SessionRequestCodegen.ToFetch(snap));
    }

    [TestMethod]
    public void ToFetch_Post_WithHeadersAndBody()
    {
        var snap = new SessionSnapshot
        {
            Method = "post",
            Url = "https://api.example/x",
            RequestHeadersText = "Content-Type: text/plain\nAuthorization: Bearer t\n",
            RequestBodyText = "hello\"world",
        };

        var fetch = SessionRequestCodegen.ToFetch(snap);
        StringAssert.Contains(fetch, "fetch(\"https://api.example/x\", {");
        StringAssert.Contains(fetch, "\"method\": \"POST\"");
        StringAssert.Contains(fetch, "\"Content-Type\": \"text/plain\"");
        StringAssert.Contains(fetch, "\"Authorization\": \"Bearer t\"");
        StringAssert.Contains(fetch, "\"body\": \"hello\\\"world\"");
        StringAssert.EndsWith(fetch, "});");
    }

    [TestMethod]
    public void ShellSingleQuote_EscapesEmbeddedQuotes()
    {
        Assert.AreEqual("''", SessionRequestCodegen.ShellSingleQuote(""));
        Assert.AreEqual("'a'\\''b'", SessionRequestCodegen.ShellSingleQuote("a'b"));
    }
}
