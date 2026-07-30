using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Helpers;

namespace Titanium.Web.Proxy.UnitTests;

/// <summary>
///     Phase E.15: "Replace ad-hoc authority splitting with one RFC 3986-aware parser supporting
///     bracketed IPv6 and strict port validation. Reject malformed unbracketed IPv6 authorities
///     instead of guessing."
/// </summary>
[TestClass]
public class AuthorityParserTests
{
    [TestMethod]
    public void TryParse_HostOnly_UsesDefaultPort()
    {
        Assert.IsTrue(AuthorityParser.TryParse("example.com", 443, out var host, out var port));
        Assert.AreEqual("example.com", host);
        Assert.AreEqual(443, port);
    }

    [TestMethod]
    public void TryParse_HostAndPort_SplitsCorrectly()
    {
        Assert.IsTrue(AuthorityParser.TryParse("example.com:8080", 443, out var host, out var port));
        Assert.AreEqual("example.com", host);
        Assert.AreEqual(8080, port);
    }

    [TestMethod]
    public void TryParse_BracketedIPv6WithPort_StripsBracketsAndSplitsPort()
    {
        Assert.IsTrue(AuthorityParser.TryParse("[::1]:8080", 443, out var host, out var port));
        Assert.AreEqual("::1", host);
        Assert.AreEqual(8080, port);
    }

    [TestMethod]
    public void TryParse_BracketedIPv6WithoutPort_UsesDefaultPort()
    {
        Assert.IsTrue(AuthorityParser.TryParse("[2001:db8::1]", 443, out var host, out var port));
        Assert.AreEqual("2001:db8::1", host);
        Assert.AreEqual(443, port);
    }

    [TestMethod]
    public void TryParse_UnbracketedIPv6Literal_IsRejectedRatherThanGuessed()
    {
        // Ambiguous: could be host "::1" port 8080, or a literal host "::1:8080" with an implicit
        // default port. RFC 3986 requires brackets specifically to remove this ambiguity.
        Assert.IsFalse(AuthorityParser.TryParse("::1:8080", 443, out _, out _));
    }

    [TestMethod]
    public void TryParse_UnterminatedBracket_IsRejected()
    {
        Assert.IsFalse(AuthorityParser.TryParse("[::1", 443, out _, out _));
    }

    [TestMethod]
    public void TryParse_InvalidIPv6InsideBrackets_IsRejected()
    {
        Assert.IsFalse(AuthorityParser.TryParse("[not-an-ipv6]:443", 443, out _, out _));
    }

    [DataTestMethod]
    [DataRow("example.com:0")]
    [DataRow("example.com:65536")]
    [DataRow("example.com:abc")]
    [DataRow("example.com:-1")]
    [DataRow("example.com:")]
    public void TryParse_InvalidPort_IsRejected(string authority)
    {
        Assert.IsFalse(AuthorityParser.TryParse(authority, 443, out _, out _));
    }

    [TestMethod]
    public void TryParse_EmptyOrNullAuthority_IsRejected()
    {
        Assert.IsFalse(AuthorityParser.TryParse(null, 443, out _, out _));
        Assert.IsFalse(AuthorityParser.TryParse("", 443, out _, out _));
    }

    [TestMethod]
    public void Parse_ValidAuthority_ReturnsTuple()
    {
        var (host, port) = AuthorityParser.Parse("[::1]:9999", 443);
        Assert.AreEqual("::1", host);
        Assert.AreEqual(9999, port);
    }

    [TestMethod]
    public void Parse_MalformedAuthority_ThrowsFormatException()
    {
        Assert.ThrowsException<FormatException>(() => AuthorityParser.Parse("::1:8080", 443));
    }
}
