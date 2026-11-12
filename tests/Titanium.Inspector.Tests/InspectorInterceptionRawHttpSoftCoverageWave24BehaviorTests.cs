using System.Linq;
using System.Security.Authentication;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Inspector.Services;
using Titanium.Inspector.ViewModels;

namespace Titanium.Inspector.Tests;

/// <summary>
///     Avalonia-safe leftovers toward 85%: SessionRawHttp truncated spilled HAR, SessionSearch error/mark arms,
///     InterceptionService HTTP/TLS protocol apply, and MainWindowViewModel status-class filters.
/// </summary>
[TestClass]
public class InspectorInterceptionRawHttpSoftCoverageWave24BehaviorTests
{
    private static readonly List<SessionSnapshot> Sample =
    [
        new()
        {
            Id = 1,
            Method = "GET",
            Url = "https://ok.example/",
            Host = "ok.example",
            StatusCode = 200,
            Mark = SessionMark.Blue,
            Comment = "keep",
        },
        new()
        {
            Id = 2,
            Method = "GET",
            Url = "https://bad.example/",
            Host = "bad.example",
            StatusCode = 503,
        },
    ];

    [TestMethod]
    public void SessionRawHttp_SpilledHar_MissingText_LeavesResponseBodyEmpty()
    {
        var harJson = "{\"log\":{\"entries\":[{\"response\":{\"content\":{\"mimeType\":\"text/plain\"}}}]}}";
        using var har = new MemoryStream(Encoding.UTF8.GetBytes(harJson));
        using var output = new MemoryStream();
        var snap = new SessionSnapshot
        {
            Method = "GET",
            Url = "https://har.example/",
            StatusCode = 200,
            BodiesOnDisk = true,
        };
        _ = SessionRawHttp.Write(snap, output, spilledHar: har);
        var text = Encoding.UTF8.GetString(output.ToArray());
        StringAssert.Contains(text, "HTTP/1.1 200 OK\r\n");
        Assert.IsTrue(text.EndsWith("\r\n\r\n", StringComparison.Ordinal));
    }

    [TestMethod]
    public void SessionSearch_PidToken_MatchesProcessId()
    {
        var sessions = new List<SessionSnapshot>
        {
            new() { Id = 1, ProcessId = 4242, Url = "https://pid.example/", Method = "GET" },
            new() { Id = 2, ProcessId = 99, Url = "https://other.example/", Method = "GET" },
        };
        Assert.AreEqual(1, SessionSearch.Filter(sessions, "pid:4242").Count());
        Assert.IsTrue(SessionSearch.Matches(sessions[0], "pid:4242"));
        Assert.IsFalse(SessionSearch.Matches(sessions[1], "pid:4242"));
    }

    [TestMethod]
    public void SessionSearch_ErrorMarkAndComment_Filters()
    {
        Assert.AreEqual(1, SessionSearch.Filter(Sample, SessionSearch.ErrorsToken).Count());
        Assert.AreEqual(1, SessionSearch.Filter(Sample, "mark:blue").Count());
        Assert.AreEqual(1, SessionSearch.Filter(Sample, "comment:keep").Count());
        Assert.IsTrue(SessionSearch.Matches(Sample[0], "mark:blue"));
        Assert.IsTrue(SessionSearch.Matches(Sample[1], SessionSearch.ErrorsToken));
        Assert.IsFalse(SessionSearch.Matches(Sample[0], SessionSearch.ErrorsToken));
    }

    [TestMethod]
    public void InterceptionService_Http2AndTlsProtocols_BeforeStart()
    {
        using var service = new InterceptionService(new RecordingSystemProxyController())
        {
            UseInMemoryTrustState = true,
            PreferHttp2 = false,
            PreferHttp3 = false,
            OutboundTlsProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
        };
        service.ApplyHttpProtocols();
        Assert.IsFalse(service.Http2Enabled);
        Assert.IsFalse(service.PreferHttp2);
    }

    [TestMethod]
    public void MainWindowViewModel_StatusClass_WritesSearchTokens()
    {
        var path = Path.Combine(Path.GetTempPath(), "twp-wave24-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var settings = new SettingsService(path);
            settings.Current.AutoStartCapture = false;
            settings.Save();
            var registry = new SessionRegistry(new SessionStoreOptions { SpillBodiesToDisk = false });
            var vm = new MainWindowViewModel(
                new SessionStreamBuffer(registry),
                registry,
                new UpdateService(settings),
                settings,
                new InterceptionService(new RecordingSystemProxyController()) { UseInMemoryTrustState = true });

            vm.StatusClass5xx = true;
            StringAssert.Contains(vm.SearchQuery, "status:5xx");
            vm.StatusClass5xx = false;
            Assert.IsFalse(vm.SearchQuery.Contains("status:5xx", StringComparison.Ordinal));
        }
        finally
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch (IOException)
            {
            }
        }
    }
}
