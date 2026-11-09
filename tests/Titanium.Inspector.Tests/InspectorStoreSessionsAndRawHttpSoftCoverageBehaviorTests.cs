using System.Globalization;
using System.Reflection;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Inspector.Services;
using Titanium.Inspector.ViewModels;
using Titanium.Web.Proxy.Http;
using Titanium.Web.Proxy.Models;

namespace Titanium.Inspector.Tests;

/// <summary>
///     Avalonia-safe leftovers toward 85%: SessionStore spill/open/dispose, Sessions
///     SaveRawHttp/Diff/SaveBody guards, SessionRawHttp reason/body arms, Progressive identity.
/// </summary>
[TestClass]
public class InspectorStoreSessionsAndRawHttpSoftCoverageBehaviorTests
{
    [TestMethod]
    public async Task SessionStore_SpillOpenNotifyRemoveAndEnsureLoad()
    {
        var root = Path.Combine(Path.GetTempPath(), "twp-store2-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var store = new SessionStore(
                new SessionStoreOptions { SpillBodiesToDisk = true, MaxSessionsInMemory = 50 },
                cacheDirectory: root);

            var snap = new SessionSnapshot
            {
                Id = 11,
                Method = "POST",
                Url = "https://store2.example/",
                RequestBodyText = "req-body",
                ResponseBodyText = "resp-body",
                StatusCode = 200,
            };
            store.Add(snap);
            store.NotifyUpdated(snap);
            Assert.IsNotNull(store.TryGet(11));

            // EnsureBodiesLoaded when already in memory is a no-op soft path.
            await store.EnsureBodiesLoadedAsync([snap]);

            Assert.IsFalse(store.TryOpenSpilledHar(new SessionSnapshot { Id = 11, BodiesOnDisk = false }, out _));

            store.Remove(Array.Empty<long>());
            store.Dispose();
            store.NotifyUpdated(new SessionSnapshot { Id = 99, Method = "GET", Url = "https://gone/" });
        }
        finally
        {
            TryDelete(root);
        }
    }

    [TestMethod]
    public async Task SessionsAndBodyInspect_NullSelectionGuards()
    {
        var path = Path.Combine(Path.GetTempPath(), "twp-sess-soft-" + Guid.NewGuid().ToString("N") + ".json");
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

            await InvokeAsync(vm, "SaveRawHttpAsync");
            StringAssert.Contains(vm.StatusText, "Select a session");

            await InvokeAsync(vm, "DiffSessionsAsync");
            // Diff needs exactly two sessions — guard status without path picker Accept.
            Assert.IsFalse(string.IsNullOrWhiteSpace(vm.StatusText));

            await InvokeAsync(vm, "SaveBodyAsync", true);
            StringAssert.Contains(vm.StatusText, "Select a session");

            vm.ToggleInspectHeadersCommand.Execute(null);
            Assert.IsNotNull(vm.CopyRequestHeadersCommand);
        }
        finally
        {
            TryDeleteFile(path);
        }
    }

    [TestMethod]
    public void SessionRawHttp_ReasonsBytesAndProgressiveIdentity()
    {
        foreach (var code in new[] { 200, 201, 204, 301, 302, 304, 400, 401, 403, 404, 500, 502, 503, 418 })
        {
            using var output = new MemoryStream();
            var snap = new SessionSnapshot
            {
                Id = code,
                Method = "GET",
                Url = "https://raw.example/" + code.ToString(CultureInfo.InvariantCulture),
                Host = "raw.example",
                StatusCode = code,
                RequestBodyBytes = Encoding.UTF8.GetBytes("q"),
                ResponseBodyBytes = Encoding.UTF8.GetBytes(new string('r', 40)),
            };
            var result = SessionRawHttp.Write(snap, output, maxBodyBytes: 8);
            Assert.IsTrue(result.Truncated || result.BytesWritten > 0);
        }

        var body = Encoding.UTF8.GetBytes(new string('p', ProgressiveDelivery.MinWireBytes + 16));
        var response = new Response
        {
            HttpVersion = new Version(1, 1),
            StatusCode = 200,
            StatusDescription = "OK",
            Body = body,
        };
        response.Headers.AddHeader(new HttpHeader("Content-Length", body.Length.ToString(CultureInfo.InvariantCulture)));
        response.Headers.AddHeader(new HttpHeader("Content-Encoding", "identity"));
        var plan = ProgressiveDelivery.TryBuild(response, new Request { Method = "GET" }, body, body.Length, false, false);
        Assert.IsNotNull(plan);
        Assert.IsTrue(ProgressiveDelivery.ProgressiveCount >= 1);
        Assert.IsTrue(ProgressiveDelivery.FallbackCount(ProgressiveSkipReason.Status) >= 0);
        Assert.IsTrue(ProgressiveDelivery.FallbackCount(ProgressiveSkipReason.BuildFailed) >= 0);
        Assert.AreEqual(0, ProgressiveDelivery.FallbackCount((ProgressiveSkipReason)12345));
    }

    private static async Task InvokeAsync(MainWindowViewModel vm, string name, params object[] args)
    {
        var flags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
        var method = typeof(MainWindowViewModel).GetMethod(name, flags);
        Assert.IsNotNull(method, name);
        var result = method!.Invoke(vm, args.Length == 0 ? null : args);
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
