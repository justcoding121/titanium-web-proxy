using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Http3;

namespace Titanium.Web.Proxy.UnitTests;

/// <summary>
///     Unit coverage for <see cref="Http3OriginCapabilityCache" />.
/// </summary>
[TestClass]
public class Http3OriginCapabilityCacheTests
{
    [TestMethod]
    public void TryGet_OnEmptyCache_ReturnsFalse()
    {
        var cache = new Http3OriginCapabilityCache();

        var found = cache.TryGet("example.com:443", out var altPort);

        Assert.IsFalse(found);
        Assert.AreEqual(int.MinValue, altPort);
    }

    [TestMethod]
    public void Set_ThenTryGet_ReturnsCachedEntry()
    {
        var cache = new Http3OriginCapabilityCache();

        cache.Set("example.com:443");

        var found = cache.TryGet("example.com:443", out var altPort);

        Assert.IsTrue(found);
        Assert.AreEqual(int.MinValue, altPort); // same port
    }

    [TestMethod]
    public void Set_WithAltPort_ReturnsCachedAltPort()
    {
        var cache = new Http3OriginCapabilityCache();

        cache.Set("example.com:443", altPort: 8443);

        var found = cache.TryGet("example.com:443", out var altPort);

        Assert.IsTrue(found);
        Assert.AreEqual(8443, altPort);
    }

    [TestMethod]
    public void TryGet_DifferentPort_ReturnsFalse()
    {
        var cache = new Http3OriginCapabilityCache();

        cache.Set("example.com:443");

        var found = cache.TryGet("example.com:8443", out _);

        Assert.IsFalse(found);
    }

    [TestMethod]
    public async Task TryGet_AfterExpiry_ReturnsFalse()
    {
        var cache = new Http3OriginCapabilityCache();

        cache.Set("example.com:443", ttl: TimeSpan.FromMilliseconds(50));
        Assert.IsTrue(cache.TryGet("example.com:443", out _), "Entry should be fresh immediately.");

        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (cache.TryGet("example.com:443", out _) && DateTime.UtcNow < deadline)
            await Task.Delay(10);

        var found = cache.TryGet("example.com:443", out _);

        Assert.IsFalse(found, "Entry should have expired after TTL elapsed.");
    }

    [TestMethod]
    public void Evict_RemovesCachedEntry()
    {
        var cache = new Http3OriginCapabilityCache();

        cache.Set("example.com:443");
        Assert.IsTrue(cache.TryGet("example.com:443", out _));

        cache.Evict("example.com:443");

        Assert.IsFalse(cache.TryGet("example.com:443", out _));
    }

    [TestMethod]
    public void Set_Overwrites_PreviousEntry()
    {
        var cache = new Http3OriginCapabilityCache();

        cache.Set("example.com:443", altPort: 9090);
        cache.Set("example.com:443"); // overwrite with no alt port

        cache.TryGet("example.com:443", out var altPort);
        Assert.AreEqual(int.MinValue, altPort);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // TargetName storage and retrieval
    // ─────────────────────────────────────────────────────────────────────────

    [TestMethod]
    public void TryGet_WithTargetName_ReturnsTargetName()
    {
        var cache = new Http3OriginCapabilityCache();
        cache.Set("example.com:443", altPort: int.MinValue, targetName: "target.cdn.example.com");

        var found = cache.TryGet("example.com:443", out _, out var targetName);

        Assert.IsTrue(found);
        Assert.AreEqual("target.cdn.example.com", targetName);
    }

    [TestMethod]
    public void TryGet_WithoutTargetName_ReturnsNullTargetName()
    {
        var cache = new Http3OriginCapabilityCache();
        cache.Set("example.com:443"); // no targetName

        var found = cache.TryGet("example.com:443", out _, out var targetName);

        Assert.IsTrue(found);
        Assert.IsNull(targetName, "Alt-Svc entries have no TargetName; should return null.");
    }

    [TestMethod]
    public void Set_WithTargetName_Overwrites_ClearsPreviousTargetName()
    {
        var cache = new Http3OriginCapabilityCache();
        cache.Set("example.com:443", targetName: "old-target.example.com");
        cache.Set("example.com:443"); // overwrite without targetName

        cache.TryGet("example.com:443", out _, out var targetName);
        Assert.IsNull(targetName, "Overwriting with no targetName should clear the previous value.");
    }

    [TestMethod]
    public void TwoArgTryGet_Overload_StillWorks()
    {
        // Verify the convenience overload that discards targetName compiles and returns correct altPort.
        var cache = new Http3OriginCapabilityCache();
        cache.Set("example.com:443", altPort: 8443, targetName: "target.example.com");

        var found = cache.TryGet("example.com:443", out var altPort);
        Assert.IsTrue(found);
        Assert.AreEqual(8443, altPort);
    }
}

/// <summary>
///     Unit coverage for <see cref="AltSvcParser" />.
/// </summary>
[TestClass]
public class AltSvcParserTests
{
    [TestMethod]
    public void Parse_NullOrEmpty_ReturnsEmpty()
    {
        Assert.AreEqual(0, AltSvcParser.Parse(null).Count);
        Assert.AreEqual(0, AltSvcParser.Parse("").Count);
        Assert.AreEqual(0, AltSvcParser.Parse("clear").Count);
    }

    [TestMethod]
    public void Parse_SimpleH3_SamePort()
    {
        var results = AltSvcParser.Parse("h3=\":443\"; ma=86400");

        Assert.AreEqual(1, results.Count);
        Assert.AreEqual(443, results[0].Port);
        Assert.AreEqual(86400, results[0].MaxAgeSeconds);
    }

    [TestMethod]
    public void Parse_H3DraftToken_IsAccepted()
    {
        var results = AltSvcParser.Parse("h3-29=\":443\"; ma=3600");

        Assert.AreEqual(1, results.Count);
        Assert.AreEqual(443, results[0].Port);
        Assert.AreEqual(3600, results[0].MaxAgeSeconds);
    }

    [TestMethod]
    public void Parse_MultipleTokens_ReturnsAllH3()
    {
        var results = AltSvcParser.Parse("h3-29=\":443\"; ma=86400, h3=\":443\"; ma=86400");

        Assert.AreEqual(2, results.Count);
    }

    [TestMethod]
    public void Parse_MixedProtocols_OnlyH3Returned()
    {
        var results = AltSvcParser.Parse("h2=\":443\"; ma=86400, h3=\":443\"; ma=3600");

        Assert.AreEqual(1, results.Count);
        Assert.AreEqual(3600, results[0].MaxAgeSeconds);
    }

    [TestMethod]
    public void Parse_AltPortDifferentFromCurrent_ReturnsAltPort()
    {
        var results = AltSvcParser.Parse("h3=\":8443\"; ma=300");

        Assert.AreEqual(1, results.Count);
        Assert.AreEqual(8443, results[0].Port);
    }

    [TestMethod]
    public void Parse_MissingMaParameter_UsesDefaultMaxAge()
    {
        var results = AltSvcParser.Parse("h3=\":443\"");

        Assert.AreEqual(1, results.Count);
        Assert.AreEqual(86400, results[0].MaxAgeSeconds); // default
    }

    [TestMethod]
    public void Parse_NonH3Token_ReturnsEmpty()
    {
        var results = AltSvcParser.Parse("h2=\":443\"; ma=86400");
        Assert.AreEqual(0, results.Count);
    }

    [TestMethod]
    public void Parse_DifferentHostInAuthority_IsIgnored()
    {
        // "other.example.com:443" — different host, should be ignored for security.
        var results = AltSvcParser.Parse("h3=\"other.example.com:443\"; ma=86400");
        Assert.AreEqual(0, results.Count);
    }

    [TestMethod]
    public void Parse_ZeroMaxAge_IsReturnedAsIs()
    {
        var results = AltSvcParser.Parse("h3=\":443\"; ma=0");
        Assert.AreEqual(1, results.Count);
        Assert.AreEqual(0, results[0].MaxAgeSeconds);
    }
}

[TestClass]
public class Http3OriginCapabilityCacheTrimTests
{
    [TestMethod]
    public async Task TrimExpired_RemovesExpiredEntries()
    {
        var cache = new Http3OriginCapabilityCache();
        cache.Set("a.example.com:443", ttl: TimeSpan.FromMilliseconds(50));
        cache.Set("b.example.com:443", altPort: 8443, ttl: TimeSpan.FromMilliseconds(50));

        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (cache.TryGet("a.example.com:443", out _) && DateTime.UtcNow < deadline)
            await Task.Delay(10);

        cache.TrimExpired();

        Assert.IsFalse(cache.TryGet("a.example.com:443", out _), "Expired entry must be removed");
        Assert.IsFalse(cache.TryGet("b.example.com:443", out _), "Expired entry with altPort must be removed");
    }

    [TestMethod]
    public void TrimExpired_PreservesActiveEntries()
    {
        var cache = new Http3OriginCapabilityCache();
        cache.Set("live.example.com:443", ttl: TimeSpan.FromSeconds(60));

        cache.TrimExpired();

        Assert.IsTrue(cache.TryGet("live.example.com:443", out _), "Active entry must survive TrimExpired");
    }
}
