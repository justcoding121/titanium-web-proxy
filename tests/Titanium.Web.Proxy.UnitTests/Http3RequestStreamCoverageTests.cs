#pragma warning disable CA1416
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Http3;

namespace Titanium.Web.Proxy.UnitTests;

[TestClass]
public class Http3RequestStreamCoverageTests
{
    private static (string? Method, string? Scheme, string? Authority, string? Path,
        List<(string Name, string Value)> Regular) Extract(List<(string Name, string Value)> fields)
    {
        var method = typeof(Http3RequestStream).GetMethod("ExtractPseudoHeaders",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.IsNotNull(method);
        return ((string?, string?, string?, string?, List<(string, string)>))
            method.Invoke(null, [fields])!;
    }

    [TestMethod]
    public void ExtractPseudoHeaders_SeparatesKnownPseudoAndRegularFields()
    {
        var result = Extract(
        [
            (":method", "POST"),
            (":scheme", "https"),
            (":authority", "example.com:443"),
            (":path", "/submit"),
            (":unknown", "ignored"),
            ("content-type", "text/plain"),
            ("x-test", "yes")
        ]);

        Assert.AreEqual("POST", result.Method);
        Assert.AreEqual("https", result.Scheme);
        Assert.AreEqual("example.com:443", result.Authority);
        Assert.AreEqual("/submit", result.Path);
        CollectionAssert.AreEqual(
            new[] { ("content-type", "text/plain"), ("x-test", "yes") },
            result.Regular.ToArray());
    }

    [TestMethod]
    public void ExtractPseudoHeaders_AllowsMissingOptionalFields()
    {
        var result = Extract([(":method", "OPTIONS")]);

        Assert.AreEqual("OPTIONS", result.Method);
        Assert.IsNull(result.Scheme);
        Assert.IsNull(result.Authority);
        Assert.IsNull(result.Path);
        Assert.AreEqual(0, result.Regular.Count);
    }

    [TestMethod]
    public void StatusCodeString_AndHasUpperAscii_CoverHelpers()
    {
        var status = typeof(Http3RequestStream).GetMethod("StatusCodeString",
            BindingFlags.NonPublic | BindingFlags.Static)!;
        foreach (var code in new[] { 200, 204, 301, 302, 304, 400, 404, 500, 502, 503, 418 })
            Assert.AreEqual(code.ToString(), (string)status.Invoke(null, [code])!);

        var upper = typeof(Http3RequestStream).GetMethod("HasUpperAscii",
            BindingFlags.NonPublic | BindingFlags.Static)!;
        Assert.IsTrue((bool)upper.Invoke(null, ["Host"])!);
        Assert.IsFalse((bool)upper.Invoke(null, ["content-type"])!);
        Assert.IsFalse((bool)upper.Invoke(null, [""])!);

        var staticOnly = typeof(Http3RequestStream).GetMethod("IsStaticOnlyQpackBlock",
            BindingFlags.NonPublic | BindingFlags.Static)!;
        Assert.IsFalse((bool)staticOnly.Invoke(null, [Array.Empty<byte>()])!);
        Assert.IsTrue((bool)staticOnly.Invoke(null, [new byte[] { 0, 0 }])!);
        Assert.IsFalse((bool)staticOnly.Invoke(null, [new byte[] { 0x80 }])!);
    }
}
#pragma warning restore CA1416
