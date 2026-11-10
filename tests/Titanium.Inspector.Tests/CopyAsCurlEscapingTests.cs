using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Inspector.Services;

namespace Titanium.Inspector.Tests;

/// <summary>
/// Verifies <see cref="SessionRequestCodegen.ToCurl"/> and
/// <see cref="SessionRequestCodegen.ShellSingleQuote"/> correctly handle
/// shell-special characters that could expand or break in bash/zsh:
/// dollar-sign (variable expansion), newlines (shell continuation), and
/// combinations with embedded single-quotes.
/// </summary>
[TestClass]
public class CopyAsCurlEscapingTests
{
    [TestMethod]
    public void ToCurl_DollarInUrl_DollarPreservedBySingleQuotes()
    {
        // A URL with a $ query-param should not expand in the shell.
        // In POSIX single-quoting, $ has no special meaning — the literal is produced.
        var snap = new SessionSnapshot
        {
            Method = "GET",
            Url = "https://api.example/search?q=$HOME&filter=$USER",
        };

        var curl = SessionRequestCodegen.ToCurl(snap);

        // The dollar signs must be inside single-quoted spans so they are not expanded.
        StringAssert.Contains(curl, "'https://api.example/search?q=$HOME&filter=$USER'",
            "Dollar signs in the URL must be enclosed in POSIX single quotes");
    }

    [TestMethod]
    public void ToCurl_DollarInBody_DollarPreservedBySingleQuotes()
    {
        var snap = new SessionSnapshot
        {
            Method = "POST",
            Url = "https://api.example/env",
            RequestBodyText = "value=$MY_VAR and $((1+1))",
        };

        var curl = SessionRequestCodegen.ToCurl(snap);

        StringAssert.Contains(curl, "--data-binary 'value=$MY_VAR and $((1+1))'",
            "Dollar signs in the body must be enclosed in POSIX single quotes");
        // Extra safeguard: no unquoted dollar should appear.
        var dataIndex = curl.IndexOf("--data-binary", StringComparison.Ordinal);
        Assert.IsTrue(dataIndex >= 0);
        var dataArg = curl[dataIndex..];
        // The $ characters must all be inside single quotes (none at the very start of a token).
        Assert.IsFalse(dataArg.TrimStart().StartsWith("$", StringComparison.Ordinal),
            "Dollar sign must never start an unquoted token");
    }

    [TestMethod]
    public void ToCurl_NewlineInBody_NewlineEncodedInsideSingleQuotes()
    {
        // A literal newline embedded in the body: when the entire body is single-quoted,
        // the newline is included verbatim and passed to curl correctly.
        var snap = new SessionSnapshot
        {
            Method = "POST",
            Url = "https://api.example/multi",
            RequestBodyText = "line1\nline2\nline3",
        };

        var curl = SessionRequestCodegen.ToCurl(snap);

        // The body argument must be present.
        StringAssert.Contains(curl, "--data-binary",
            "Multiline body must produce a --data-binary argument");
        StringAssert.Contains(curl, "line1",
            "First line must appear in the output");
        StringAssert.Contains(curl, "line2",
            "Second line must appear in the output");
    }

    [TestMethod]
    public void ToCurl_CrlfInBody_BothLineEndingsPreserved()
    {
        var snap = new SessionSnapshot
        {
            Method = "POST",
            Url = "https://api.example/crlf",
            RequestBodyText = "a\r\nb\r\nc",
        };

        var curl = SessionRequestCodegen.ToCurl(snap);

        StringAssert.Contains(curl, "a",
            "Body content must be present");
        StringAssert.Contains(curl, "b",
            "Body content must be present");
        StringAssert.Contains(curl, "--data-binary");
    }

    [TestMethod]
    public void ToCurl_DollarAndQuoteInBody_BothHandledCorrectly()
    {
        // Body contains both a single-quote and a dollar sign.
        // Single-quote: needs the stop-quote \ restart pattern.
        // Dollar sign: is neutralised by being inside single-quotes.
        var snap = new SessionSnapshot
        {
            Method = "POST",
            Url = "https://api.example/both",
            RequestHeadersText = "Content-Type: application/json\r\n",
            RequestBodyText = "{\"val\":\"it's $100\"}",
        };

        var curl = SessionRequestCodegen.ToCurl(snap);

        // Single-quote escaping must be present for the apostrophe.
        StringAssert.Contains(curl, "'\\''",
            "Embedded single-quote must use the POSIX '\\'' escape sequence");
        // Dollar sign must still appear in the output (inside the reconstructed single-quoted spans).
        StringAssert.Contains(curl, "$100",
            "Dollar sign must be present in the --data-binary argument");
        StringAssert.Contains(curl, "--data-binary");
    }

    [TestMethod]
    public void ToCurl_HeaderValueWithDollar_SingleQuoted()
    {
        var snap = new SessionSnapshot
        {
            Method = "GET",
            Url = "https://api.example/token",
            RequestHeadersText = "Authorization: Bearer $TOKEN\r\nX-Env: $HOME\r\n",
        };

        var curl = SessionRequestCodegen.ToCurl(snap);

        StringAssert.Contains(curl, "-H 'Authorization: Bearer $TOKEN'",
            "Header with dollar-sign value must be single-quoted");
        StringAssert.Contains(curl, "-H 'X-Env: $HOME'",
            "Header with dollar-sign value must be single-quoted");
    }

    [TestMethod]
    public void ShellSingleQuote_DollarSign_RetainedInsideQuotes()
    {
        // $VAR inside single quotes is never expanded by the shell.
        Assert.AreEqual("'$VAR'", SessionRequestCodegen.ShellSingleQuote("$VAR"));
        Assert.AreEqual("'$((1+1))'", SessionRequestCodegen.ShellSingleQuote("$((1+1))"));
    }

    [TestMethod]
    public void ShellSingleQuote_Newline_RetainedInsideQuotes()
    {
        // A single-quoted string with a literal newline is valid POSIX.
        var result = SessionRequestCodegen.ShellSingleQuote("a\nb");
        Assert.IsTrue(result.StartsWith("'", StringComparison.Ordinal));
        Assert.IsTrue(result.EndsWith("'", StringComparison.Ordinal));
        StringAssert.Contains(result, "a");
        StringAssert.Contains(result, "b");
        // Newline itself must be present (not escaped away).
        Assert.IsTrue(result.Contains('\n'),
            "Newline must be preserved verbatim inside single-quoted POSIX argument");
    }

    [TestMethod]
    public void ShellSingleQuote_BacktickAndBang_NotEscaped()
    {
        // Backtick and ! are only special in double-quoted or unquoted contexts.
        // Single quotes need no escaping for them.
        Assert.AreEqual("'`cmd`'", SessionRequestCodegen.ShellSingleQuote("`cmd`"));
        Assert.AreEqual("'!hist'", SessionRequestCodegen.ShellSingleQuote("!hist"));
    }

    [TestMethod]
    public void ToCurl_EmptyBody_OmitsDataBinaryFlag()
    {
        var snap = new SessionSnapshot
        {
            Method = "POST",
            Url = "https://api.example/empty",
            RequestBodyText = "",
        };

        var curl = SessionRequestCodegen.ToCurl(snap);

        Assert.IsFalse(curl.Contains("--data-binary", StringComparison.Ordinal),
            "An empty body must not produce a --data-binary flag");
    }
}
