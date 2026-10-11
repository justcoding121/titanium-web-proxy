using Avalonia.Controls;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Inspector.Services;
using Titanium.Inspector.ViewModels;

namespace Titanium.Inspector.Tests;

[TestClass]
public class HelpLinkTests
{
    [TestMethod]
    public void Commands_LaunchTheSharedDocumentationAndIssueAddresses()
    {
        var launcher = new RecordingUriLauncher();
        var dir = Path.Combine(Path.GetTempPath(), "ti-help-" + Guid.NewGuid().ToString("N"));
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
            new ScriptedInspectorPathPicker(),
            UriLauncher: launcher));

        Assert.IsNotNull(vm.OpenDocumentationCommand);
        Assert.IsNotNull(vm.OpenReportIssueCommand);

        vm.OpenDocumentationCommand.Execute(null);
        Assert.AreEqual(InspectorLinks.Documentation, launcher.Last);

        vm.OpenReportIssueCommand.Execute(null);
        Assert.AreEqual(InspectorLinks.ReportIssue, launcher.Last);
        Assert.AreEqual(InspectorLinks.Website, new Uri(string.Concat("https://", "titaniumproxy.com")));
    }

    [TestMethod]
    public async Task WindowLauncher_WithoutAWindow_DoesNotThrow()
    {
        var launcher = new WindowUriLauncher(() => (TopLevel?)null);
        await launcher.LaunchAsync(InspectorLinks.Documentation);
    }

    private sealed class RecordingUriLauncher : IExternalUriLauncher
    {
        public Uri? Last { get; private set; }

        public Task LaunchAsync(Uri uri)
        {
            Last = uri;
            return Task.CompletedTask;
        }
    }
}
