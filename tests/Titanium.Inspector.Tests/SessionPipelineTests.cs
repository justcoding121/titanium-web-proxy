using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Inspector.Services;
using Titanium.Inspector.ViewModels;

namespace Titanium.Inspector.Tests;

[TestClass]
public class SessionPipelineTests
{
    [TestMethod]
    public async Task SessionStreamBuffer_PublishesToRegistry()
    {
        var registry = new SessionRegistry();
        var buffer = new SessionStreamBuffer(registry);
        var snap = buffer.CreatePlaceholder("GET", "https://example.com/");
        var tcs = new TaskCompletionSource();
        buffer.SessionAdded += _ => tcs.TrySetResult();
        buffer.Publish(snap);
        await tcs.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.AreEqual(1, registry.VisibleSessions.Count);
        Assert.AreEqual(snap.Id, registry.TryGet(snap.Id)?.Id);
    }

    [TestMethod]
    public void AutoResponder_DefaultsDisabled()
    {
        var vm = new AutoResponderViewModel();
        Assert.IsFalse(vm.Enabled);
        Assert.AreEqual(0, vm.Rules.Count);
    }

    [TestMethod]
    public void BreakpointViewModel_TimeoutIs120Seconds()
    {
        var vm = new BreakpointViewModel();
        Assert.AreEqual(TimeSpan.FromSeconds(120), vm.Timeout);
    }

    [TestMethod]
    public void SettingsService_RoundTripsChannel()
    {
        var path = Path.Combine(Path.GetTempPath(), "twp-inspector-settings-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var svc = new SettingsService(path);
            svc.Current.UpdateChannel = "Beta";
            svc.Save();
            var loaded = new SettingsService(path);
            Assert.AreEqual("Beta", loaded.Current.UpdateChannel);
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
