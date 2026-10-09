using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Inspector.Services;

namespace Titanium.Inspector.Tests;

/// <summary>Guards that keep capture-driven work from freezing the Inspector UI thread under heavy load.</summary>
[TestClass]
public class CaptureUiResponsivenessTests
{
    [TestMethod]
    public void Governor_DefersFlush_WhileUserIsInteracting()
    {
        CaptureUiGovernor.NoteGridFlushed();
        CaptureUiGovernor.NoteUserInput();

        Assert.IsTrue(CaptureUiGovernor.ShouldDeferForInput());
    }

    [TestMethod]
    public async Task Governor_DoesNotDefer_AfterInputGoesQuiet()
    {
        CaptureUiGovernor.NoteGridFlushed();
        CaptureUiGovernor.NoteUserInput();

        await Task.Delay(CaptureUiGovernor.InputQuietMs + 100);

        Assert.IsFalse(CaptureUiGovernor.ShouldDeferForInput());
    }

    [TestMethod]
    public void Governor_IntervalGrowsWithCost_AndStaysBounded()
    {
        for (var i = 0; i < 40; i++)
        {
            CaptureUiGovernor.Report(5_000);
        }

        Assert.IsTrue(CaptureUiGovernor.IntervalMs >= CaptureUiGovernor.MaxIntervalMs - 5 && CaptureUiGovernor.IntervalMs <= CaptureUiGovernor.MaxIntervalMs);

        for (var i = 0; i < 200; i++)
        {
            CaptureUiGovernor.Report(0);
        }

        Assert.IsTrue(CaptureUiGovernor.IntervalMs >= CaptureUiGovernor.MinIntervalMs && CaptureUiGovernor.IntervalMs <= CaptureUiGovernor.MinIntervalMs + 10);
    }

    [TestMethod]
    public void Store_BodyByteAccounting_StaysExact_WithoutFullRecalculationPerAdd()
    {
        using var store = new SessionStore(
            new SessionStoreOptions { MaxSessionsInMemory = 1000, SpillBodiesToDisk = false });

        var all = new List<SessionSnapshot>();
        for (var i = 1; i <= 300; i++)
        {
            var s = new SessionSnapshot
            {
                Id = i,
                Method = "GET",
                Url = $"https://example.com/{i}",
                StatusCode = 200,
                RequestBodyBytes = new byte[10 + i],
                ResponseBodyText = new string('x', i),
            };
            all.Add(s);
            store.Add(s);
        }

        // Grow one body after it was added, then notify: the delta must be picked up.
        all[0].ResponseBodyBytes = new byte[500];
        store.NotifyUpdated(all[0]);

        var expected = all.Sum(SessionStore.EstimateInMemoryBodyBytes);
        Assert.AreEqual(expected, store.InMemoryBodyBytes);
    }
}
