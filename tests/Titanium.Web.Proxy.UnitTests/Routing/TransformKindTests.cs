using System;
using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Abstractions.Routing;
using Titanium.Web.Proxy.Transforms;

namespace Titanium.Web.Proxy.UnitTests.Routing;

[TestClass]
public class TransformKindTests
{
    private static readonly string[] CookieOnly = ["Cookie"];

    private static TransformRequestContext Run(string path, string kind, params (string Key, string Value)[] parameters) =>
        Run(new TransformRequestContext { Path = path }, kind, parameters);

    private static TransformRequestContext Run(TransformRequestContext context, string kind, params (string Key, string Value)[] parameters)
    {
        var map = new Dictionary<string, string>();
        foreach (var (key, value) in parameters)
        {
            map[key] = value;
        }

        new TransformEngine().ApplyRequestTransforms([new TransformConfig { Kind = kind, Parameters = map }], context);
        return context;
    }

    [TestMethod]
    public void NoTransforms_LeaveTheContextAlone()
    {
        var context = new TransformRequestContext { Path = "/a" };
        var engine = new TransformEngine();
        engine.ApplyRequestTransforms(null, context);
        engine.ApplyRequestTransforms([], context);
        Assert.AreEqual("/a", context.Path);
    }

    [TestMethod]
    public void PathSet_AddsTheLeadingSlash()
    {
        Assert.AreEqual("/fixed", Run("/old", TransformKinds.PathSet, ("path", "fixed")).Path);
        Assert.AreEqual("/fixed", Run("/old", TransformKinds.PathSet, ("path", "/fixed")).Path);
    }

    [TestMethod]
    public void PathPattern_SubstitutesTheRemainder()
    {
        Assert.AreEqual("/api/a/b", Run("/a/b", TransformKinds.PathPattern, ("pattern", "/api{**remainder}")).Path);
    }

    [TestMethod]
    public void PathRegexReplace_RewritesTheMatch()
    {
        Assert.AreEqual("/v2/x", Run("/v1/x", TransformKinds.PathRegexReplace, ("pattern", "^/v1"), ("replacement", "/v2")).Path);
    }

    [TestMethod]
    public void PathPrefixAndRemovePrefix_AdjustTheSlashes()
    {
        Assert.AreEqual("/api/x", Run("/x", TransformKinds.PathPrefix, ("prefix", "api")).Path);
        Assert.AreEqual("/x", Run("/x", TransformKinds.PathPrefix, ("prefix", "")).Path);
        Assert.AreEqual("/x", Run("/api/x", TransformKinds.PathRemovePrefix, ("prefix", "/api")).Path);
        Assert.AreEqual("/", Run("/api", TransformKinds.PathRemovePrefix, ("prefix", "/api")).Path);
        Assert.AreEqual("/other", Run("/other", TransformKinds.PathRemovePrefix, ("prefix", "/api")).Path);
    }

    [TestMethod]
    public void HttpMethodChange_HonoursTheFromFilter()
    {
        var matching = Run(new TransformRequestContext { Path = "/", Method = "GET" }, TransformKinds.HttpMethodChange, ("from", "get"), ("to", "POST"));
        Assert.AreEqual("POST", matching.Method);
        var other = Run(new TransformRequestContext { Path = "/", Method = "PUT" }, TransformKinds.HttpMethodChange, ("from", "GET"), ("to", "POST"));
        Assert.AreEqual("PUT", other.Method);
        var wildcard = Run(new TransformRequestContext { Path = "/", Method = "PUT" }, TransformKinds.HttpMethodChange, ("from", "*"), ("to", "POST"));
        Assert.AreEqual("POST", wildcard.Method);
        var unconditional = Run(new TransformRequestContext { Path = "/", Method = "PUT" }, TransformKinds.HttpMethodChange, ("to", "DELETE"));
        Assert.AreEqual("DELETE", unconditional.Method);
    }

    [TestMethod]
    public void QueryTransforms_SetAppendAndRemove()
    {
        Assert.AreEqual("/p?a=1", Run("/p", TransformKinds.QueryValueSet, ("name", "a"), ("value", "1")).Path);
        Assert.AreEqual("/p?a=2&b=3", Run("/p?a=1&b=3", TransformKinds.QueryValueSet, ("name", "a"), ("value", "2")).Path);
        Assert.AreEqual("/p?n=v%20w", Run("/p", TransformKinds.QueryValueAppend, ("name", "n"), ("value", "v w")).Path);
        Assert.AreEqual("/p?x=1&n=2", Run("/p?x=1", TransformKinds.QueryValueAppend, ("name", "n"), ("value", "2")).Path);
        Assert.AreEqual("/p?b=2", Run("/p?a=1&b=2", TransformKinds.QueryRemove, ("name", "a")).Path);
        Assert.AreEqual("/p", Run("/p?a=1", TransformKinds.QueryRemove, ("name", "a")).Path);
        Assert.AreEqual("/p", Run("/p", TransformKinds.QueryRemove, ("name", "a")).Path);
    }

    [TestMethod]
    public void QueryRouteParameter_CopiesACapturedRouteValue()
    {
        var context = new TransformRequestContext { Path = "/items" };
        context.RouteValues["id"] = "42";
        Run(context, TransformKinds.QueryRouteParameter, ("name", "item"), ("routeValue", "id"));
        Assert.AreEqual("/items?item=42", context.Path);

        var missing = new TransformRequestContext { Path = "/items" };
        Run(missing, TransformKinds.QueryRouteParameter, ("name", "item"), ("routeValue", "id"));
        Assert.AreEqual("/items", missing.Path);
    }

    [TestMethod]
    public void RequestHeaderTransforms_SetAppendRemoveAndAllowList()
    {
        var set = Run("/", TransformKinds.RequestHeaderSet, ("name", "X-A"), ("value", "1"));
        Assert.AreEqual("1", set.Headers["X-A"]);

        var append = new TransformRequestContext { Path = "/" };
        append.Headers["X-A"] = "1";
        Run(append, TransformKinds.RequestHeaderAppend, ("name", "x-a"), ("value", "2"));
        Assert.AreEqual("1, 2", append.Headers["X-A"]);
        var fresh = Run("/", TransformKinds.RequestHeaderAppend, ("name", "X-B"), ("value", "9"));
        Assert.AreEqual("9", fresh.Headers["X-B"]);

        var remove = new TransformRequestContext { Path = "/" };
        remove.Headers["X-A"] = "1";
        Run(remove, TransformKinds.RequestHeaderRemove, ("name", "X-A"));
        Assert.IsFalse(remove.Headers.ContainsKey("X-A"));
        Assert.IsTrue(remove.HeadersToRemove.Contains("X-A"));

        var allow = new TransformRequestContext { Path = "/" };
        allow.Headers["Accept"] = "*/*";
        allow.Headers["X-Keep"] = "1";
        allow.Headers["Cookie"] = "secret";
        Run(allow, TransformKinds.RequestHeadersAllowed, ("headers", "Accept, x-keep"));
        CollectionAssert.AreEquivalent(CookieOnly, new List<string>(allow.HeadersToRemove));
    }

    [TestMethod]
    public void HostAndClientCertificateTransforms_UseTheContext()
    {
        Assert.IsTrue(Run("/", TransformKinds.RequestHostUseDestination).UseDestinationHost);

        var original = new TransformRequestContext { Path = "/", OriginalHost = "app.test" };
        Run(original, TransformKinds.RequestHeaderOriginalHost);
        Assert.AreEqual("app.test", original.Headers["X-Original-Host"]);
        var none = Run("/", TransformKinds.RequestHeaderOriginalHost);
        Assert.IsFalse(none.Headers.ContainsKey("X-Original-Host"));

        var cert = new TransformRequestContext { Path = "/", ClientCertificateSubject = "CN=client" };
        Run(cert, TransformKinds.ClientCertHeader, ("name", "X-Client-Cert"));
        Assert.AreEqual("CN=client", cert.Headers["X-Client-Cert"]);
        var absent = Run("/", TransformKinds.ClientCertHeader, ("name", "X-Client-Cert"));
        Assert.IsFalse(absent.Headers.ContainsKey("X-Client-Cert"));

        // The header is identity. Without a certificate it is still removed, so a client cannot assert one.
        Assert.IsTrue(absent.HeadersToRemove.Contains("X-Client-Cert"));
        Assert.IsTrue(cert.HeadersToRemove.Contains("X-Client-Cert"));
    }

    [TestMethod]
    public void ResponseHeaderTransforms_AreStagedOnTheContext()
    {
        var set = Run("/", TransformKinds.ResponseHeaderSet, ("name", "X-R"), ("value", "1"));
        Assert.AreEqual("1", set.ResponseHeadersToSet["X-R"]);
        var append = Run("/", TransformKinds.ResponseHeaderAppend, ("name", "X-S"), ("value", "2"));
        Assert.AreEqual("2", append.ResponseHeadersToSet["X-S"]);
        var remove = Run("/", TransformKinds.ResponseHeaderRemove, ("name", "Server"));
        Assert.IsTrue(remove.ResponseHeadersToRemove.Contains("Server"));
        var allowed = Run("/", TransformKinds.ResponseHeadersAllowed, ("headers", "Content-Type"));
        Assert.IsTrue(allowed.ResponseHeadersToRemove.Contains("*Content-Type"));
    }

    [TestMethod]
    public void Transform_WithoutItsParameters_IsIgnored()
    {
        var context = Run("/x", TransformKinds.PathSet);
        Assert.AreEqual("/x", context.Path);
        var noMap = new TransformRequestContext { Path = "/x" };
        new TransformEngine().ApplyRequestTransforms([new TransformConfig { Kind = TransformKinds.PathPrefix }], noMap);
        Assert.AreEqual("/x", noMap.Path);
    }

    [DataTestMethod]
    [DataRow(TransformKinds.PathRemovePrefix, "prefix")]
    [DataRow(TransformKinds.PathPrefix, "prefix")]
    [DataRow(TransformKinds.RequestHeaderSet, "name,value")]
    [DataRow(TransformKinds.RequestHeaderRemove, "name")]
    [DataRow(TransformKinds.QueryValueSet, "name,value")]
    [DataRow(TransformKinds.RequestHostUseDestination, "")]
    [DataRow(TransformKinds.ResponseHeaderSet, "name,value")]
    [DataRow(TransformKinds.ResponseHeaderRemove, "name")]
    [DataRow(TransformKinds.PathSet, "path")]
    [DataRow(TransformKinds.PathPattern, "pattern")]
    [DataRow(TransformKinds.PathRegexReplace, "pattern,replacement")]
    [DataRow(TransformKinds.HttpMethodChange, "to")]
    [DataRow(TransformKinds.QueryValueAppend, "name,value")]
    [DataRow(TransformKinds.QueryRemove, "name")]
    [DataRow(TransformKinds.QueryRouteParameter, "name,routeValue")]
    [DataRow(TransformKinds.RequestHeaderAppend, "name,value")]
    [DataRow(TransformKinds.RequestHeadersAllowed, "headers")]
    [DataRow(TransformKinds.RequestHeaderOriginalHost, "")]
    [DataRow(TransformKinds.ResponseHeaderAppend, "name,value")]
    [DataRow(TransformKinds.ResponseHeadersAllowed, "headers")]
    [DataRow(TransformKinds.ClientCertHeader, "name")]
    public void EveryKind_ReportsItsRequiredParameters(string kind, string expectedCsv)
    {
        Assert.IsTrue(TransformKinds.TryGetRequiredParameters(kind, out var required));
        var expected = expectedCsv.Length == 0 ? Array.Empty<string>() : expectedCsv.Split(',');
        CollectionAssert.AreEqual(expected, new List<string>(required));
    }

    [TestMethod]
    public void UnknownKind_IsNotKnown()
    {
        Assert.IsFalse(TransformKinds.TryGetRequiredParameters("Nope", out var required));
        Assert.AreEqual(0, required.Count);
    }
}
