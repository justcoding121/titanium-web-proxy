using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.E2E.Tests.Harness;
using Titanium.Inspector.Converters;
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
            fx.Robot.Click("TabResponse");
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
            fx.Robot.Click("TabRequest");
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
            fx.Robot.Click("TabResponse");
            fx.Robot.SetCheck("BodyHex", true);
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

    [TestMethod]
    [TestCategory("E2E-UI-Visual")]
    public async Task Status200_PaintsTheActiveThemeBrush()
    {
        await using var fx = new InspectorHeadlessFixture();
        await fx.StartAsync(visualSkia: true);
        await fx.DispatchAsync(() =>
        {
            AssertPainted(fx, ThemeVariant.Light, 0x0A, 0x5F, 0x0A);
            AssertPainted(fx, ThemeVariant.Dark, 0x8A, 0xDB, 0x7A);
            AssertPainted(fx, ThemeVariant.Light, 0x0A, 0x5F, 0x0A);
        });
    }

    private static void AssertPainted(InspectorHeadlessFixture fx, ThemeVariant theme, byte r, byte g, byte b)
    {
        Application.Current!.RequestedThemeVariant = theme;
        fx.ViewModel.NotifyThemeVariantChanged();
        var brush = (IBrush)StatusCodeBrushConverter.Instance.Convert(
            200, typeof(IBrush), null, System.Globalization.CultureInfo.InvariantCulture)!;
        var window = new Window
        {
            Width = 220,
            Height = 140,
            Background = Brushes.White,
            Content = new TextBlock
            {
                Text = "200",
                FontSize = 72,
                FontWeight = FontWeight.SemiBold,
                Foreground = brush,
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            },
        };
        window.Show();
        try
        {
            var frame = window.CaptureRenderedFrame();
            Assert.IsNotNull(frame);
            var hits = CountNear(frame!, r, g, b, tolerance: 28);
            Assert.IsTrue(hits > 20, $"{theme} painted {hits} pixels near #{r:X2}{g:X2}{b:X2}");
        }
        finally
        {
            window.Close();
        }
    }

    private static unsafe int CountNear(WriteableBitmap frame, byte r, byte g, byte b, int tolerance)
    {
        using var locked = frame.Lock();
        var bgra = frame.Format == Avalonia.Platform.PixelFormat.Bgra8888;
        var ptr = (byte*)locked.Address;
        var stride = locked.RowBytes;
        var hits = 0;
        for (var y = 0; y < frame.PixelSize.Height; y++)
        {
            var row = ptr + (y * stride);
            for (var x = 0; x < frame.PixelSize.Width; x++)
            {
                var i = x * 4;
                byte pr = bgra ? row[i + 2] : row[i];
                byte pg = row[i + 1];
                byte pb = bgra ? row[i] : row[i + 2];
                if (Math.Abs(pr - r) <= tolerance && Math.Abs(pg - g) <= tolerance && Math.Abs(pb - b) <= tolerance)
                {
                    hits++;
                }
            }
        }

        return hits;
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
