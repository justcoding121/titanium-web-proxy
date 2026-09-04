using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Inspector.Services;
using Titanium.Inspector.Views;

namespace Titanium.Inspector.Tests;

[TestClass]
public class CaptureSettingsParityTests
{
    private static readonly string[] ExpectedSkipHosts = ["*.corp.example.com", "auth.example.com"];
    private static readonly string[] ExpectedOnlyHosts = ["api.example.com"];
    private static readonly string[] ParsedHostList = ["api.example.com", "*.corp.dev"];
    [TestMethod]
    public void SessionRetention_MbConversion_RoundTrips()
    {
        Assert.AreEqual(512, SessionRetentionWindow.BytesToMb(512L * 1024 * 1024));
        Assert.AreEqual(512L * 1024 * 1024, SessionRetentionWindow.MbToBytes(512));
        Assert.IsTrue(SessionRetentionWindow.TryParsePositiveInt("10000", out var n));
        Assert.AreEqual(10000, n);
        Assert.IsFalse(SessionRetentionWindow.TryParsePositiveInt("0", out _));
        Assert.IsFalse(SessionRetentionWindow.TryParsePositiveLong("-1", out _));
    }

    [TestMethod]
    public void Settings_RoundTripsRetentionAndHttpsHostsAndIgnoreCert()
    {
        var path = Path.Combine(Path.GetTempPath(), "twp-capture-settings-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var svc = new SettingsService(path);
            svc.Current.SpillBodiesToDisk = false;
            svc.Current.DiskCacheMaxBytes = SessionRetentionWindow.MbToBytes(256);
            svc.Current.DiskCacheMaxAgeDays = 3;
            svc.Current.MaxSessionsInMemory = 500;
            svc.Current.HotBodySessions = 50;
            svc.Current.MaxCaptureBytesInMemory = SessionRetentionWindow.MbToBytes(128);
            svc.Current.IgnoreServerCertificateErrors = true;
            svc.Current.DecryptSkipHosts = ["*.corp.example.com", "auth.example.com"];
            svc.Current.DecryptOnlyHosts = ["api.example.com"];
            svc.Current.SystemProxyBypassHosts = ["sso.corp.example.com"];
            svc.Current.ProxyLoopback = false;
            svc.Current.WarnedAboutPacReplace = true;
            svc.Current.LoggingEnabled = true;
            svc.Current.LoggingEnableFile = true;
            svc.Current.LoggingMinimumLevel = "Warning";
            svc.Current.LoggingFilePath = @"C:\tmp\inspector.log";
            svc.Save();

            var loaded = new SettingsService(path).Current;
            Assert.IsFalse(loaded.SpillBodiesToDisk);
            Assert.AreEqual(SessionRetentionWindow.MbToBytes(256), loaded.DiskCacheMaxBytes);
            Assert.AreEqual(3, loaded.DiskCacheMaxAgeDays);
            Assert.AreEqual(500, loaded.MaxSessionsInMemory);
            Assert.AreEqual(50, loaded.HotBodySessions);
            Assert.AreEqual(SessionRetentionWindow.MbToBytes(128), loaded.MaxCaptureBytesInMemory);
            Assert.IsTrue(loaded.IgnoreServerCertificateErrors);
            CollectionAssert.AreEqual(ExpectedSkipHosts, loaded.DecryptSkipHosts);
            CollectionAssert.AreEqual(ExpectedOnlyHosts, loaded.DecryptOnlyHosts);
            CollectionAssert.AreEqual(new[] { "sso.corp.example.com" }, loaded.SystemProxyBypassHosts);
            Assert.IsFalse(loaded.ProxyLoopback);
            Assert.IsTrue(loaded.WarnedAboutPacReplace);
            Assert.AreEqual("Warning", loaded.LoggingMinimumLevel);
            Assert.AreEqual(@"C:\tmp\inspector.log", loaded.LoggingFilePath);
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    [TestMethod]
    public void HostListFormat_ParseAndJoin()
    {
        var list = HostListFormat.Parse("api.example.com\n# comment\n*.corp.dev\n\napi.example.com");
        CollectionAssert.AreEqual(ParsedHostList, list);
        Assert.AreEqual("api.example.com" + Environment.NewLine + "*.corp.dev", HostListFormat.Join(list));
    }

    [TestMethod]
    public void MitmBypass_UserSkipAndOnlyLists_ReplaceMode()
    {
        Assert.IsFalse(MitmBypass.ShouldDisableSslDecrypt("api.example.com", null, null));

        Assert.IsTrue(MitmBypass.ShouldDisableSslDecrypt(
            "secret.corp.example.com",
            ["*.corp.example.com"],
            null));

        // Include list: only api.example.com decrypts
        Assert.IsFalse(MitmBypass.ShouldDisableSslDecrypt(
            "api.example.com",
            null,
            ["api.example.com"]));
        Assert.IsTrue(MitmBypass.ShouldDisableSslDecrypt(
            "other.example.com",
            null,
            ["api.example.com"]));

        // Replace: factory identity hosts are not forced when omitted from skip list
        Assert.IsFalse(MitmBypass.ShouldDisableSslDecrypt(
            "login.live.com",
            null,
            ["login.live.com"]));
    }

    [TestMethod]
    public void DiskCache_PrunesByAge_OnWrite()
    {
        var dir = Path.Combine(Path.GetTempPath(), "twp-age-prune-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(dir);
            var stale = Path.Combine(dir, "1.bin");
            File.WriteAllBytes(stale, [1, 2, 3, 4]);
            File.SetLastWriteTimeUtc(stale, DateTime.UtcNow.AddDays(-10));

            using var cache = new SessionBodyDiskCache(dir, maxBytes: 64L * 1024 * 1024, maxAge: TimeSpan.FromDays(2));
            Assert.IsFalse(File.Exists(stale), "Startup prune should remove aged files");

            // Recreate stale after construction, then Write should prune it mid-run.
            File.WriteAllBytes(stale, [9, 9, 9]);
            File.SetLastWriteTimeUtc(stale, DateTime.UtcNow.AddDays(-10));

            cache.Write(new SessionSnapshot
            {
                Id = 2,
                ResponseBodyBytes = [5, 6, 7],
            });

            Assert.IsFalse(File.Exists(stale), "Write should prune aged files");
            Assert.IsTrue(File.Exists(Path.Combine(dir, "2.bin")));
        }
        finally
        {
            try
            {
                if (Directory.Exists(dir))
                {
                    Directory.Delete(dir, recursive: true);
                }
            }
            catch
            {
                // ignore
            }
        }
    }

    [TestMethod]
    public void IgnoreServerCertificateErrors_PersistsViaViewModel()
    {
        var path = Path.Combine(Path.GetTempPath(), "twp-ignore-cert-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var settings = new SettingsService(path);
            var registry = new SessionRegistry();
            var vm = new ViewModels.MainWindowViewModel(
                new SessionStreamBuffer(registry),
                registry,
                new UpdateService(settings),
                settings,
                new InterceptionService(new RecordingSystemProxyController()));

            Assert.IsFalse(vm.IgnoreServerCertificateErrors);
            vm.IgnoreServerCertificateErrors = true;
            Assert.IsTrue(settings.Current.IgnoreServerCertificateErrors);

            var loaded = new SettingsService(path);
            Assert.IsTrue(loaded.Current.IgnoreServerCertificateErrors);
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    [TestMethod]
    public async Task ResetSettings_RestoresFactoryDefaults_WithoutClearingSessions()
    {
        var path = Path.Combine(Path.GetTempPath(), "twp-reset-settings-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var settings = new SettingsService(path);
            settings.Current.BindPort = 9999;
            settings.Current.IgnoreServerCertificateErrors = true;
            settings.Current.DecryptHttps = true;
            settings.Current.DecryptSkipHosts = ["*.corp.example.com"];
            settings.Current.MaxSessionsInMemory = 42;
            settings.Save();

            var dialogs = new ScriptedInspectorDialogs { ResetSettingsResult = true };
            var registry = new SessionRegistry();
            registry.Add(new SessionSnapshot
            {
                Id = 1,
                Method = "GET",
                Url = "https://example.com/",
                Host = "example.com",
            });
            using var interception = new InterceptionService(new RecordingSystemProxyController());
            var vm = new ViewModels.MainWindowViewModel(
                new SessionStreamBuffer(registry),
                registry,
                new UpdateService(settings),
                settings,
                interception,
                dialogs);

            Assert.AreEqual(9999, vm.BindPort);
            Assert.IsTrue(vm.IgnoreServerCertificateErrors);
            Assert.AreEqual(1, registry.VisibleSessions.Count);

            vm.ResetSettingsCommand.Execute(null);
            await Task.Delay(150);

            Assert.AreEqual(1, dialogs.ResetSettingsCalls);
            Assert.AreEqual(8866, vm.BindPort);
            Assert.IsFalse(vm.IgnoreServerCertificateErrors);
            Assert.IsFalse(vm.DecryptHttps);
            Assert.AreEqual(0, settings.Current.DecryptSkipHosts.Count);
            Assert.AreEqual(10_000, settings.Current.MaxSessionsInMemory);
            Assert.AreEqual(1, registry.VisibleSessions.Count);
            StringAssert.Contains(vm.StatusText, "Root CA and sessions were not changed");

            var reloaded = new SettingsService(path).Current;
            Assert.AreEqual(8866, reloaded.BindPort);
            Assert.IsFalse(reloaded.IgnoreServerCertificateErrors);
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    [TestMethod]
    public async Task ResetSettings_Cancelled_LeavesSettingsUnchanged()
    {
        var path = Path.Combine(Path.GetTempPath(), "twp-reset-cancel-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var settings = new SettingsService(path);
            settings.Current.BindPort = 7777;
            settings.Save();

            var dialogs = new ScriptedInspectorDialogs { ResetSettingsResult = false };
            var registry = new SessionRegistry();
            using var interception = new InterceptionService(new RecordingSystemProxyController());
            var vm = new ViewModels.MainWindowViewModel(
                new SessionStreamBuffer(registry),
                registry,
                new UpdateService(settings),
                settings,
                interception,
                dialogs);

            vm.ResetSettingsCommand.Execute(null);
            await Task.Delay(150);

            Assert.AreEqual(7777, vm.BindPort);
            StringAssert.Contains(vm.StatusText, "Reset settings cancelled");
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    [TestMethod]
    public void DiskCache_RoundTrip_DeleteMany_ClearAll_EnforcesBudget()
    {
        var dir = Path.Combine(Path.GetTempPath(), "twp-disk-cov-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(dir);
            using var cache = new SessionBodyDiskCache(dir, maxBytes: 10485760, maxAge: TimeSpan.FromDays(1));

            cache.Write(new SessionSnapshot
            {
                Id = 10,
                RequestBodyBytes = [1, 2, 3, 4, 5, 6, 7, 8],
                ResponseBodyBytes = [9, 10],
                RequestBodyText = "req",
                ResponseBodyText = "resp",
            });
            cache.Write(new SessionSnapshot
            {
                Id = 11,
                ResponseBodyBytes = new byte[64],
            });
            cache.Write(new SessionSnapshot
            {
                Id = 12,
                ResponseBodyBytes = new byte[64],
            });

            var loaded = new SessionSnapshot { Id = 10 };
            Assert.IsTrue(cache.TryLoad(loaded));
            CollectionAssert.AreEqual(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }, loaded.RequestBodyBytes);
            Assert.AreEqual("req", loaded.RequestBodyText);
            Assert.AreEqual("resp", loaded.ResponseBodyText);

            cache.DeleteMany([10, 11]);
            Assert.IsFalse(File.Exists(cache.PathFor(10)));
            Assert.IsFalse(File.Exists(cache.PathFor(11)));

            // Corrupt magic should fail load without throwing.
            File.WriteAllBytes(cache.PathFor(99), "XXXX"u8.ToArray());
            Assert.IsFalse(cache.TryLoad(new SessionSnapshot { Id = 99 }));

            cache.ClearAll();
            Assert.AreEqual(0, Directory.EnumerateFiles(dir, "*.bin").Count());

            cache.Delete(12345); // missing id — no throw
            cache.Dispose();
            Assert.ThrowsExactly<ObjectDisposedException>(() =>
                cache.Write(new SessionSnapshot { Id = 1, ResponseBodyBytes = [1] }));
        }
        finally
        {
            try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); } catch { }
        }
    }
[TestMethod]
    public void DiskCache_ClearAll_OnEmptyDirectory_IsNoOp()
    {
        var dir = Path.Combine(Path.GetTempPath(), "twp-disk-empty-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(dir);
            using var cache = new SessionBodyDiskCache(dir, maxBytes: 1024, maxAge: TimeSpan.FromDays(1));
            cache.ClearAll();
            cache.DeleteMany(Array.Empty<long>());
            cache.DeleteMany([42, 43]);
            Assert.IsFalse(cache.TryLoad(new SessionSnapshot { Id = 42 }));
        }
        finally
        {
            try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); } catch { }
        }
    }
[TestMethod]
    public void FormatRotateCaStatusHelpers_CoverChangedAndTrustedBranches()
    {
        var install = typeof(ViewModels.MainWindowViewModel).GetMethod("FormatRotateCaInstallStatus",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!;
        var deferred = typeof(ViewModels.MainWindowViewModel).GetMethod("FormatRotateCaDeferredTrustStatus",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!;

        Assert.IsTrue(((string)install.Invoke(null, [false, true])!).Contains("trust failed"));
        Assert.IsTrue(((string)install.Invoke(null, [true, true])!).Contains("reinstalled"));
        Assert.IsTrue(((string)install.Invoke(null, [true, false])!).Contains("recreate completed"));
        Assert.IsTrue(((string)deferred.Invoke(null, [true])!).Contains("Install root CA"));
        Assert.IsTrue(((string)deferred.Invoke(null, [false])!).Contains("recreate completed"));
    }
}