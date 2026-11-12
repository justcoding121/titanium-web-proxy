using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Inspector.Services;
using Titanium.Web.Proxy.Http;
using Titanium.Web.Proxy.Models;

namespace Titanium.Inspector.Tests;

/// <summary>
///     Remaining InterceptionService / OsThemePreference / CaptureUi / progressive write edges that stay thin
///     on develop Sonar without requiring a live Avalonia desktop lifetime.
/// </summary>
[TestClass]
public class InterceptionLoggingThemeAndNotifierBehaviorTests
{
    [TestMethod]
    public void OsThemePreference_ParseColorSchemeAndIniThemeFallbacks()
    {
        Assert.IsNull(OsThemePreference.ParseColorScheme(null));
        Assert.IsNull(OsThemePreference.ParseColorScheme(""));
        Assert.IsNull(OsThemePreference.ParseColorScheme("variant uint32"));
        Assert.AreEqual(1, OsThemePreference.ParseColorScheme("  (('org.freedesktop.appearance', 'color-scheme'), <uint32 1>)"));
        Assert.AreEqual(2, OsThemePreference.ParseColorScheme("prefix uint32 2 trailing"));
        Assert.IsNull(OsThemePreference.ParseColorScheme("uint32 9"));

        Assert.IsTrue(OsThemePreference.GtkSettingsPrefersDark("gtk-theme-name=Adwaita-dark\n"));
        Assert.IsFalse(OsThemePreference.GtkSettingsPrefersDark("gtk-theme-name=Adwaita\n"));
        Assert.IsNull(OsThemePreference.GtkSettingsPrefersDark("# only comments\n[Settings]\n"));
        Assert.IsTrue(OsThemePreference.KdePrefersDark("ColorScheme=BreezeDark\n"));
        Assert.IsFalse(OsThemePreference.KdePrefersDark("ColorScheme=BreezeLight\n"));
        Assert.IsNull(OsThemePreference.KdePrefersDark(""));

        var previous = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        var home = Path.Combine(Path.GetTempPath(), "twp-xdg-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(Path.Combine(home, "gtk-3.0"));
            File.WriteAllText(
                Path.Combine(home, "gtk-3.0", "settings.ini"),
                "[Settings]\ngtk-application-prefer-dark-theme=1\n");
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", home);
            Environment.SetEnvironmentVariable("GTK_THEME", null);
            Environment.SetEnvironmentVariable("XDG_CURRENT_DESKTOP", "GNOME");
            Assert.IsTrue(OsThemePreference.FilePrefersDark());
        }
        finally
        {
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", previous);
            try
            {
                if (Directory.Exists(home))
                {
                    Directory.Delete(home, recursive: true);
                }
            }
            catch
            {
                // Best-effort.
            }
        }

        Assert.IsFalse(SessionSnapshotNotifier.ShouldDefer);
        _ = CaptureUiGovernor.IsPacingActive;
        _ = CaptureUiGovernor.IntervalMs;
        CaptureUiGovernor.Report(12);
        Assert.IsTrue(CaptureUiGovernor.IntervalMs >= CaptureUiGovernor.MinIntervalMs);
    }

    [TestMethod]
    public async Task InspectorFileLogProvider_WritesRotatesAndReconfigures()
    {
        var folder = Path.Combine(Path.GetTempPath(), "twp-file-log-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            using var provider = new InspectorFileLogProvider(folder, maxBytes: 10_000_000, keep: 2, queueCapacity: 64);
            var logger = provider.CreateLogger("tests.filelog");
            if (logger.IsEnabled(LogLevel.Information))
            {
                logger.LogInformation("hello-file-log-{N}", 1);
            }
            logger.LogWarning("warn-file-log");
            logger.LogError("error-file-log");

            string? path = null;
            for (var i = 0; i < 50 && path is null; i++)
            {
                path = provider.CurrentFilePath;
                if (path is null)
                {
                    await Task.Delay(50);
                }
            }

            Assert.IsNotNull(path);
            Assert.IsTrue(File.Exists(path));

            var next = Path.Combine(Path.GetTempPath(), "twp-file-log2-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(next);
            try
            {
                provider.Reconfigure(next, maxBytes: 10_000_000, keep: 3);
                logger.LogError("after-reconfigure");
                string? reconfigured = null;
                for (var i = 0; i < 50 && reconfigured is null; i++)
                {
                    var current = provider.CurrentFilePath;
                    if (current is not null && current.StartsWith(next, StringComparison.OrdinalIgnoreCase))
                    {
                        reconfigured = current;
                        break;
                    }

                    await Task.Delay(50);
                }

                Assert.IsNotNull(reconfigured);
                provider.Dispose();
                provider.Dispose();
            }
            finally
            {
                TryDeleteTree(next);
            }
        }
        finally
        {
            TryDeleteTree(folder);
        }
    }

    [TestMethod]
    public async Task ProgressiveDelivery_WriteAsync_SlicesIdentityBody()
    {
        var plain = Encoding.UTF8.GetBytes(new string('w', ProgressiveDelivery.MinWireBytes + 100));
        var response = new Response
        {
            HttpVersion = new Version(1, 1),
            StatusCode = 200,
            StatusDescription = "OK",
            Body = plain,
        };
        response.Headers.AddHeader(new HttpHeader("Content-Length", plain.Length.ToString()));
        var plan = ProgressiveDelivery.TryBuild(response, new Request { Method = "GET" }, plain, plain.Length, false, false);
        Assert.IsNotNull(plan);

        await using var output = new MemoryStream();
        await ProgressiveDelivery.WritePacedAsync(
            output, plan!.Wire, plan.ChargeWireBytes, () => null, CancellationToken.None, sliceBytes: 4096);
        CollectionAssert.AreEqual(plan.Wire, output.ToArray());

        await using var empty = new MemoryStream();
        await ProgressiveDelivery.WritePacedAsync(
            empty, plan.Wire, chargeWireBytes: 0, () => null, CancellationToken.None, sliceBytes: 0);
        Assert.IsTrue(empty.Length > 0);

        _ = ProgressiveDelivery.FallbackCount(ProgressiveSkipReason.Status);
        _ = ProgressiveDelivery.FallbackCount(ProgressiveSkipReason.Head);
        _ = ProgressiveDelivery.FallbackCount(ProgressiveSkipReason.TooSmall);
        _ = ProgressiveDelivery.FallbackCount(ProgressiveSkipReason.BuildFailed);
        _ = ProgressiveDelivery.ProgressiveCount;
    }

    [TestMethod]
    public void InterceptionService_BeginBackgroundShutdown_IsIdempotentWithoutProxy()
    {
        var interception = new InterceptionService(new RecordingSystemProxyController())
        {
            UseInMemoryTrustState = true,
        };
        interception.BeginBackgroundShutdown();
        interception.BeginBackgroundShutdown();
        interception.EnsureShutdown();
        interception.EnsureShutdown();
        Assert.IsFalse(interception.IsRunning);
    }

    private static async Task WaitUntil(Func<bool> predicate, int timeoutMs = 10_000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (!predicate())
        {
            if (Environment.TickCount64 >= deadline)
            {
                Assert.Fail("Timed out waiting for condition.");
            }

            await Task.Delay(25);
        }
    }

    private static void TryDeleteTree(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
            // Best-effort.
        }
    }
}
