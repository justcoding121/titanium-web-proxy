using System.Reflection;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Inspector.Services;
using Titanium.Inspector.ViewModels;

namespace Titanium.Inspector.Tests;

/// <summary>
///     Avalonia-safe leftovers toward 85%: InterceptionService protocol/logging before proxy start,
///     SessionRawHttp status-only response, SessionStore batch remove, and MainWindowViewModel filter tokens.
/// </summary>
[TestClass]
public class InspectorVmStoreInterceptionSoftCoverageWaveBehaviorTests
{
    [TestMethod]
    public void InterceptionService_BeforeProxyStart_ProtocolsAndLoggingNoThrow()
    {
        using var interception = new InterceptionService(new RecordingSystemProxyController())
        {
            UseInMemoryTrustState = true,
            PreferHttp2 = false,
            PreferHttp3 = false,
        };
        interception.ApplyHttpProtocols();
        Assert.IsFalse(interception.Http2Enabled);
        interception.ConfigureLogging(new InspectorSettings { LoggingEnableFile = false });
        interception.SetLastOsTrustCancelled();
        interception.ResetSessionIdSequence();
    }

    [TestMethod]
    public void SessionRawHttp_StatusOnlyResponse_WritesReasonLine()
    {
        using var output = new MemoryStream();
        var snap = new SessionSnapshot
        {
            Method = "HEAD",
            Url = "https://head.example/resource",
            Host = "head.example",
            StatusCode = 304,
            ResponseHeadersText = "ETag: \"v1\"\r\n",
        };
        var result = SessionRawHttp.Write(snap, output);
        Assert.IsFalse(result.Truncated);
        var text = Encoding.UTF8.GetString(output.ToArray());
        StringAssert.Contains(text, "HEAD /resource");
        StringAssert.Contains(text, "HTTP/1.1 304");
        StringAssert.Contains(text, "Not Modified");
        StringAssert.Contains(text, "ETag:");
    }

    [TestMethod]
    public void SessionStore_RemoveMany_RaisesRemovedOnce()
    {
        var store = new SessionStore(new SessionStoreOptions { SpillBodiesToDisk = false });
        store.Add(Snap(1));
        store.Add(Snap(2));
        store.Add(Snap(3));
        var removed = 0;
        store.SessionsRemoved += ids => removed += ids.Count;
        store.Remove([1, 2]);
        Assert.AreEqual(2, removed);
        Assert.IsNull(store.TryGet(1));
        Assert.IsNull(store.TryGet(2));
        Assert.IsNotNull(store.TryGet(3));
    }

    [TestMethod]
    public async Task MainWindowViewModel_QuickFilterTokens_AndCopyUrlGuard()
    {
        var path = Path.Combine(Path.GetTempPath(), "twp-vm-wave-" + Guid.NewGuid().ToString("N") + ".json");
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

            vm.SearchQuery = "status:404,method:POST";
            vm.SeedSession(new SessionSnapshot
            {
                Id = 7,
                Method = "POST",
                Url = "https://filter.example/submit",
                Host = "filter.example",
                StatusCode = 404,
            });
            await InvokeAsync(vm, "CopyUrlAsync");
            StringAssert.Contains(vm.StatusText, "Select", StringComparison.OrdinalIgnoreCase);
            vm.SelectedSession = vm.Sessions[0];
            await InvokeAsync(vm, "CopyUrlAsync");
            Assert.IsFalse(string.IsNullOrWhiteSpace(vm.StatusText));
        }
        finally
        {
            TryDelete(path);
        }
    }

    private static SessionSnapshot Snap(long id) =>
        new()
        {
            Id = id,
            Method = "GET",
            Url = "https://wave.example/" + id.ToString(System.Globalization.CultureInfo.InvariantCulture),
        };

    private static Task InvokeAsync(MainWindowViewModel vm, string name, params object?[] args)
    {
        var method = typeof(MainWindowViewModel).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.IsNotNull(method);
        var result = method!.Invoke(vm, args);
        return result is Task task ? task : Task.CompletedTask;
    }

    private static void TryDelete(string path)
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
