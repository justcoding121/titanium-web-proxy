using System;
using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Abstractions.Routing;
using Titanium.Web.Proxy.Transforms;

namespace Titanium.Web.Proxy.UnitTests.Routing;

/// <summary><c>ResponseCors</c>: the allow header on the actual (non-preflight) response.</summary>
[TestClass]
public class TransformResponseCorsTests
{
    private const string AllowOrigin = "Access-Control-Allow-Origin";

    private static TransformRequestContext Apply(string origins, string? requestOrigin)
    {
        var context = new TransformRequestContext
        {
            Path = "/",
            ExistingRequestHeader = name =>
                name.Equals("Origin", StringComparison.OrdinalIgnoreCase) ? requestOrigin : null,
        };
        var transform = new TransformConfig
        {
            Kind = TransformKinds.ResponseCors,
            Parameters = new Dictionary<string, string> { ["origins"] = origins },
        };
        new TransformEngine().ApplyRequestTransforms([transform], context);
        return context;
    }

    [TestMethod]
    public void Kind_RequiresTheOriginsParameter()
    {
        Assert.IsTrue(TransformKinds.TryGetRequiredParameters(TransformKinds.ResponseCors, out var required));
        CollectionAssert.AreEqual(new[] { "origins" }, new List<string>(required));
    }

    [TestMethod]
    public void Wildcard_AllowsEveryOrigin_WithoutVary()
    {
        var context = Apply("*", "https://any.example.com");

        Assert.AreEqual("*", context.ResponseHeadersToSet[AllowOrigin]);
        Assert.IsFalse(context.ResponseHeadersToSet.ContainsKey("Vary"));
    }

    [TestMethod]
    public void Wildcard_StillAllowsARequestWithNoOrigin()
    {
        Assert.AreEqual("*", Apply("*", null).ResponseHeadersToSet[AllowOrigin]);
    }

    [TestMethod]
    public void List_EchoesAListedOrigin_AndVariesOnOrigin()
    {
        var context = Apply("https://a.example.com,https://b.example.com", "https://b.example.com");

        Assert.AreEqual("https://b.example.com", context.ResponseHeadersToSet[AllowOrigin]);
        Assert.AreEqual("Origin", context.ResponseHeadersToSet["Vary"]);
        Assert.IsTrue(context.ResponseHeadersToAppend!.Contains("Vary"), "Vary joins what the origin sent.");
    }

    [TestMethod]
    public void List_AddsNoAllowHeaderForAnUnlistedOrigin_ButStillVaries()
    {
        var context = Apply("https://a.example.com", "https://evil.example.com");

        Assert.IsFalse(context.ResponseHeadersToSet.ContainsKey(AllowOrigin));
        Assert.AreEqual("Origin", context.ResponseHeadersToSet["Vary"]);
    }

    [TestMethod]
    public void List_AddsNoAllowHeaderWhenTheRequestHasNoOrigin()
    {
        var context = Apply("https://a.example.com", null);

        Assert.IsFalse(context.ResponseHeadersToSet.ContainsKey(AllowOrigin));
        Assert.AreEqual("Origin", context.ResponseHeadersToSet["Vary"]);
    }

    [TestMethod]
    public void List_MatchesOriginsExactlyAndCaseSensitively()
    {
        Assert.IsFalse(Apply("https://a.example.com", "https://A.example.com").ResponseHeadersToSet.ContainsKey(AllowOrigin));
        Assert.IsFalse(Apply("https://a.example.com", "https://a.example.com:8443").ResponseHeadersToSet.ContainsKey(AllowOrigin));
    }

    [TestMethod]
    public void List_ToleratesSpacesAroundEntries()
    {
        var context = Apply("https://a.example.com , https://b.example.com", "https://b.example.com");

        Assert.AreEqual("https://b.example.com", context.ResponseHeadersToSet[AllowOrigin]);
    }
}
