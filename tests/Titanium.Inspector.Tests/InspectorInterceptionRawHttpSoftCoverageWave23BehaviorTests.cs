using System.Linq;
using System.Security.Authentication;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Inspector.Services;

namespace Titanium.Inspector.Tests;

/// <summary>
///     Avalonia-safe leftovers toward 85%: SessionRawHttp request-only and reason phrases,
///     SessionSearch negative host/method arms, InterceptionService decrypt host lists before start,
///     and SessionBodyDiskCache PathFor after write.
/// </summary>
[TestClass]
public class InspectorInterceptionRawHttpSoftCoverageWave23BehaviorTests
{
    private static readonly List<SessionSnapshot> Sample =
    [
        new()
        {
            Id = 1,
            Method = "GET",
            Url = "https://keep.example/ok",
            Host = "keep.example",
            StatusCode = 200,
        },
        new()
        {
            Id = 2,
            Method = "POST",
            Url = "https://drop.example/submit",
            Host = "drop.example",
            StatusCode = 201,
        },
    ];

    [TestMethod]
    public void SessionRawHttp_RequestOnly_OmitsResponseBlock()
    {
        using var output = new MemoryStream();
        var snap = new SessionSnapshot
        {
            Method = "GET",
            Url = "https://req-only.example/path",
            RequestHeadersText = "Accept: */*\r\n",
        };
        var result = SessionRawHttp.Write(snap, output);
        Assert.IsFalse(result.Truncated);
        var text = Encoding.UTF8.GetString(output.ToArray());
        StringAssert.Contains(text, "GET /path HTTP/1.1");
        Assert.IsFalse(text.Contains("HTTP/1.1 200", StringComparison.Ordinal));
    }

    [TestMethod]
    public void SessionRawHttp_Status404_IncludesNotFoundReason()
    {
        using var output = new MemoryStream();
        var snap = new SessionSnapshot
        {
            Method = "GET",
            Url = "https://missing.example/x",
            StatusCode = 404,
        };
        SessionRawHttp.Write(snap, output);
        var text = Encoding.UTF8.GetString(output.ToArray());
        StringAssert.Contains(text, "HTTP/1.1 404 Not Found\r\n");
    }

    [TestMethod]
    public void SessionSearch_NegativeHostAndMethod_Filters()
    {
        Assert.AreEqual(1, SessionSearch.Filter(Sample, "-host:drop.example").Count());
        Assert.AreEqual(1, SessionSearch.Filter(Sample, "-method:POST").Count());
        Assert.IsTrue(SessionSearch.Matches(Sample[0], "-method:POST"));
        Assert.IsFalse(SessionSearch.Matches(Sample[1], "-host:drop.example"));
    }

    [TestMethod]
    public void InterceptionService_DecryptListsAndTls_BeforeProxyStart()
    {
        using var service = new InterceptionService(new RecordingSystemProxyController())
        {
            UseInMemoryTrustState = true,
            DecryptHttps = true,
            ProxyLoopback = false,
            ReuseServerConnections = false,
            OutboundTlsProtocols = SslProtocols.Tls12,
        };
        service.DecryptSkipHosts = ["skip.example"];
        service.DecryptOnlyHosts = ["only.example"];
        service.ApplyDecryptFailureBypassSetting();
        service.ApplyHttpProtocols();
        Assert.IsTrue(service.DecryptHttps);
        CollectionAssert.Contains(service.DecryptSkipHosts.ToList(), "skip.example");
        CollectionAssert.Contains(service.DecryptOnlyHosts.ToList(), "only.example");
    }

    [TestMethod]
    public void SessionBodyDiskCache_PathFor_ReturnsRunScopedHar()
    {
        var root = Path.Combine(Path.GetTempPath(), "twp-wave23-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var cache = new SessionBodyDiskCache(root, maxBytes: 500_000, maxAge: TimeSpan.FromDays(1));
            cache.Write(new SessionSnapshot
            {
                Id = 7,
                Method = "GET",
                Url = "https://path.example/",
                RequestBodyText = "q",
            });
            var path = cache.PathFor(7);
            Assert.IsTrue(File.Exists(path));
            StringAssert.Contains(path, root);
            StringAssert.EndsWith(path, "7.har");
        }
        finally
        {
            TryDeleteTree(root);
        }
    }

    private static void TryDeleteTree(string root)
    {
        try
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
        catch (IOException)
        {
        }
    }
}
