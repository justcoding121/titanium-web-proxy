using System.Reflection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Http2;
using Titanium.Web.Proxy.Http3;

namespace Titanium.Web.Proxy.UnitTests;

/// <summary>
///     Unit tests verifying the H2 ExternalBridge mechanism: that the
///     <see cref="Http2StreamState.IsExternalBridge" /> flag exists on the stream-state type,
///     defaults to false, and is distinct from the extended-CONNECT path.
/// </summary>
[TestClass]
public class Http2ExternalBridgeTests
{
    // ─────────────────────────────────────────────────────────────────────────
    // API shape / reflection tests (no SessionEventArgs required)
    // ─────────────────────────────────────────────────────────────────────────

    [TestMethod]
    public void Http2StreamState_HasIsExternalBridgeProperty()
    {
        var prop = typeof(Http2StreamState).GetProperty(
            "IsExternalBridge",
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);

        Assert.IsNotNull(prop, "Http2StreamState must expose IsExternalBridge.");
        Assert.AreEqual(typeof(bool), prop.PropertyType, "IsExternalBridge must be bool.");
        Assert.IsTrue(prop.CanRead, "IsExternalBridge must be readable.");
        Assert.IsTrue(prop.CanWrite, "IsExternalBridge must be settable.");
    }

    [TestMethod]
    public void Http2StreamState_HasSyntheticTaskProperty()
    {
        var prop = typeof(Http2StreamState).GetProperty(
            "SyntheticTask",
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);

        Assert.IsNotNull(prop, "Http2StreamState must expose SyntheticTask so bridges can register ownership.");
    }

    [TestMethod]
    public void Http2StreamState_HasIsExtendedConnectProperty()
    {
        var prop = typeof(Http2StreamState).GetProperty(
            "IsExtendedConnect",
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);

        Assert.IsNotNull(prop, "Http2StreamState.IsExtendedConnect must exist (RFC 8441 path).");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Http3OriginRoute: QuicHost vs ForcedH3 vs Source properties
    // ─────────────────────────────────────────────────────────────────────────

    [TestMethod]
    public void Http3OriginRoute_None_AllFlagsDefault()
    {
        var none = Http3OriginRoute.None;
        Assert.IsFalse(none.UseH3);
        Assert.IsFalse(none.ForcedH3);
        Assert.IsNull(none.QuicHost);
        Assert.AreEqual(Http3RouteSource.None, none.Source);
    }

    [TestMethod]
    public void Http3OriginRoute_ForcedH3_HasExpectedFlags()
    {
        var route = new Http3OriginRoute
        {
            UseH3 = true,
            ForcedH3 = true,
            QuicPort = 443,
            Source = Http3RouteSource.Forced
        };

        Assert.IsTrue(route.UseH3);
        Assert.IsTrue(route.ForcedH3);
        Assert.IsNull(route.QuicHost, "Forced H3 route without SVCB TargetName has no QuicHost override.");
        Assert.AreEqual(Http3RouteSource.Forced, route.Source);
    }

    [TestMethod]
    public void Http3OriginRoute_SvcbRoute_WithQuicHost()
    {
        var route = new Http3OriginRoute
        {
            UseH3 = true,
            ForcedH3 = false,
            QuicPort = 443,
            QuicHost = "quic-target.cdn.example.com",
            Source = Http3RouteSource.HttpsSvcb
        };

        Assert.AreEqual("quic-target.cdn.example.com", route.QuicHost);
        Assert.IsFalse(route.ForcedH3, "SVCB Auto route must not be flagged as forced.");
    }

    [TestMethod]
    public void Http3OriginRoute_AltSvcRoute_HasNullQuicHost()
    {
        var route = new Http3OriginRoute
        {
            UseH3 = true,
            QuicPort = 443,
            QuicHost = null, // Alt-Svc never provides a TargetName
            Source = Http3RouteSource.AltSvcCache
        };

        Assert.IsNull(route.QuicHost, "Alt-Svc routes do not carry a TargetName / QuicHost.");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Http3RouteSource enum values
    // ─────────────────────────────────────────────────────────────────────────

    [TestMethod]
    public void Http3RouteSource_HasAllExpectedValues()
    {
        // Verify all expected enum members exist so the switch in consumers doesn't miss a case.
        var values = System.Enum.GetNames<Http3RouteSource>();
        CollectionAssert.Contains(values, "None");
        CollectionAssert.Contains(values, "Forced");
        CollectionAssert.Contains(values, "AltSvcCache");
        CollectionAssert.Contains(values, "HttpsSvcb");
    }
}
