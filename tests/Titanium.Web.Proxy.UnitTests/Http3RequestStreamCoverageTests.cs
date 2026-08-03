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
}
#pragma warning restore CA1416
