using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Plus.Security.Waf;

namespace Titanium.Plus.Tests;

/// <summary>
///     Multipart helpers for WAF body screening.
///     Bug classes: quoted boundaries miss, filename= parts are scanned, DispositionName matches inside filenames.
/// </summary>
[TestClass]
public class WafBodyPolicyMultipartBehaviorTests
{
    [TestMethod]
    public void MultipartBoundary_ParsesQuotedUnquotedAndMissing()
    {
        Assert.AreEqual("----bound", WafBodyPolicy.MultipartBoundary("multipart/form-data; boundary=----bound"));
        Assert.AreEqual("abc", WafBodyPolicy.MultipartBoundary("multipart/form-data; boundary=\"abc\"; charset=utf-8"));
        Assert.IsNull(WafBodyPolicy.MultipartBoundary("multipart/form-data"));
        Assert.IsNull(WafBodyPolicy.MultipartBoundary("multipart/form-data; boundary=\""));
        // Boundary extraction is independent of Classify(); callers gate on media type first.
        Assert.AreEqual("nope", WafBodyPolicy.MultipartBoundary("text/plain; boundary=nope"));
    }

    [TestMethod]
    public void IsTextPart_SkipsUploads_AndReadsQuotedNames()
    {
        Assert.IsFalse(WafBodyPolicy.IsTextPart(
            "Content-Disposition: form-data; name=\"file\"; filename=\"a.txt\"\r\n".AsSpan(),
            out _));

        Assert.IsTrue(WafBodyPolicy.IsTextPart(
            "Content-Disposition: form-data; name=\"note\"\r\nContent-Type: text/plain\r\n".AsSpan(),
            out var note));
        Assert.AreEqual("note", note.ToString());

        Assert.IsTrue(WafBodyPolicy.IsTextPart(
            "Content-Disposition: form-data; name=plain\r\n".AsSpan(),
            out var plain));
        Assert.AreEqual("plain", plain.ToString());

        Assert.IsFalse(WafBodyPolicy.IsTextPart(
            "Content-Disposition: form-data; name=\"bin\"\r\nContent-Type: application/octet-stream\r\n".AsSpan(),
            out _));

        // Coarse Contains("filename") skip: field names that embed that substring are not scanned.
        Assert.IsFalse(WafBodyPolicy.IsTextPart(
            "Content-Disposition: form-data; name=\"myfilename\"\r\n".AsSpan(),
            out _));
    }

    [TestMethod]
    public void HasNestedUnboundedRepeat_IgnoresCharClassAndEscapes()
    {
        Assert.IsFalse(WafPatternLinter.HasNestedUnboundedRepeat(@"[+(]+"));
        Assert.IsFalse(WafPatternLinter.HasNestedUnboundedRepeat(@"\(a+\)+"));
        Assert.IsTrue(WafPatternLinter.HasNestedUnboundedRepeat("(a+)+"));
        Assert.IsTrue(WafPatternLinter.HasNestedUnboundedRepeat("(x*y)*"));
        Assert.IsTrue(WafPatternLinter.HasNestedUnboundedRepeat("(a+){1,}"));
        Assert.IsFalse(WafPatternLinter.HasNestedUnboundedRepeat("(a+){1,3}"));

        var regex = WafPatternLinter.Compile(@"\bunion\b", ignoreCase: true);
        Assert.IsTrue(WafPatternLinter.IsNonBacktracking(regex));
        WafPatternLinter.WarmUp(regex);
    }
}
