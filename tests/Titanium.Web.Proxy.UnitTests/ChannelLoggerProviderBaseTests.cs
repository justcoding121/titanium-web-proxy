using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Logging;

namespace Titanium.Web.Proxy.UnitTests;

/// <summary>
///     Covers the sink-ownership contract of <see cref="ChannelLoggerProviderBase" />: the single writer
///     task is the only thread that ever touches sink state (verified via a recording sink that fails a
///     concurrency check rather than via timing), high-severity overflow uses the dedicated priority
///     channel instead of a synchronous write on the calling thread, and <c>DisposeSink()</c> is skipped
///     - not raced - when the writer does not drain in time.
/// </summary>
[TestClass]
public class ChannelLoggerProviderBaseTests
{
    [TestMethod]
    [Timeout(30 * 1000)]
    public void Enqueue_NeverWritesOnCallingThread_EvenWhenMainChannelIsSaturatedWithErrors()
    {
        // A capacity-1 channel plus a writer gate that never opens guarantees every subsequent
        // Enqueue call observes a saturated main channel and takes the Error overflow path.
        using var sink = new RecordingSink(queueCapacity: 1);
        sink.BlockWriter();

        var logger = sink.CreateLogger("test");

        // First entry is consumed into the (capacity-1) channel; the writer is blocked so it is
        // never actually written yet. Every subsequent Error fills/overflows the main channel.
        logger.LogInformation("filler");
        for (var i = 0; i < 50; i++)
            logger.LogError("overflow entry {Index}", i);

        // The defining assertion: none of these calls ever executed WriteEntryAsync on this
        // (the calling/test) thread. If Enqueue still did a synchronous fallback write here, this
        // would be violated because the writer task is deliberately blocked.
        Assert.AreEqual(0, sink.WritesObservedOnCallingThread,
            "Enqueue must never write to the sink on the calling thread, even when the main " +
            "channel is saturated by Error entries.");

        sink.ReleaseWriter();
    }

    [TestMethod]
    [Timeout(30 * 1000)]
    public async Task OverflowErrors_AreEventuallyDeliveredThroughThePriorityChannel()
    {
        using var sink = new RecordingSink(queueCapacity: 1);
        sink.BlockWriter();

        var logger = sink.CreateLogger("test");
        logger.LogInformation("filler");
        logger.LogError("must not be silently dropped");

        sink.ReleaseWriter();

        var delivered = await sink.WaitForMessageAsync("must not be silently dropped", TimeSpan.FromSeconds(5));
        Assert.IsTrue(delivered, "an Error entry that overflowed the main channel must still reach the " +
                                 "sink via the priority channel instead of being dropped like a low-severity entry.");
    }

    [TestMethod]
    [Timeout(30 * 1000)]
    public void Dispose_WhenWriterDoesNotDrainInTime_DoesNotCallDisposeSink()
    {
        // Dispose() bounds its drain wait to a fixed 3 seconds internally, so this test's own
        // timeout must comfortably exceed that.
        using var sink = new RecordingSink(queueCapacity: 16);
        sink.BlockWriter(); // Writer task is now stuck awaiting the gate, forever, until released below.

        var logger = sink.CreateLogger("test");
        logger.LogError("stuck behind the gate");

        sink.Dispose();

        Assert.IsFalse(sink.DisposeSinkCalled,
            "DisposeSink() must not run while the writer could still be mid-write; it should be " +
            "skipped (leaking the handle) rather than racing the in-progress write.");
        Assert.IsTrue(sink.SinkDisposalLeakReported,
            "a skipped DisposeSink() must be reported rather than silently swallowed.");

        sink.ReleaseWriter(); // Unblock so the background task can exit cleanly before test teardown.
    }

    [TestMethod]
    [Timeout(30 * 1000)]
    public void Dispose_WhenWriterDrainsInTime_CallsDisposeSinkExactlyOnce()
    {
        using var sink = new RecordingSink(queueCapacity: 16);

        var logger = sink.CreateLogger("test");
        logger.LogError("normal entry");

        sink.Dispose();

        Assert.IsTrue(sink.DisposeSinkCalled, "a clean drain must still dispose the sink.");
        Assert.IsFalse(sink.SinkDisposalLeakReported, "a clean drain must not report a leak.");
    }

    /// <summary>
    ///     A minimal <see cref="ChannelLoggerProviderBase" /> that records every write, can be gated to
    ///     simulate a stuck/slow sink, and exposes the protected leak/dispose hooks for assertions.
    /// </summary>
    private sealed class RecordingSink : ChannelLoggerProviderBase
    {
        private readonly List<string> messages = new();
        private readonly object gate = new();
        private SemaphoreSlim? writeGate;
        private readonly int mainThreadId = Environment.CurrentManagedThreadId;

        public RecordingSink(int queueCapacity) : base(queueCapacity)
        {
        }

        public int WritesObservedOnCallingThread { get; private set; }

        public bool DisposeSinkCalled { get; private set; }

        public bool SinkDisposalLeakReported { get; private set; }

        public void BlockWriter()
        {
            writeGate = new SemaphoreSlim(0, 1);
        }

        public void ReleaseWriter()
        {
            // Release the currently-blocked call (if any) and clear the gate so every subsequent
            // write proceeds unblocked, rather than releasing exactly one permit and leaving the
            // *next* write stuck on an exhausted semaphore.
            var gateToRelease = writeGate;
            writeGate = null;
            gateToRelease?.Release();
        }

        public async Task<bool> WaitForMessageAsync(string message, TimeSpan timeout)
        {
            var deadline = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < deadline)
            {
                lock (gate)
                {
                    if (messages.Contains(message)) return true;
                }

                await Task.Delay(20);
            }

            return false;
        }

        protected override async Task WriteEntryAsync(LogEntry entry)
        {
            var gateToAwait = writeGate;
            if (gateToAwait != null) await gateToAwait.WaitAsync().ConfigureAwait(false);

            if (Environment.CurrentManagedThreadId == mainThreadId)
                WritesObservedOnCallingThread++;

            lock (gate)
            {
                messages.Add(entry.Message);
            }
        }

        protected override void DisposeSink()
        {
            DisposeSinkCalled = true;
        }

        protected override void OnSinkDisposalLeaked()
        {
            SinkDisposalLeakReported = true;
        }
    }
}
