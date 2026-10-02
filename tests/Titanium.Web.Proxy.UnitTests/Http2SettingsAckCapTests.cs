using System.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Http2;

namespace Titanium.Web.Proxy.UnitTests;

/// <summary>
///     SETTINGS ACKs are not numbered. Enforcement must follow the frame that was actually
///     acknowledged, including when the ACK wins the race against recording that frame.
/// </summary>
[TestClass]
public class Http2SettingsAckCapTests
{
    [TestMethod]
    public void AckAppliesTheOldestAdvertisedCap_NotTheNewest()
    {
        using var cts = new CancellationTokenSource();
        var state = new Http2ConnectionState(1, cts, maxPooledStreamStates: 8);
        state.CommitSettingsFrameTowardClient(8);
        state.CommitSettingsFrameTowardClient(1);

        state.ApplyClientSettingsAck();
        Assert.AreEqual(8, state.EnforcedMaxConcurrentStreamsTowardClient);

        state.ApplyClientSettingsAck();
        Assert.AreEqual(1, state.EnforcedMaxConcurrentStreamsTowardClient);
    }

    [TestMethod]
    public void AckThatArrivesBeforeTheFrameIsRecorded_AppliesThatFrameOnly()
    {
        using var cts = new CancellationTokenSource();
        var state = new Http2ConnectionState(1, cts, maxPooledStreamStates: 8);

        state.ApplyClientSettingsAck();
        state.CommitSettingsFrameTowardClient(8);
        Assert.AreEqual(8, state.EnforcedMaxConcurrentStreamsTowardClient);

        state.CommitSettingsFrameTowardClient(1);
        Assert.AreEqual(8, state.EnforcedMaxConcurrentStreamsTowardClient,
            "A later SETTINGS frame waits for its own ACK.");

        state.ApplyClientSettingsAck();
        Assert.AreEqual(1, state.EnforcedMaxConcurrentStreamsTowardClient);
    }

    [TestMethod]
    public void SettingsFrameThatDoesNotChangeTheCap_LeavesEnforcementAlone()
    {
        using var cts = new CancellationTokenSource();
        var state = new Http2ConnectionState(1, cts, maxPooledStreamStates: 8);
        state.CommitSettingsFrameTowardClient(8);
        state.ApplyClientSettingsAck();

        state.CommitSettingsFrameTowardClient(null);
        state.ApplyClientSettingsAck();

        Assert.AreEqual(8, state.EnforcedMaxConcurrentStreamsTowardClient);
    }
}
