using System.Diagnostics;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.E2E.Tests.Harness;
using Titanium.Inspector.Services;

namespace Titanium.E2E.Tests.UiHeadless;

/// <summary>
///     Regression: selecting Response Body, then Hex, for a large binary response (a git upload-pack stream)
///     froze the UI for seconds because ~256 K characters of U+FFFD / control characters were laid
///     out in a wrapped TextBox on the UI thread.
/// </summary>
[TestClass]
public class InspectLargeBodyHeadlessTests
{
    // Generous: the fix measures well under a second; the unfixed layout took several seconds.
    private const int MaxTabSwitchMs = 3000;

    [TestMethod]
    [TestCategory("E2E-UI-Headless")]
    public async Task BodyAndHexTabs_LargeBinaryResponse_StayResponsiveAndBounded()
    {
        await using var fx = new InspectorHeadlessFixture();
        await fx.StartAsync();

        var wire = new byte[InspectorBodyLimits.MaxBodyBytes];
        new Random(7).NextBytes(wire);
        var text = InspectorBodyLimits.TruncateText(Encoding.UTF8.GetString(wire));

        long bodyMs = 0;
        long hexMs = 0;
        await fx.DispatchAsync(() =>
        {
            fx.ViewModel.Sessions.Add(new SessionSnapshot
            {
                Id = 21,
                Method = "POST",
                StatusCode = 200,
                Host = "git.test",
                Url = "http://git.test/repo.git/git-upload-pack",
                Protocol = "HTTP/1.1",
                ContentType = "application/x-git-upload-pack-result",
                ResponseBodyBytes = wire,
                ResponseBodyText = text,
                ResponseBodyCapture = BodyCaptureState.Truncated,
            });
            fx.ViewModel.SelectedSession = fx.ViewModel.Sessions[0];

            var sw = Stopwatch.StartNew();
            fx.Robot.Click("TabResponse");
            fx.Window.UpdateLayout();
            bodyMs = sw.ElapsedMilliseconds;

            var body = fx.ViewModel.SelectedResponseBody;
            Assert.IsTrue(body.Length <= InspectorDisplayText.MaxBinaryChars + 512, $"Body text length {body.Length}");
            Assert.IsFalse(body.Contains('\uFFFD'), "replacement characters must not reach the TextBox");
            StringAssert.Contains(body, "display limited");

            sw.Restart();
            fx.Robot.SetCheck("BodyHex", true);
            fx.Window.UpdateLayout();
            hexMs = sw.ElapsedMilliseconds;

            var hex = fx.ViewModel.SelectedResponseBody;
            Assert.IsTrue(hex.Length < InspectorBodyLimits.MaxHexBytes * 4, $"Hex text length {hex.Length}");
            Assert.IsFalse(hex.Contains('\uFFFD'), "hex view must stay printable");
        });

        Console.WriteLine($"Inspect tab switch: Body={bodyMs} ms, Hex={hexMs} ms");
        Assert.IsTrue(bodyMs < MaxTabSwitchMs, $"Body tab took {bodyMs} ms");
        Assert.IsTrue(hexMs < MaxTabSwitchMs, $"Hex view took {hexMs} ms");
    }
}
