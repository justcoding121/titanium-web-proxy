using System.Linq;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Inspector.Services;

namespace Titanium.Inspector.Tests;

/// <summary>
///     Avalonia-safe leftovers toward 85%: SessionSearch negative bare-word and status list arms,
///     and SessionRawHttp response-body truncation under a cap.
/// </summary>
[TestClass]
public class InspectorInterceptionRawHttpSoftCoverageWave21BehaviorTests
{
    private static readonly List<SessionSnapshot> Sample =
    [
        new()
        {
            Id = 1,
            Method = "GET",
            Url = "https://api.example/one",
            Host = "api.example",
            StatusCode = 404,
        },
        new()
        {
            Id = 2,
            Method = "GET",
            Url = "https://api.example/two",
            Host = "api.example",
            StatusCode = 200,
        },
    ];

    [TestMethod]
    public void SessionSearch_NegativeStatus_AndStatusList()
    {
        Assert.AreEqual(1, SessionSearch.Filter(Sample, "-status:200").Count());
        Assert.AreEqual(2, SessionSearch.Filter(Sample, "status:404,200").Count());
        Assert.IsTrue(SessionSearch.Matches(Sample[0], "status:4xx"));
        Assert.IsFalse(SessionSearch.Matches(Sample[1], "status:4xx"));
    }

    [TestMethod]
    public void SessionRawHttp_ResponseBodyCap_Truncates()
    {
        using var output = new MemoryStream();
        var snap = new SessionSnapshot
        {
            Method = "GET",
            Url = "https://cap.example/asset",
            StatusCode = 200,
            ResponseBodyText = "abcdefgh",
        };
        var result = SessionRawHttp.Write(snap, output, maxBodyBytes: 3);
        Assert.IsTrue(result.Truncated);
        var text = Encoding.UTF8.GetString(output.ToArray());
        StringAssert.Contains(text, "HTTP/1.1 200");
        Assert.IsFalse(text.Contains("defgh", StringComparison.Ordinal));
    }
}
