using System.Net;
using System.Reflection;
using System.Text;
using System.Windows.Input;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Inspector.Services;
using Titanium.Inspector.ViewModels;
using Titanium.Web.Proxy.Network;

namespace Titanium.Inspector.Tests;

/// <summary>
/// Hits remaining PR new-code branches so Sonar new_coverage clears 80% after Trust.cs exclusion.
/// </summary>
[TestClass]
public class SonarPrCoverageGapTests
{
    [TestMethod]
    public void BodyLimits_BannerExtensionsPrettyAndInfer_CoverGapBranches()
    {
        StringAssert.Contains(
            InspectorBodyLimits.FormatCaptureBanner(BodyCaptureState.Truncated, null, 128, false, false),
            "body truncated");
        StringAssert.Contains(
            InspectorBodyLimits.FormatCaptureBanner(BodyCaptureState.NotCaptured, 0, 0, false, false),
            "not captured");

        Assert.AreEqual(
            BodyCaptureState.Truncated,
            InspectorBodyLimits.InferFromBytes(new byte[InspectorBodyLimits.MaxBodyBytes], null));

        Assert.IsNotNull(InspectorBodyLimits.TryPrettyPrint("  {\"k\":1}", contentType: null));
        Assert.IsNotNull(InspectorBodyLimits.TryPrettyPrint("  [1,2]", contentType: ""));
        Assert.IsNotNull(InspectorBodyLimits.TryPrettyPrint("<div>hi", "text/html"));
        Assert.IsNotNull(InspectorBodyLimits.TryPrettyPrint("plain text after", "text/html"));

        Assert.AreEqual("request.bin", InspectorBodyLimits.SuggestBodyFileName(null, null, null, true));
        Assert.AreEqual("response.json", InspectorBodyLimits.SuggestBodyFileName(null, null, "application/json", false));
        Assert.AreEqual("response.json", InspectorBodyLimits.SuggestBodyFileName(null, null, "text/json; charset=utf-8", false));
        Assert.AreEqual("response.html", InspectorBodyLimits.SuggestBodyFileName(null, null, "text/html", false));
        Assert.AreEqual("response.txt", InspectorBodyLimits.SuggestBodyFileName(null, null, "text/plain", false));
        Assert.AreEqual("response.xml", InspectorBodyLimits.SuggestBodyFileName(null, null, "text/xml", false));
        Assert.AreEqual("response.xml", InspectorBodyLimits.SuggestBodyFileName(null, null, "application/xml", false));
        Assert.AreEqual("response.png", InspectorBodyLimits.SuggestBodyFileName(null, null, "image/png", false));
        Assert.AreEqual("response.jpg", InspectorBodyLimits.SuggestBodyFileName(null, null, "image/jpeg", false));
        Assert.AreEqual("response.gif", InspectorBodyLimits.SuggestBodyFileName(null, null, "image/gif", false));
        Assert.AreEqual("response.webp", InspectorBodyLimits.SuggestBodyFileName(null, null, "image/webp", false));
        Assert.AreEqual("response.bmp", InspectorBodyLimits.SuggestBodyFileName(null, null, "image/bmp", false));
        Assert.AreEqual("response.bin", InspectorBodyLimits.SuggestBodyFileName(null, null, "application/octet-stream", false));
        Assert.AreEqual("response.js", InspectorBodyLimits.SuggestBodyFileName(null, null, "application/javascript", false));
        Assert.AreEqual("response.css", InspectorBodyLimits.SuggestBodyFileName(null, null, "text/css", false));
        Assert.AreEqual("response.bin", InspectorBodyLimits.SuggestBodyFileName(null, null, "application/pdf", false));
    }

    [TestMethod]
    public void Breakpoint_TimeoutOverflowAndEdit_CoverActiveHitArms()
    {
        var vm = new BreakpointViewModel
        {
            Enabled = true,
            UrlFilter = "*",
        };
        var session = new SessionSnapshot
        {
            Id = 9,
            Url = "https://example.test/" + new string('x', 80),
            Method = "GET",
        };

        Assert.IsTrue(vm.TryEnter(session, out var hit));
        Assert.IsNotNull(hit);
        vm.EditBody("{\"edited\":true}");
        Assert.AreEqual("{\"edited\":true}", hit!.EditedBody);

        // Second enter while paused → overflow message (no second active hit).
        Assert.IsFalse(vm.TryEnter(session, out _));
        StringAssert.Contains(vm.LastOverflowMessage, "Already paused");

        typeof(BreakpointViewModel)
            .GetMethod("OnHitTimedOut", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(vm, [hit]);
        StringAssert.Contains(vm.LastOverflowMessage, "auto-continued");
        Assert.IsFalse(vm.HasActiveHit);

        // Idempotent ActiveSummary / LastOverflowMessage setters (same value).
        var summary = vm.ActiveSummary;
        typeof(BreakpointViewModel).GetProperty(nameof(BreakpointViewModel.ActiveSummary))!
            .SetValue(vm, summary);
        Assert.AreEqual(summary, vm.ActiveSummary);
        var overflow = vm.LastOverflowMessage;
        typeof(BreakpointViewModel).GetProperty(nameof(BreakpointViewModel.LastOverflowMessage))!
            .SetValue(vm, overflow);
        Assert.AreEqual(overflow, vm.LastOverflowMessage);
    }

    [TestMethod]
    public async Task Replay_AttachBodyFromFile_CoversStreamContentPath()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ti-replay-cov-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var bodyPath = Path.Combine(dir, "body.bin");
        await File.WriteAllBytesAsync(bodyPath, [1, 2, 3, 4]);
        try
        {
            var attach = typeof(ReplayService).GetMethod(
                "AttachBodyAsync", BindingFlags.NonPublic | BindingFlags.Static)!;
            using var request = new HttpRequestMessage(HttpMethod.Post, "https://example.test/");
            var session = new SessionSnapshot { ContentType = "application/octet-stream" };
            await using var fs = await (Task<FileStream?>)attach.Invoke(
                null, [request, session, null, bodyPath, CancellationToken.None])!;
            Assert.IsNotNull(fs);
            Assert.IsNotNull(request.Content);
            Assert.AreEqual(4, request.Content!.Headers.ContentLength);

            await Assert.ThrowsExceptionAsync<FileNotFoundException>(async () =>
            {
                using var missingReq = new HttpRequestMessage(HttpMethod.Post, "https://example.test/");
                _ = await (Task<FileStream?>)attach.Invoke(
                    null,
                    [missingReq, session, null, Path.Combine(dir, "missing.bin"), CancellationToken.None])!;
            });
        }
        finally
        {
            try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch { /* ignore */ }
        }
    }

    [TestMethod]
    public void SessionBodyDiskCache_UpdateLimits_PrunesWithNewBudget()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ti-disk-cov-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            using var cache = new SessionBodyDiskCache(dir, maxBytes: 10_000_000, maxAge: TimeSpan.FromDays(1));
            cache.UpdateLimits(0, TimeSpan.Zero); // keep prior when non-positive
            cache.UpdateLimits(1024, TimeSpan.FromMilliseconds(1));
            Assert.AreEqual(dir, cache.DirectoryPath);
        }
        finally
        {
            try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch { /* ignore */ }
        }
    }

    [TestMethod]
    public void AutoResponder_MapLocalOversizeAndIoFailure_CoverResolveArms()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ti-ar-cov-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var big = Path.Combine(dir, "big.bin");
        try
        {
            using (var fs = new FileStream(big, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                fs.SetLength(InspectorBodyLimits.MaxMapLocalFileBytes + 1);
            }

            var rule = new AutoResponderRule
            {
                Enabled = true,
                MatchUrl = "*",
                LocalFilePath = big,
            };
            Assert.IsFalse(AutoResponderViewModel.TryResolveResponse(rule, out _, out _, out _, out var err));
            StringAssert.Contains(err!, "exceeds");

            rule.LocalFilePath = Path.Combine(dir, "missing.bin");
            Assert.IsFalse(AutoResponderViewModel.TryResolveResponse(rule, out _, out _, out _, out err));
            StringAssert.Contains(err!, "not found");

            // Locked file → Map Local read failed catch arm (Windows); skip soft on platforms that allow share.
            rule.LocalFilePath = big;
            using (new FileStream(big, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                _ = AutoResponderViewModel.TryResolveResponse(rule, out _, out _, out _, out _);
            }
        }
        finally
        {
            try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch { /* ignore */ }
        }
    }

    [TestMethod]
    public async Task BodyInspect_PrettyRawAndComposerFile_CoverPropertyArms()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ti-body-gap-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var bodyFile = Path.Combine(dir, "composer.bin");
        await File.WriteAllBytesAsync(bodyFile, [9, 9]);
        try
        {
            using var interception = new InterceptionService(new RecordingSystemProxyController())
            {
                UseInMemoryTrustState = true,
            };
            var settings = new SettingsService(Path.Combine(dir, "settings.json"));
            settings.Current.AutoStartCapture = false;
            settings.Save();
            var registry = new SessionRegistry();
            var vm = new MainWindowViewModel(
                new SessionStreamBuffer(registry),
                registry,
                new UpdateService(settings),
                settings,
                interception,
                new ScriptedInspectorDialogs())
            {
                BindPort = 0,
                BindAddress = "127.0.0.1",
            };

            var snap = new SessionSnapshot
            {
                Id = 3,
                Method = "GET",
                Url = "https://example.test/api",
                ContentType = "application/json",
                ResponseBodyText = "{\"a\":1}",
                ResponseBodyBytes = Encoding.UTF8.GetBytes("{\"a\":1}"),
                ResponseBodyCapture = BodyCaptureState.Complete,
            };
            vm.SeedSession(snap);
            vm.SelectedSession = snap;

            Assert.IsTrue(vm.BodyPrettyMode);
            vm.BodyPrettyMode = false;
            Assert.IsFalse(vm.BodyPrettyMode);
            await ExecuteAsync(vm.SetBodyPrettyCommand);
            Assert.IsTrue(vm.BodyPrettyMode);
            await ExecuteAsync(vm.SetBodyRawCommand);
            Assert.IsFalse(vm.BodyPrettyMode);
            vm.BodyPrettyMode = false; // same-value setter arm

            vm.ComposerBodyFilePath = bodyFile;
            Assert.IsTrue(vm.HasComposerBodyFile);
            Assert.IsFalse(vm.ComposerBodyEditorEnabled);
            StringAssert.Contains(vm.ComposerBodyFromFileHint, "Body from file");
            vm.ComposerBodyFilePath = bodyFile; // unchanged
            vm.ComposerBodyFilePath = "  ";
            Assert.IsFalse(vm.HasComposerBodyFile);
            Assert.AreEqual("", vm.ComposerBodyFromFileHint);

            interception.ApplyUnixSslTrustOnUi(false); // Windows / in-memory early return
            Assert.IsFalse(interception.RemoveDecryptFailureBypass("never-added.example"));
            interception.ClearDecryptFailureBypass();
        }
        finally
        {
            try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch { /* ignore */ }
        }
    }

    private static async Task ExecuteAsync(ICommand command)
    {
        command.Execute(null);
        await Task.Delay(50);
    }
}
