using System.Linq;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Inspector.Services;

namespace Titanium.Inspector.Tests;

/// <summary>
///     Avalonia-safe leftovers toward 85%: SessionRawHttp body cap truncation, SessionSearch
///     tunnel/websocket arms, and retention-aware count text with an empty filter match.
/// </summary>
[TestClass]
public class InspectorInterceptionRawHttpSoftCoverageWave20BehaviorTests
{
    private static readonly List<SessionSnapshot> TunnelSample =
    [
        new()
        {
            Id = 1,
            Method = "CONNECT",
            Url = "https://tunnel.example:443",
            Host = "tunnel.example",
            IsTunnel = true,
        },
        new()
        {
            Id = 2,
            Method = "GET",
            Url = "wss://live.example/ws",
            Host = "live.example",
            IsWebSocket = true,
            Protocol = "WebSocket",
        },
    ];

    [TestMethod]
    public void SessionRawHttp_SmallBodyCap_TruncatesRequestBody()
    {
        using var output = new MemoryStream();
        var snap = new SessionSnapshot
        {
            Method = "POST",
            Url = "https://cap.example/upload",
            RequestBodyText = "abcdefghij",
            StatusCode = 200,
        };
        var result = SessionRawHttp.Write(snap, output, maxBodyBytes: 4);
        Assert.IsTrue(result.Truncated);
        var text = Encoding.UTF8.GetString(output.ToArray());
        StringAssert.Contains(text, "POST /");
        Assert.IsFalse(text.Contains("ghij", StringComparison.Ordinal));
    }

    [TestMethod]
    public void SessionSearch_IsTunnelAndWebSocket_Filters()
    {
        Assert.AreEqual(1, SessionSearch.Filter(TunnelSample, "is:tunnel").Count());
        Assert.AreEqual(1, SessionSearch.Filter(TunnelSample, "is:websocket").Count());
        Assert.AreEqual(1, SessionSearch.Filter(TunnelSample, "protocol:websocket").Count());
    }

    [TestMethod]
    public void BuildSessionCountText_EmptyFilterMatch_IncludesRetentionHint()
    {
        var now = DateTimeOffset.UtcNow;
        var text = SessionSearch.BuildSessionCountText(
            visibleCount: 0,
            totalCount: 12,
            searchQuery: "host:missing.example",
            retentionEvictedTotal: 48,
            oldestStartedUtc: now.AddMinutes(-3),
            nowUtc: now);
        StringAssert.Contains(text, "0 of 12 most recent match filter");
        StringAssert.Contains(text, "older removed by retention");
    }
}
