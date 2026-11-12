using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Inspector.Services;
using Titanium.Inspector.ViewModels;

namespace Titanium.Inspector.Tests;

/// <summary>
///     Avalonia-safe leftovers toward 85%: InterceptionService protocol prefs,
///     SessionRawHttp CONNECT tunnel lines, SessionBodyDiskCache generation churn,
///     and MainWindowViewModel status-class tokens.
/// </summary>
[TestClass]
public class InspectorInterceptionRawHttpSoftCoverageWave25BehaviorTests
{
    [TestMethod]
    public void InterceptionService_PreferHttp2AndTlsProtocols_NoThrow()
    {
        using var interception = new InterceptionService(new RecordingSystemProxyController())
        {
            UseInMemoryTrustState = true,
            PreferHttp2 = true,
            PreferHttp3 = false,
        };
        interception.ApplyHttpProtocols();
        Assert.IsTrue(interception.Http2Enabled);
        interception.OutboundTlsProtocols = System.Security.Authentication.SslProtocols.Tls12;
        interception.ApplyHttpProtocols();
    }

    [TestMethod]
    public void SessionRawHttp_ConnectTunnel_WritesAuthorityLine()
    {
        using var output = new MemoryStream();
        var snap = new SessionSnapshot
        {
            Method = "CONNECT",
            Url = "tunnel.example:443",
            Host = "tunnel.example",
            StatusCode = 200,
            IsTunnel = true,
            RequestHeadersText = "Host: tunnel.example:443\r\n",
        };
        _ = SessionRawHttp.Write(snap, output);
        var text = Encoding.UTF8.GetString(output.ToArray());
        StringAssert.Contains(text, "CONNECT tunnel.example:443");
        StringAssert.Contains(text, "HTTP/1.1 200");
    }

    [TestMethod]
    public void SessionBodyDiskCache_WriteMany_IncrementsStats()
    {
        var root = Path.Combine(Path.GetTempPath(), "twp-gen-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var cache = new SessionBodyDiskCache(root, maxBytes: 200_000, maxAge: TimeSpan.FromDays(1));
            cache.Write(new SessionSnapshot
            {
                Id = 1,
                Method = "GET",
                Url = "https://gen.example/one",
                RequestBodyText = "a",
                ResponseBodyText = "b",
                StatusCode = 200,
            });
            var stats = cache.GetCacheStats();
            cache.Write(new SessionSnapshot
            {
                Id = 2,
                Method = "GET",
                Url = "https://gen.example/two",
                RequestBodyText = "c",
                ResponseBodyText = "d",
                StatusCode = 200,
            });
            var stats2 = cache.GetCacheStats();
            Assert.IsTrue(stats2.TotalBytes >= stats.TotalBytes);
            Assert.IsTrue(cache.FileExists(1));
            Assert.IsTrue(cache.FileExists(2));
        }
        finally
        {
            TryDeleteTree(root);
        }
    }

    [TestMethod]
    public async Task MainWindowViewModel_StatusClass2xxAnd4xx_WritesSearchTokens()
    {
        var path = Path.Combine(Path.GetTempPath(), "twp-wave25-" + Guid.NewGuid().ToString("N") + ".json");
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

            vm.StatusClass2xx = true;
            StringAssert.Contains(vm.SearchQuery, "status:2xx");
            vm.StatusClass4xx = true;
            StringAssert.Contains(vm.SearchQuery, "4xx");
            vm.StatusClass2xx = false;
            Assert.IsFalse(vm.SearchQuery.Contains("status:2xx", StringComparison.Ordinal));
        }
        finally
        {
            TryDelete(path);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (IOException)
        {
        }
    }

    private static void TryDeleteTree(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
