using System;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Models;

namespace Titanium.Web.Proxy.UnitTests;

/// <summary>
///     Coverage for <see cref="ByteString" /> helpers still flagged by Sonar new-code.
/// </summary>
[TestClass]
public class ByteStringTests
{
    private static ByteString Bs(string s) => new(Encoding.ASCII.GetBytes(s));

    [TestMethod]
    public void IndexOf_FindsByteOrReturnsMinusOne()
    {
        var s = Bs("hello");
        Assert.AreEqual(1, s.IndexOf((byte)'e'));
        Assert.AreEqual(4, s.IndexOf((byte)'o'));
        Assert.AreEqual(-1, s.IndexOf((byte)'z'));
        Assert.AreEqual(-1, ByteString.Empty.IndexOf((byte)'a'));
    }

    [TestMethod]
    public void Slice_Start_ReturnsSuffix()
    {
        var s = Bs("abcdef");
        var sliced = s.Slice(2);
        Assert.AreEqual(4, sliced.Length);
        Assert.AreEqual("cdef", sliced.ToString());
        Assert.AreEqual(0, s.Slice(6).Length);
    }

    [TestMethod]
    public void EqualsIgnoreCaseAscii_MismatchBreaksEarly()
    {
        Assert.IsTrue(Bs("Host").EqualsIgnoreCaseAscii(Bs("host")));
        Assert.IsTrue(Bs("CONTENT-TYPE").EqualsIgnoreCaseAscii(Bs("content-type")));
        // Same length, first differing byte after case fold → return false on mismatch.
        Assert.IsFalse(Bs("abcd").EqualsIgnoreCaseAscii(Bs("abXd")));
        Assert.IsFalse(Bs("Ab").EqualsIgnoreCaseAscii(Bs("Ac")));
        Assert.IsFalse(Bs("abc").EqualsIgnoreCaseAscii(Bs("ab"))); // length mismatch
    }

    [TestMethod]
    public void SpanContainsIgnoreCaseAscii_ReturnsFalseWhenAbsent()
    {
        var hay = Bs("Transfer-Encoding: gzip");
        Assert.IsTrue(hay.SpanContainsIgnoreCaseAscii("gzip"u8));
        Assert.IsTrue(hay.SpanContainsIgnoreCaseAscii("ENCODING"u8));
        Assert.IsTrue(hay.SpanContainsIgnoreCaseAscii(ReadOnlySpan<byte>.Empty));

        Assert.IsFalse(hay.SpanContainsIgnoreCaseAscii("chunked"u8));
        Assert.IsFalse(hay.SpanContainsIgnoreCaseAscii("Transfer-Encoding: gzip; extra"u8)); // longer than hay
        Assert.IsFalse(Bs("abc").SpanContainsIgnoreCaseAscii("abd"u8));
        // Mismatch mid-needle forces the inner break → continue scanning → false.
        Assert.IsFalse(Bs("xxABCyy").SpanContainsIgnoreCaseAscii("ABX"u8));
    }
}
