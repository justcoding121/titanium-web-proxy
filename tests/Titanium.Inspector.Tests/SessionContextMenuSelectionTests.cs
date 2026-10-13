using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Inspector.Services;
using Titanium.Inspector.ViewModels;

namespace Titanium.Inspector.Tests;

[TestClass]
public class SessionContextMenuSelectionTests
{
    [TestMethod]
    public void MultiSelect_ExposesOnlyGroupApplicableActions()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ti-ctx-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var settings = new SettingsService(Path.Combine(dir, "settings.json"));
        settings.Current.AutoStartCapture = false;
        settings.Save();
        var registry = new SessionRegistry();
        var vm = new MainWindowViewModel(new InspectorViewModelServices(
            new SessionStreamBuffer(registry),
            registry,
            new UpdateService(settings),
            settings,
            new InterceptionService(new RecordingSystemProxyController()) { UseInMemoryTrustState = true },
            new ScriptedInspectorDialogs(),
            new ScriptedInspectorPathPicker()));

        var a = new SessionSnapshot
        {
            Id = 1,
            Method = "GET",
            StatusCode = 200,
            Host = "a.test",
            Url = "http://a.test/",
            Protocol = "HTTP/1.1",
            ProcessName = "chrome",
            RequestBodyText = "req",
            RequestBodyCapture = BodyCaptureState.Complete,
        };
        var b = new SessionSnapshot
        {
            Id = 2,
            Method = "GET",
            StatusCode = 404,
            Host = "b.test",
            Url = "http://b.test/",
            Protocol = "HTTP/1.1",
            ProcessName = "firefox",
        };
        var c = new SessionSnapshot
        {
            Id = 3,
            Method = "GET",
            StatusCode = 200,
            Host = "c.test",
            Url = "http://c.test/",
            Protocol = "HTTP/1.1",
        };
        registry.Store.Sessions.Add(a);
        registry.Store.Sessions.Add(b);
        registry.Store.Sessions.Add(c);
        vm.SelectedSession = a;
        vm.SetSelectedSessions([a, b]);

        Assert.IsFalse(vm.HasSingleSelectedSession);
        Assert.IsFalse(vm.CanSaveSessionMenu);
        Assert.IsFalse(vm.CanSaveRequestBody);
        Assert.IsFalse(vm.CanSaveRawHttp);
        Assert.IsFalse(vm.CanCopyAsCurl);
        Assert.IsTrue(vm.CanCopyUrl);
        Assert.IsTrue(vm.CanCopySessionMenu);
        Assert.IsTrue(vm.CanCopyAsTable);
        Assert.IsFalse(vm.CanFilterByHost);
        Assert.IsFalse(vm.CanFilterSessionMenu);
        Assert.IsTrue(vm.CanDiffSessions);
        Assert.IsTrue(vm.CanKeepOnlySelected);

        b.Host = "a.test";
        vm.SetSelectedSessions([a, b]);
        Assert.IsTrue(vm.CanFilterByHost);
        Assert.IsTrue(vm.CanFilterSessionMenu);
        Assert.IsFalse(vm.CanFilterByStatus);

        vm.FilterByHostCommand.Execute(null);
        StringAssert.Contains(vm.SearchQuery, "host:a.test");
    }

    [TestMethod]
    public void SingleSelect_ExposesSaveAndCodegen()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ti-ctx1-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var settings = new SettingsService(Path.Combine(dir, "settings.json"));
        settings.Current.AutoStartCapture = false;
        settings.Save();
        var registry = new SessionRegistry();
        var vm = new MainWindowViewModel(new InspectorViewModelServices(
            new SessionStreamBuffer(registry),
            registry,
            new UpdateService(settings),
            settings,
            new InterceptionService(new RecordingSystemProxyController()) { UseInMemoryTrustState = true },
            new ScriptedInspectorDialogs(),
            new ScriptedInspectorPathPicker()));

        var snap = new SessionSnapshot
        {
            Id = 9,
            Method = "POST",
            StatusCode = 200,
            Host = "one.test",
            Url = "http://one.test/x",
            Protocol = "HTTP/1.1",
            RequestBodyText = "body",
            RequestBodyCapture = BodyCaptureState.Complete,
        };
        registry.Store.Sessions.Add(snap);
        vm.SelectedSession = snap;
        vm.SetSelectedSessions([snap]);

        Assert.IsTrue(vm.CanSaveSessionMenu);
        Assert.IsTrue(vm.CanSaveRequestBody);
        Assert.IsTrue(vm.CanSaveRawHttp);
        Assert.IsTrue(vm.CanCopyAsCurl);
        Assert.IsTrue(vm.CanFilterByHost);
        Assert.IsTrue(vm.CanFilterSessionMenu);
    }
}
