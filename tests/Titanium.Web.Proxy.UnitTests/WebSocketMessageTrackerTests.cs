using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Titanium.Web.Proxy.UnitTests;

[TestClass]
public class WebSocketMessageTrackerTests
{
    private static WebSocketFrame MakeFrame(bool isFinal, WebsocketOpCode opCode)
    {
        return new WebSocketFrame { IsFinal = isFinal, OpCode = opCode, Data = System.Memory<byte>.Empty };
    }

    [TestMethod]
    public void SingleFrame_Text_CompleteOnFinal()
    {
        var tracker = new WebSocketMessageTracker();
        var complete = tracker.OnFrame(MakeFrame(true, WebsocketOpCode.Text), out var compressed);
        Assert.IsTrue(complete);
        Assert.IsFalse(compressed);
    }

    [TestMethod]
    public void FragmentedMessage_ReportsIncomplete_UntilFinalContinuation()
    {
        var tracker = new WebSocketMessageTracker();

        // Opening fragment (FIN=0)
        var complete1 = tracker.OnFrame(MakeFrame(false, WebsocketOpCode.Text), out _);
        Assert.IsFalse(complete1);

        // Continuation fragment (FIN=0)
        var complete2 = tracker.OnFrame(MakeFrame(false, WebsocketOpCode.Continuation), out _);
        Assert.IsFalse(complete2);

        // Final continuation (FIN=1)
        var complete3 = tracker.OnFrame(MakeFrame(true, WebsocketOpCode.Continuation), out _);
        Assert.IsTrue(complete3);
    }

    [TestMethod]
    public void ControlFrame_InjectedDuringFragment_DoesNotAffectState()
    {
        var tracker = new WebSocketMessageTracker();

        // Opening fragment
        tracker.OnFrame(MakeFrame(false, WebsocketOpCode.Text), out _);

        // Ping (control, may be injected)
        var pingComplete = tracker.OnFrame(MakeFrame(true, WebsocketOpCode.Ping), out _);
        Assert.IsTrue(pingComplete); // control frames are always "complete"

        // Final continuation should still work
        var complete = tracker.OnFrame(MakeFrame(true, WebsocketOpCode.Continuation), out _);
        Assert.IsTrue(complete);
    }

    [TestMethod]
    public void PerMessageDeflateParameters_TryParse_Valid()
    {
        var p = PerMessageDeflateParameters.TryParse(
            "permessage-deflate; client_no_context_takeover; client_max_window_bits=12");
        Assert.IsNotNull(p);
        Assert.IsFalse(p!.ClientContextTakeover);
        Assert.IsTrue(p.ServerContextTakeover);
        Assert.AreEqual(12, p.ClientMaxWindowBits);
        Assert.AreEqual(15, p.ServerMaxWindowBits);
    }

    [TestMethod]
    public void PerMessageDeflateParameters_TryParse_Null_ReturnsNull()
    {
        Assert.IsNull(PerMessageDeflateParameters.TryParse(null));
        Assert.IsNull(PerMessageDeflateParameters.TryParse("x-custom-extension"));
    }
}
