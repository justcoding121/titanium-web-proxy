using Avalonia.Media.Imaging;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Titanium.E2E.Tests.Harness;

/// <summary>Capture Skia frames and assert the shell actually painted.</summary>
public static class InspectorVisualAssert
{
    public static void AssertFramePainted(WriteableBitmap? frame, int minWidth = 640, int minHeight = 400)
    {
        Assert.IsNotNull(frame, "CaptureRenderedFrame returned null — need UseSkia + UseHeadlessDrawing=false.");
        Assert.IsTrue(frame!.PixelSize.Width >= minWidth, $"Frame width {frame.PixelSize.Width} < {minWidth}");
        Assert.IsTrue(frame.PixelSize.Height >= minHeight, $"Frame height {frame.PixelSize.Height} < {minHeight}");

        using var locked = frame.Lock();
        unsafe
        {
            var ptr = (byte*)locked.Address;
            var stride = locked.RowBytes;
            var w = frame.PixelSize.Width;
            var h = Math.Min(frame.PixelSize.Height, 200);
            long nonWhite = 0;
            for (var y = 0; y < h; y++)
            {
                var row = ptr + (y * stride);
                for (var x = 0; x < w; x++)
                {
                    var i = x * 4;
                    if (row[i] < 250 || row[i + 1] < 250 || row[i + 2] < 250)
                    {
                        nonWhite++;
                    }
                }
            }

            Assert.IsTrue(nonWhite > 100, "Frame looks blank/white — shell likely did not paint.");
        }
    }

    public static void SaveBaseline(WriteableBitmap frame, string relativeUnderUiVisual)
    {
        var repo = CliProcessHarness.FindRepoRoot();
        var path = Path.Combine(repo, "tests", "Titanium.E2E.Tests", "UiVisual", "Baselines", relativeUnderUiVisual);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        frame.Save(path);
    }
}
