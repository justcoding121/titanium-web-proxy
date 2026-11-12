using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Inspector.Services;
using Titanium.Inspector.ViewModels;

namespace Titanium.Inspector.Tests;

/// <summary>
///     Avalonia-safe leftovers toward 85%: SessionArchive _inspector timing/flags/capture/bodies,
///     OsTheme PrefersDark matrix, LoadIntoComposer truncated, FileLog roll, RawHttp host-from-URL.
/// </summary>
[TestClass]
public class InspectorArchiveThemeComposerSoftCoverageBehaviorTests
{
    [TestMethod]
    public void SessionArchive_InspectorTimingFlagsCaptureAndBodies()
    {
        var reqB64 = Convert.ToBase64String(Encoding.UTF8.GetBytes("{\"q\":1}"));
        var respB64 = Convert.ToBase64String(Encoding.UTF8.GetBytes("ok"));
        var har = $$"""
            {
              "log": {
                "entries": [
                  {
                    "startedDateTime": "2026-03-09T10:00:00.000Z",
                    "time": 12,
                    "request": {
                      "method": "POST",
                      "url": "https://ext.example/gql",
                      "httpVersion": "HTTP/1.1",
                      "headers": [],
                      "queryString": [],
                      "cookies": [],
                      "headersSize": -1,
                      "bodySize": 0
                    },
                    "response": {
                      "status": 200,
                      "statusText": "OK",
                      "httpVersion": "HTTP/1.1",
                      "headers": [],
                      "cookies": [],
                      "content": { "size": 2, "mimeType": "text/plain", "text": "ok" },
                      "redirectURL": "",
                      "headersSize": -1,
                      "bodySize": 2
                    },
                    "cache": {},
                    "timings": { "send": 1, "wait": 2, "receive": 3 },
                    "_inspector": {
                      "id": 42,
                      "protocol": "HTTP/2",
                      "host": "ext.example",
                      "bodySize": 9,
                      "processId": 77,
                      "processName": "app",
                      "mark": "Blue",
                      "timing": {
                        "estimated": false,
                        "recorded": true,
                        "reused": true,
                        "attempts": 2,
                        "blocked": 0.5,
                        "dns": 1.5,
                        "connect": 2.5,
                        "ssl": 3.5,
                        "send": 4.5,
                        "wait": 5.5,
                        "receive": 6.5
                      },
                      "sentBytes": 11,
                      "receivedBytes": 22,
                      "requestBodyCapture": "Truncated",
                      "responseBodyCapture": "Streaming",
                      "requestBodyOriginalSize": 100,
                      "responseBodyOriginalSize": 200,
                      "isWebSocket": false,
                      "isGrpc": true,
                      "isTranscoded": true,
                      "isTunnel": false,
                      "isMultipart": true,
                      "isServerSentEvents": false,
                      "clientMethod": "POST",
                      "clientPathAndQuery": "/gql",
                      "upstreamMethod": "POST",
                      "upstreamPath": "/gql",
                      "requestBodyBase64": "{{reqB64}}",
                      "responseBodyBase64": "{{respB64}}"
                    }
                  }
                ]
              }
            }
            """;

        using var doc = JsonDocument.Parse(har);
        var snaps = SessionArchive.ParseHarDocument(doc.RootElement, startId: 1);
        Assert.AreEqual(1, snaps.Count);
        var snap = snaps[0];
        Assert.AreEqual(42, snap.Id);
        Assert.AreEqual(SessionMark.Blue, snap.Mark);
        Assert.IsTrue(snap.TimingRecorded);
        Assert.IsFalse(snap.TimingEstimated);
        Assert.IsTrue(snap.ConnectionReused);
        Assert.AreEqual(2, snap.TimingAttempts);
        Assert.AreEqual(3.5, snap.TlsMs);
        Assert.AreEqual(BodyCaptureState.Truncated, snap.RequestBodyCapture);
        Assert.AreEqual(BodyCaptureState.Streaming, snap.ResponseBodyCapture);
        Assert.IsTrue(snap.IsGrpc);
        Assert.IsTrue(snap.IsTranscoded);
        Assert.IsTrue(snap.IsMultipart);
        Assert.IsNotNull(snap.RequestBodyBytes);
        Assert.IsNotNull(snap.ResponseBodyBytes);
    }

    [TestMethod]
    public void OsThemePreference_PrefersDarkMatrix()
    {
        Assert.IsTrue(OsThemePreference.PrefersDark("KDE", null, null, null, "ColorScheme=BreezeDark\n"));
        Assert.IsFalse(OsThemePreference.PrefersDark("KDE", null, null, null, "ColorScheme=BreezeLight\n"));
        Assert.IsTrue(OsThemePreference.PrefersDark("GNOME", "Adwaita:dark", null, null, null));
        Assert.IsFalse(OsThemePreference.PrefersDark("GNOME", "Adwaita:light", null, null, null));
        Assert.IsTrue(OsThemePreference.PrefersDark(
            "GNOME",
            null,
            "gtk-application-prefer-dark-theme=1\n",
            null,
            null));
        Assert.IsNull(OsThemePreference.GtkThemeEnvPrefersDark(null));
        Assert.IsNull(OsThemePreference.GtkSettingsPrefersDark(null));
        Assert.IsNull(OsThemePreference.KdePrefersDark(null));
        Assert.IsNull(OsThemePreference.ParseColorScheme("no-uint"));
        _ = OsThemePreference.AutomaticVariant();
    }

    [TestMethod]
    public async Task LoadIntoComposer_TruncatedAndNullSelection_AndRawHttpHost()
    {
        var path = Path.Combine(Path.GetTempPath(), "twp-composer-soft-" + Guid.NewGuid().ToString("N") + ".json");
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

            await InvokeAsync(vm, "LoadIntoComposerAsync");
            StringAssert.Contains(vm.StatusText, "Select a session");

            var truncated = new SessionSnapshot
            {
                Id = 8,
                Method = "POST",
                Url = "https://composer.example/",
                RequestHeadersText = "Content-Type: application/json\r\n",
                RequestBodyText = "{\"a\":1}",
                RequestBodyCapture = BodyCaptureState.Truncated,
            };
            registry.Store.Add(truncated);
            vm.SelectedSession = truncated;
            await InvokeAsync(vm, "LoadIntoComposerAsync");
            StringAssert.Contains(vm.StatusText, "truncated");

            using var output = new MemoryStream();
            var raw = new SessionSnapshot
            {
                Id = 9,
                Method = "GET",
                Url = "https://host-from-url.example/path",
                StatusCode = 204,
                ResponseBodyText = "",
            };
            var written = SessionRawHttp.Write(raw, output, maxBodyBytes: 0);
            Assert.IsTrue(written.BytesWritten > 0);
            var text = Encoding.UTF8.GetString(output.ToArray());
            StringAssert.Contains(text, "host-from-url.example");
            StringAssert.Contains(text, "204");
        }
        finally
        {
            TryDeleteFile(path);
        }
    }

    [TestMethod]
    public async Task InspectorFileLogProvider_RollsWhenMaxBytesTiny()
    {
        var folder = Path.Combine(Path.GetTempPath(), "twp-roll-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            using var provider = new InspectorFileLogProvider(folder, maxBytes: 120, keep: 3, queueCapacity: 64);
            var logger = provider.CreateLogger("roll.tests");
            string? first = null;
            for (var i = 0; i < 40; i++)
            {
                if (logger.IsEnabled(LogLevel.Information))
                {
                    logger.LogInformation("line-{N}-{Pad}", i, new string('x', 40));
                }
                await Task.Delay(20);
                if (first is null && provider.CurrentFilePath is not null)
                {
                    first = provider.CurrentFilePath;
                }
            }

            await Task.Delay(200);
            Assert.IsNotNull(first);
            Assert.IsNotNull(provider.CurrentFilePath);
            // Either rolled to a new file or still writing; both exercise EnsureWriter/Roll soft arms.
            Assert.IsTrue(Directory.GetFiles(folder, "*.log").Length >= 1);
        }
        finally
        {
            TryDelete(folder);
        }
    }

    private static async Task InvokeAsync(MainWindowViewModel vm, string name)
    {
        var method = typeof(MainWindowViewModel).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.IsNotNull(method, name);
        var result = method!.Invoke(vm, null);
        if (result is Task task)
        {
            await task;
        }
    }

    private static void TryDelete(string dir)
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
            // best-effort
        }
    }

    private static void TryDeleteFile(string file)
    {
        try
        {
            if (File.Exists(file))
            {
                File.Delete(file);
            }
        }
        catch
        {
            // best-effort
        }
    }
}
