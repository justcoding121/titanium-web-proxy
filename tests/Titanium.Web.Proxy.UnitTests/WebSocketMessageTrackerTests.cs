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
        var complete = tracker.OnFrame(MakeFrame(true, WebsocketOpCode.Text), out var compressed, out var error);
        Assert.IsTrue(complete);
        Assert.IsFalse(compressed);
        Assert.IsFalse(error);
    }

    [TestMethod]
    public void FragmentedMessage_ReportsIncomplete_UntilFinalContinuation()
    {
        var tracker = new WebSocketMessageTracker();

        // Opening fragment (FIN=0)
        var complete1 = tracker.OnFrame(MakeFrame(false, WebsocketOpCode.Text), out _, out var error1);
        Assert.IsFalse(complete1);
        Assert.IsFalse(error1);

        // Continuation fragment (FIN=0)
        var complete2 = tracker.OnFrame(MakeFrame(false, WebsocketOpCode.Continuation), out _, out var error2);
        Assert.IsFalse(complete2);
        Assert.IsFalse(error2);

        // Final continuation (FIN=1)
        var complete3 = tracker.OnFrame(MakeFrame(true, WebsocketOpCode.Continuation), out _, out var error3);
        Assert.IsTrue(complete3);
        Assert.IsFalse(error3);
    }

    [TestMethod]
    public void ControlFrame_InjectedDuringFragment_DoesNotAffectState()
    {
        var tracker = new WebSocketMessageTracker();

        // Opening fragment
        tracker.OnFrame(MakeFrame(false, WebsocketOpCode.Text), out _, out _);

        // Ping (control, may be injected)
        var pingComplete = tracker.OnFrame(MakeFrame(true, WebsocketOpCode.Ping), out _, out var pingError);
        Assert.IsTrue(pingComplete); // control frames are always "complete"
        Assert.IsFalse(pingError);

        // Final continuation should still work
        var complete = tracker.OnFrame(MakeFrame(true, WebsocketOpCode.Continuation), out _, out var error);
        Assert.IsTrue(complete);
        Assert.IsFalse(error);
    }

    [TestMethod]
    public void NonContinuationDataFrame_DuringFragmentedMessage_ReportsProtocolError()
    {
        var tracker = new WebSocketMessageTracker();

        // Opening fragment (FIN=0) leaves the message open.
        tracker.OnFrame(MakeFrame(false, WebsocketOpCode.Text), out _, out _);

        // A new Text data frame arrives instead of a Continuation - RFC 6455 §5.4 protocol error.
        var complete = tracker.OnFrame(MakeFrame(true, WebsocketOpCode.Text), out _, out var isProtocolError);

        Assert.IsFalse(complete);
        Assert.IsTrue(isProtocolError, "a non-continuation data frame during an open fragmented message " +
                                        "must be reported as a protocol error, not as 'still in progress'");
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
