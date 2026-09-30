using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Inspector.Services;

namespace Titanium.Inspector.Tests;

/// <summary>
/// A/B: shared mutable WebSocket frame list vs immutable live publisher.
/// Arm A reproduces the inspector crash (List modified during enumeration).
/// Arm B proves LiveWebSocketFrames publish stays race-free under the same overlap.
/// </summary>
[TestClass]
public class LiveWebSocketFrameRaceTests
{
    private static WebSocketFrameSnapshot Frame(string preview) =>
        new()
        {
            Direction = "Client",
            Opcode = "Text",
            PayloadPreview = preview,
        };

    [TestMethod]
    public void ArmA_SharedList_EstimateThrowsWhenMutatedDuringEnumeration()
    {
        var frames = new List<WebSocketFrameSnapshot> { Frame("seed") };
        var snap = new SessionSnapshot
        {
            Id = 1,
            Method = "GET",
            Url = "wss://example/ws",
            IsWebSocket = true,
            WebSocketFrames = frames,
        };

        using var stop = new CancellationTokenSource();
        var writer = Task.Run(() =>
        {
            var n = 0;
            while (!stop.IsCancellationRequested)
            {
                frames.Add(Frame("w" + n++));
            }
        });

        Exception? caught = null;
        try
        {
            for (var i = 0; i < 2_000_000 && caught is null; i++)
            {
                try
                {
                    _ = SessionStore.EstimateInMemoryBodyBytes(snap);
                }
                catch (InvalidOperationException ex) when (ex.Message.Contains(
                           "Collection was modified",
                           StringComparison.Ordinal))
                {
                    // Classic List enumerator fail-fast under concurrent Add.
                    caught = ex;
                }
                catch (NullReferenceException ex)
                {
                    // Concurrent List resize can also tear reads (null element / enumerator).
                    caught = ex;
                }
            }
        }
        finally
        {
            stop.Cancel();
            writer.Wait(TimeSpan.FromSeconds(5));
        }

        Assert.IsNotNull(
            caught,
            "Arm A must hit shared-list race (modified-during-enumeration or torn null); stress was too weak if this fails.");
    }

    [TestMethod]
    public void ArmB_LivePublisher_NotifyUpdatedSurvivesConcurrentAppends()
    {
        const int appendCount = 5_000;
        var live = new LiveWebSocketFrames();
        var snap = new SessionSnapshot
        {
            Id = 42,
            Method = "GET",
            Url = "wss://example/ws",
            StatusCode = 101,
            IsWebSocket = true,
            WebSocketFrames = live.Current,
        };

        using var store = new SessionStore(
            new SessionStoreOptions
            {
                MaxSessionsInMemory = 100,
                SpillBodiesToDisk = false,
            });
        store.Add(snap);

        // Hold a reference to the initial empty snapshot; appends must not mutate it.
        var emptySnapshot = snap.WebSocketFrames!;
        Assert.AreEqual(0, emptySnapshot.Count);

        using var stop = new CancellationTokenSource();
        var writer = Task.Run(() =>
        {
            for (var n = 0; n < appendCount && !stop.IsCancellationRequested; n++)
            {
                live.Append(snap, Frame("b" + n));
            }
        });

        Exception? caught = null;
        try
        {
            for (var i = 0; i < 50_000; i++)
            {
                try
                {
                    store.NotifyUpdated(snap);
                    _ = SessionStore.EstimateInMemoryBodyBytes(snap);
                    // In-flight reader of the original empty list still finishes.
                    foreach (var _ in emptySnapshot)
                    {
                    }
                }
                catch (Exception ex)
                {
                    caught = ex;
                    break;
                }
            }

            Assert.IsTrue(writer.Wait(TimeSpan.FromSeconds(30)), "Append loop did not finish");
            // Final wave after writer drained.
            for (var i = 0; i < 1_000; i++)
            {
                store.NotifyUpdated(snap);
                _ = SessionStore.EstimateInMemoryBodyBytes(snap);
            }
        }
        finally
        {
            stop.Cancel();
            writer.Wait(TimeSpan.FromSeconds(5));
        }

        Assert.IsNull(caught, $"Arm B must stay race-free; got {caught}");
        Assert.AreEqual(0, emptySnapshot.Count, "Original immutable snapshot must stay empty");
        Assert.AreEqual(appendCount, snap.WebSocketFrames!.Count);
    }
}
