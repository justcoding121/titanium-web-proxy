using System.Text;
using Avalonia.Styling;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Inspector.Services;

namespace Titanium.Inspector.Tests;

/// <summary>
///     Avalonia-safe Inspector leftovers: GTK4/KDE theme files, SessionRawHttp non-seekable and HAR spill,
///     and ScriptedInspectorDialogs offer-kind arms.
/// </summary>
[TestClass]
public class InspectorThemeRawHttpAndDialogCoverageBehaviorTests
{
    [TestMethod]
    public void OsThemePreference_Gtk4KdeAndYesPreferDark()
    {
        Assert.IsTrue(OsThemePreference.GtkSettingsPrefersDark("gtk-application-prefer-dark-theme=yes\n"));
        Assert.IsTrue(OsThemePreference.GtkSettingsPrefersDark("gtk-application-prefer-dark-theme=true\n"));
        Assert.IsTrue(OsThemePreference.GtkThemeEnvPrefersDark("Something-dark"));
        Assert.IsFalse(OsThemePreference.GtkThemeEnvPrefersDark("Adwaita:light"));
        Assert.IsNull(OsThemePreference.GtkThemeEnvPrefersDark("Adwaita"));

        var previousHome = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        var previousDesktop = Environment.GetEnvironmentVariable("XDG_CURRENT_DESKTOP");
        var previousGtk = Environment.GetEnvironmentVariable("GTK_THEME");
        var home = Path.Combine(Path.GetTempPath(), "twp-theme-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(Path.Combine(home, "gtk-4.0"));
            File.WriteAllText(
                Path.Combine(home, "gtk-4.0", "settings.ini"),
                "[Settings]\ngtk-application-prefer-dark-theme=1\n");
            File.WriteAllText(
                Path.Combine(home, "kdeglobals"),
                "[General]\nColorScheme=BreezeDark\n");
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", home);
            Environment.SetEnvironmentVariable("GTK_THEME", null);
            Environment.SetEnvironmentVariable("XDG_CURRENT_DESKTOP", "KDE");
            Assert.IsTrue(OsThemePreference.FilePrefersDark());

            Environment.SetEnvironmentVariable("XDG_CURRENT_DESKTOP", "GNOME");
            Assert.IsTrue(OsThemePreference.FilePrefersDark());

            if (OperatingSystem.IsLinux())
            {
                Assert.AreEqual(ThemeVariant.Dark, OsThemePreference.AutomaticVariant());
            }
            else
            {
                Assert.AreEqual(ThemeVariant.Default, OsThemePreference.AutomaticVariant());
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", previousHome);
            Environment.SetEnvironmentVariable("XDG_CURRENT_DESKTOP", previousDesktop);
            Environment.SetEnvironmentVariable("GTK_THEME", previousGtk);
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
    }

    [TestMethod]
    public void SessionRawHttp_NonSeekable_SpilledHar_AndRequestOnly()
    {
        var requestOnly = new SessionSnapshot
        {
            Method = "post",
            Url = "https://example.test/path?q=1",
            Host = "example.test",
            RequestHeadersText = "Accept: */*\n",
            RequestBodyText = "body",
        };
        using (var output = new NonSeekableMemoryStream())
        {
            var result = SessionRawHttp.Write(requestOnly, output, maxBodyBytes: 0);
            Assert.IsFalse(result.Truncated);
            Assert.AreEqual(0, result.BytesWritten, "forward-only streams must not invent a Length");
            var text = Encoding.UTF8.GetString(output.ToArray());
            StringAssert.StartsWith(text, "POST /path?q=1 HTTP/1.1\r\n");
            StringAssert.Contains(text, "Host: example.test");
            Assert.IsFalse(text.Contains("HTTP/1.1 200", StringComparison.Ordinal));
        }

        var harJson = """
            {
              "log": {
                "entries": [
                  {
                    "request": { "postData": { "text": "from-har-req" } },
                    "response": { "content": { "text": "from-har-resp", "encoding": null } }
                  }
                ]
              }
            }
            """;
        using var har = new MemoryStream(Encoding.UTF8.GetBytes(harJson));
        var spilled = new SessionSnapshot
        {
            Method = "PUT",
            Url = "/spill",
            StatusCode = 201,
            BodiesOnDisk = true,
            RequestHeadersText = "Content-Type: text/plain\r\n",
            ResponseHeadersText = "Content-Type: text/plain\r\n",
        };
        using var seekable = new MemoryStream();
        var spilledResult = SessionRawHttp.Write(spilled, seekable, har, maxBodyBytes: SessionRawHttp.MaxBodyBytes);
        Assert.IsFalse(spilledResult.Truncated);
        var spilledText = Encoding.UTF8.GetString(seekable.ToArray());
        StringAssert.Contains(spilledText, "HTTP/1.1 201 Created");
        StringAssert.Contains(spilledText, "from-har-req");
        StringAssert.Contains(spilledText, "from-har-resp");
    }

    [TestMethod]
    public async Task ScriptedInspectorDialogs_OfferKinds_AndTrustHooks()
    {
        var dialogs = new ScriptedInspectorDialogs
        {
            InstallUpdateResult = true,
            TrustRecoveryResult = TrustRecoveryChoice.Primary,
            DecryptTrustFailedResult = TrustRecoveryChoice.Cancel,
            MacSslTrustWaitResult = MacSslTrustWaitResult.Trusted,
        };

        Assert.IsTrue(await dialogs.ConfirmInstallUpdateAsync(null, "2.0.0", "stable", UpdateOfferKind.Downgrade));
        Assert.AreEqual(UpdateOfferKind.Downgrade, dialogs.LastInstallUpdateOfferKind);
        Assert.IsTrue(await dialogs.ConfirmInstallUpdateAsync(null, "2.0.0", "beta", UpdateOfferKind.ChannelSwitch));
        Assert.AreEqual(UpdateOfferKind.ChannelSwitch, dialogs.LastInstallUpdateOfferKind);

        Assert.AreEqual(TrustRecoveryChoice.Primary, await dialogs.ShowTrustRecoveryAsync(null, null));
        Assert.AreEqual(TrustRecoveryChoice.Cancel, await dialogs.ShowDecryptTrustFailedAsync(null, null));
        Assert.AreEqual(
            MacSslTrustWaitResult.Trusted,
            await dialogs.ShowMacSslTrustWaitAsync(null, () => true, () => { }));
        Assert.AreEqual(1, dialogs.TrustRecoveryCalls);
        Assert.AreEqual(1, dialogs.DecryptTrustFailedCalls);
        Assert.AreEqual(1, dialogs.MacSslTrustWaitCalls);
        Assert.AreEqual(2, dialogs.InstallUpdateCalls);
    }

    private sealed class NonSeekableMemoryStream : MemoryStream
    {
        public override bool CanSeek => false;
    }
}
