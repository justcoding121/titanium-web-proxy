using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Http;
using Titanium.Web.Proxy.Models;

namespace Titanium.Web.Proxy.UnitTests;

[TestClass]
public class WebSocketUpgradeRequestTests
{
    [TestMethod]
    public void BuildRequest_FiltersHopByHopAndPseudoHeaders()
    {
        var headers = new HeaderCollection();
        headers.AddHeader("Connection", "keep-alive");
        headers.AddHeader("Upgrade", "websocket");
        headers.AddHeader("Sec-WebSocket-Key", "ignored");
        headers.AddHeader("Origin", "https://example.com");
        headers.AddHeader("X-Custom", "yes");

        var bytes = WebSocketUpgradeRequest.BuildRequest("/chat", "example.com", headers, out var key);
        var text = Encoding.ASCII.GetString(bytes);

        StringAssert.Contains(text, "GET /chat HTTP/1.1");
        StringAssert.Contains(text, "Origin: https://example.com");
        StringAssert.Contains(text, "X-Custom: yes");
        StringAssert.Contains(text, $"Sec-WebSocket-Key: {key}");
        Assert.IsFalse(text.Contains("Connection: keep-alive", StringComparison.Ordinal));
        Assert.IsFalse(text.Contains("ignored", StringComparison.Ordinal));
    }

    [TestMethod]
    public void BuildRequest_GeneratesUniqueKeys()
    {
        var headers = new HeaderCollection();
        var a = WebSocketUpgradeRequest.BuildRequest("/", "h", headers, out var keyA);
        var b = WebSocketUpgradeRequest.BuildRequest("/", "h", headers, out var keyB);
        Assert.AreNotEqual(keyA, keyB);
        Assert.IsTrue(a.Length > 0 && b.Length > 0);
    }

    [TestMethod]
    public void IsValid101_RejectsWrongAccept()
    {
        var headers = new List<HttpHeader>
        {
            new("Upgrade", "websocket"),
            new("Connection", "Upgrade"),
            new("Sec-WebSocket-Accept", "wrong")
        };
        Assert.IsFalse(WebSocketUpgradeRequest.IsValid101(101, headers, "dGhlIHNhbXBsZSBub25jZQ=="));
    }

    [TestMethod]
    public void IsValid101_AcceptsExtraHeaders()
    {
        const string key = "dGhlIHNhbXBsZSBub25jZQ==";
        var accept = WebSocketHandshake.ComputeAccept(key);
        var headers = new List<HttpHeader>
        {
            new("Upgrade", "websocket"),
            new("Connection", "Upgrade"),
            new("Sec-WebSocket-Accept", accept),
            new("Sec-WebSocket-Protocol", "chat")
        };
        Assert.IsTrue(WebSocketUpgradeRequest.IsValid101(101, headers, key));
    }

    [TestMethod]
    public void TryParseResponse_ReturnsNon101Status()
    {
        var head = Encoding.ASCII.GetBytes("HTTP/1.1 403 Forbidden\r\nContent-Length: 0\r\n\r\n");
        Assert.IsTrue(WebSocketUpgradeRequest.TryParseResponse(head, "key", out var status, out _, out _));
        Assert.AreEqual(403, status);
    }

    [TestMethod]
    public void TryParseResponse_TruncatedHead_ReturnsFalse()
    {
        var head = Encoding.ASCII.GetBytes("HTTP/1.1 101 Switching");
        Assert.IsFalse(WebSocketUpgradeRequest.TryParseResponse(head, "key", out _, out _, out _));
    }
}
