using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Inspector.Services;
using Titanium.Inspector.Views;

namespace Titanium.Inspector.Tests;

[TestClass]
public class CaptureSettingsParityTests
{
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
            CollectionAssert.AreEqual(new[] { "*.corp.example.com", "auth.example.com" }, loaded.DecryptSkipHosts);
            CollectionAssert.AreEqual(new[] { "api.example.com" }, loaded.DecryptOnlyHosts);
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
        CollectionAssert.AreEqual(new[] { "api.example.com", "*.corp.dev" }, list);
        Assert.AreEqual("api.example.com" + Environment.NewLine + "*.corp.dev", HostListFormat.Join(list));
    }

    [TestMethod]
    public void MitmBypass_UserSkipAndOnlyLists()
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

        // Built-in still wins even if on include list
        Assert.IsTrue(MitmBypass.ShouldDisableSslDecrypt(
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
}
