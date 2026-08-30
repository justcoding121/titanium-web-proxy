using Avalonia.Headless;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.E2E.Tests.Harness;
using Titanium.Inspector.Services;

namespace Titanium.E2E.Tests.UiVisual;

[TestClass]
public class InspectorShellVisualTests
{
    [TestMethod]
    [TestCategory("E2E-UI-Visual")]
    public async Task MainShell_CaptureRenderedFrame_IsPainted()
    {
        await using var fx = new InspectorHeadlessFixture();
        await fx.StartAsync(visualSkia: true);
        await fx.DispatchAsync(() =>
        {
            var frame = fx.Window.CaptureRenderedFrame();
            InspectorVisualAssert.AssertFramePainted(frame);
            MaybeSave(frame!, "any", "MainShell.png");
        });
    }

    [TestMethod]
    [TestCategory("E2E-UI-Visual")]
    public async Task InspectBody_WithSession_IsPainted()
    {
        await using var fx = new InspectorHeadlessFixture();
        await fx.StartAsync(visualSkia: true);
        await fx.DispatchAsync(() =>
        {
            SeedSession(fx, body: "hello-visual");
            fx.Robot.Click("TabBody");
            InspectorVisualAssert.AssertFramePainted(fx.Window.CaptureRenderedFrame());
        });
    }

    [TestMethod]
    [TestCategory("E2E-UI-Visual")]
    public async Task InspectHeaders_IsPainted()
    {
        await using var fx = new InspectorHeadlessFixture();
        await fx.StartAsync(visualSkia: true);
        await fx.DispatchAsync(() =>
        {
            SeedSession(fx, headers: "Host: visual.test\nAccept: */*");
            fx.Robot.Click("TabHeaders");
            InspectorVisualAssert.AssertFramePainted(fx.Window.CaptureRenderedFrame());
        });
    }

    [TestMethod]
    [TestCategory("E2E-UI-Visual")]
    public async Task InspectHex_IsPainted()
    {
        await using var fx = new InspectorHeadlessFixture();
        await fx.StartAsync(visualSkia: true);
        await fx.DispatchAsync(() =>
        {
            SeedSession(fx, body: "hex-bytes");
            fx.Robot.Click("TabHex");
            InspectorVisualAssert.AssertFramePainted(fx.Window.CaptureRenderedFrame());
        });
    }

    [TestMethod]
    [TestCategory("E2E-UI-Visual")]
    public async Task ToolsComposer_IsPainted()
    {
        await using var fx = new InspectorHeadlessFixture();
        await fx.StartAsync(visualSkia: true);
        await fx.DispatchAsync(() =>
        {
            fx.Robot.Click("MenuToolsComposer");
            fx.Robot.SetText("ComposerUrl", "http://visual.test/composer");
            InspectorVisualAssert.AssertFramePainted(fx.Window.CaptureRenderedFrame());
        });
    }

    [TestMethod]
    [TestCategory("E2E-UI-Visual")]
    public async Task ToolsBreakpoints_IsPainted()
    {
        await using var fx = new InspectorHeadlessFixture();
        await fx.StartAsync(visualSkia: true);
        await fx.DispatchAsync(() =>
        {
            fx.Robot.Click("MenuToolsBreakpoints");
            fx.Robot.SetText("BreakpointUrlFilter", "*/visual/*");
            InspectorVisualAssert.AssertFramePainted(fx.Window.CaptureRenderedFrame());
        });
    }

    [TestMethod]
    [TestCategory("E2E-UI-Visual")]
    public async Task ToolsAutoResponder_IsPainted()
    {
        await using var fx = new InspectorHeadlessFixture();
        await fx.StartAsync(visualSkia: true);
        await fx.DispatchAsync(() =>
        {
            fx.Robot.Click("MenuToolsAutoResponder");
            fx.Robot.SetCheck("AutoResponderEnabled", true);
            InspectorVisualAssert.AssertFramePainted(fx.Window.CaptureRenderedFrame());
        });
    }

    [TestMethod]
    [TestCategory("E2E-UI-Visual")]
    public async Task ToolsScripts_IsPainted()
    {
        await using var fx = new InspectorHeadlessFixture();
        await fx.StartAsync(visualSkia: true);
        await fx.DispatchAsync(() =>
        {
            fx.Robot.Click("MenuToolsScripts");
            fx.Robot.SetText("ScriptOnRequest", "abort");
            InspectorVisualAssert.AssertFramePainted(fx.Window.CaptureRenderedFrame());
        });
    }

    private static void SeedSession(InspectorHeadlessFixture fx, string? body = null, string? headers = null)
    {
        fx.ViewModel.Sessions.Add(new SessionSnapshot
        {
            Id = 9,
            Method = "GET",
            StatusCode = 200,
            Host = "visual.test",
            Url = "http://visual.test/body",
            Protocol = "HTTP/1.1",
            ResponseBodyText = body,
            RequestHeadersText = headers,
        });
        fx.ViewModel.SelectedSession = fx.ViewModel.Sessions[0];
    }

    private static void MaybeSave(Avalonia.Media.Imaging.WriteableBitmap frame, string os, string name)
    {
        if (string.Equals(Environment.GetEnvironmentVariable("UPDATE_VISUAL_BASELINES"), "1", StringComparison.Ordinal))
        {
            InspectorVisualAssert.SaveBaseline(frame, Path.Combine(os, name));
        }
    }
}
