#pragma warning disable CA1416
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Http;
using Titanium.Web.Proxy.Http3;
using Titanium.Web.Proxy.Models;

namespace Titanium.Web.Proxy.UnitTests;

[TestClass]
public class Http3OriginBridgeCoverageTests
{
    private static MethodInfo BridgeMethod(string name) =>
        typeof(Http3OriginBridge).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException($"HTTP/3 bridge method {name} was not found.");

    [TestMethod]
    public void BuildRequestHeaders_EmitsPseudoHeaders_StripsHopByHop_AndLowercases()
    {
        var request = new Request
        {
            Method = "POST",
            IsHttps = true,
            RequestUriString = "https://example.com:8443/path?q=1"
        };
        request.Headers.AddHeader("Connection", "close");
        request.Headers.AddHeader("Host", "wrong.example");
        request.Headers.AddHeader("Proxy-Authorization", "secret");
        request.Headers.AddHeader("X-Mixed", "kept");

        var headers = (List<(string Name, string Value)>)BridgeMethod("BuildRequestHeaders")
            .Invoke(null, [request, "fallback.example"])!;

        CollectionAssert.AreEqual(
            new[]
            {
                (":method", "POST"), (":scheme", "https"),
                (":authority", "example.com:8443"), (":path", "/path?q=1")
            },
            headers.Take(4).ToArray());
        Assert.IsFalse(headers.Any(h => h.Name is "connection" or "host" or "proxy-authorization"));
        Assert.IsTrue(headers.Contains(("x-mixed", "kept")));
    }

    [TestMethod]
    public void BuildRequestHeaders_HttpRoot_UsesHttpSchemeAndRootPath()
    {
        var request = new Request
        {
            Method = "OPTIONS",
            IsHttps = false,
            RequestUriString = "http://plain.example/"
        };

        var headers = (List<(string Name, string Value)>)BridgeMethod("BuildRequestHeaders")
            .Invoke(null, [request, "fallback.example"])!;

        Assert.IsTrue(headers.Contains((":scheme", "http")));
        Assert.IsTrue(headers.Contains((":authority", "plain.example")));
        Assert.IsTrue(headers.Contains((":path", "/")));
    }

    [TestMethod]
    public void ResponseHeaderHelpers_ParseStatus_IgnoreUnknownPseudo_AndKeepRegular()
    {
        var fields = new List<(string Name, string Value)>
        {
            (":unknown", "ignored"), (":status", "204"), ("x-origin", "yes")
        };

        var status = (int)BridgeMethod("ParseStatusCode").Invoke(null, [fields])!;
        var response = (Response)BridgeMethod("BuildResponseFromHeaders")
            .Invoke(null, [fields, HttpHeader.Version30])!;

        Assert.AreEqual(204, status);
        Assert.AreEqual(204, response.StatusCode);
        Assert.AreEqual(HttpHeader.Version30, response.HttpVersion);
        Assert.AreEqual("yes", response.Headers.GetHeaderValueOrNull("x-origin"));
        Assert.AreEqual(1, response.Headers.Count());
    }

    [TestMethod]
    public void ResponseHeaderHelpers_MissingOrInvalidStatus_ReturnsZero()
    {
        var fields = new List<(string Name, string Value)> { (":status", "not-a-number") };
        Assert.AreEqual(0, BridgeMethod("ParseStatusCode").Invoke(null, [fields]));

        fields.Clear();
        Assert.AreEqual(0, BridgeMethod("ParseStatusCode").Invoke(null, [fields]));
    }

    [TestMethod]
    public void MakeBadGatewayResponse_ContainsSafeHttp3Response()
    {
        var response = (Response)BridgeMethod("MakeBadGatewayResponse").Invoke(null, ["connection failed"])!;

        Assert.AreEqual(502, response.StatusCode);
        Assert.AreEqual(HttpHeader.Version30, response.HttpVersion);
        Assert.IsTrue(response.IsBodyRead);
        StringAssert.Contains(Encoding.UTF8.GetString(response.Body), "connection failed");
    }

    [TestMethod]
    [DataRow((ulong)Http3FrameType.Settings, true)]
    [DataRow((ulong)Http3FrameType.GoAway, true)]
    [DataRow((ulong)Http3FrameType.MaxPushId, true)]
    [DataRow((ulong)Http3FrameType.CancelPush, true)]
    [DataRow((ulong)Http3FrameType.Data, false)]
    [DataRow(0x21UL, false)]
    public void IsForbiddenOnRequestStream_ClassifiesFrameTypes(ulong frameType, bool expected)
    {
        var actual = (bool)BridgeMethod("IsForbiddenOnRequestStream").Invoke(null, [frameType])!;
        Assert.AreEqual(expected, actual);
    }

}
#pragma warning restore CA1416
