using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Http2;
using Titanium.Web.Proxy.Network.Tcp;

namespace Titanium.Web.Proxy.UnitTests;

/// <summary>
///     Cold HTTP/2 origin probes must not stall MITM ServerHello. Chrome/Edge abort the handshake
///     (EOF / net::ERR_HTTP2_PROTOCOL_ERROR) and recover on reload once the capability cache is warm.
/// </summary>
[TestClass]
public class Http2ServerHelloProbeBudgetTests
{
    [TestMethod]
    public async Task TryComplete_ReturnsCompletedProbeImmediately()
    {
        var expected = new Http2NegotiationResult(true, null);

        var sw = Stopwatch.StartNew();
        var result = await ProxyServer.TryCompleteHttp2NegotiationBeforeClientAlpnAsync(
            Task.FromResult(expected), CancellationToken.None);
        sw.Stop();

        Assert.AreSame(expected, result);
        Assert.IsTrue(sw.Elapsed < TimeSpan.FromMilliseconds(50),
            $"Completed probe waited {sw.Elapsed}; cache hits must not pay Http2ServerHelloProbeBudget.");
    }

    [TestMethod]
    public async Task TryComplete_ReturnsAsSoonAsProbeCompletes_WithoutWaitingFullBudget()
    {
        var expected = new Http2NegotiationResult(true, null);
        var tcs = new TaskCompletionSource<Http2NegotiationResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        _ = Task.Run(async () =>
        {
            await Task.Delay(40);
            tcs.SetResult(expected);
        });

        var sw = Stopwatch.StartNew();
        var result = await ProxyServer.TryCompleteHttp2NegotiationBeforeClientAlpnAsync(
            tcs.Task, CancellationToken.None);
        sw.Stop();

        Assert.AreSame(expected, result);
        Assert.IsTrue(sw.Elapsed < ProxyServer.Http2ServerHelloProbeBudget,
            $"Fast probe waited {sw.Elapsed}; must return when the probe completes, not at the full budget.");
    }

    [TestMethod]
    public async Task TryComplete_ReturnsNullWhenProbeExceedsBudget()
    {
        var tcs = new TaskCompletionSource<Http2NegotiationResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var sw = Stopwatch.StartNew();

        var result = await ProxyServer.TryCompleteHttp2NegotiationBeforeClientAlpnAsync(
            tcs.Task, CancellationToken.None);

        sw.Stop();

        Assert.IsNull(result);
        Assert.IsFalse(tcs.Task.IsCompleted,
            "A ServerHello-budget miss must leave the origin probe running so it can be adopted after TLS.");
        Assert.IsTrue(sw.Elapsed < TimeSpan.FromSeconds(1),
            $"Budget wait took {sw.Elapsed}; expected return inside Http2ServerHelloProbeBudget.");
        tcs.SetCanceled();
    }

    [TestMethod]
    public async Task TryComplete_PropagatesProbeTimeoutException_InsteadOfTreatingItAsBudgetMiss()
    {
        var probe = Task.FromException<Http2NegotiationResult>(new TimeoutException("origin TLS timed out"));

        var thrown = await Assert.ThrowsExceptionAsync<TimeoutException>(() =>
            ProxyServer.TryCompleteHttp2NegotiationBeforeClientAlpnAsync(probe, CancellationToken.None));

        Assert.AreEqual("origin TLS timed out", thrown.Message);
    }

    [TestMethod]
    public async Task TryComplete_CanceledToken_ThrowsWithoutWaitingBudget()
    {
        var tcs = new TaskCompletionSource<Http2NegotiationResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var sw = Stopwatch.StartNew();
        try
        {
            await ProxyServer.TryCompleteHttp2NegotiationBeforeClientAlpnAsync(tcs.Task, cts.Token);
            Assert.Fail("Expected OperationCanceledException when the CONNECT token is already canceled.");
        }
        catch (OperationCanceledException)
        {
        }

        sw.Stop();
        tcs.SetCanceled();

        Assert.IsTrue(sw.Elapsed < TimeSpan.FromMilliseconds(200),
            $"Cancellation waited {sw.Elapsed}; must not sit on the ServerHello budget.");
    }

    [TestMethod]
    public void ApplyDeferred_SpeculativeH2RequiresTranslationToForceHttp11Bridge()
    {
        var negotiation = new Http2NegotiationResult(originSupportsHttp2: false, retainedConnectionTask: null);
        var requiresHttp11Bridge = false;
        var requiresH2OriginBridge = false;
        Task<TcpServerConnection?>? prefetch = null;

        ProxyServer.ApplyDeferredHttp2Negotiation(negotiation, clientHttp2AlreadyOffered: true,
            allowHttpProtocolTranslation: true,
            ref requiresHttp11Bridge, ref requiresH2OriginBridge, ref prefetch);

        Assert.IsTrue(requiresHttp11Bridge);
        Assert.IsFalse(requiresH2OriginBridge);
    }

    [TestMethod]
    public void ApplyDeferred_SpeculativeH2WithHttp11Origin_DoesNotForceBridgeWhenTranslationDisabled()
    {
        var negotiation = new Http2NegotiationResult(originSupportsHttp2: false, retainedConnectionTask: null);
        var requiresHttp11Bridge = false;
        var requiresH2OriginBridge = false;
        Task<TcpServerConnection?>? prefetch = null;

        ProxyServer.ApplyDeferredHttp2Negotiation(negotiation, clientHttp2AlreadyOffered: true,
            allowHttpProtocolTranslation: false,
            ref requiresHttp11Bridge, ref requiresH2OriginBridge, ref prefetch);

        Assert.IsFalse(requiresHttp11Bridge,
            "AllowHttpProtocolTranslation=false must not silently enable the H2→H1 bridge.");
    }

    [TestMethod]
    public void ApplyDeferred_LearnableOriginTlsFailure_DoesNotForceHttp11Bridge()
    {
        var negotiation = new Http2NegotiationResult(originSupportsHttp2: false, retainedConnectionTask: null,
            learnableOriginTlsFailure: true);
        var requiresHttp11Bridge = false;
        var requiresH2OriginBridge = false;
        Task<TcpServerConnection?>? prefetch = null;

        ProxyServer.ApplyDeferredHttp2Negotiation(negotiation, clientHttp2AlreadyOffered: true,
            allowHttpProtocolTranslation: true,
            ref requiresHttp11Bridge, ref requiresH2OriginBridge, ref prefetch);

        Assert.IsFalse(requiresHttp11Bridge,
            "A learnable origin TLS failure should not open a doomed H1 origin handshake.");
    }

    [TestMethod]
    public void ApplyDeferred_OriginHttp2_KeepsNativeRelayAndRetainedConnection()
    {
        var retained = Task.FromResult<TcpServerConnection?>(null);
        var negotiation = new Http2NegotiationResult(true, retained);
        var requiresHttp11Bridge = false;
        var requiresH2OriginBridge = false;
        Task<TcpServerConnection?>? prefetch = null;

        ProxyServer.ApplyDeferredHttp2Negotiation(negotiation, clientHttp2AlreadyOffered: true,
            allowHttpProtocolTranslation: false,
            ref requiresHttp11Bridge, ref requiresH2OriginBridge, ref prefetch);

        Assert.IsFalse(requiresHttp11Bridge);
        Assert.IsFalse(requiresH2OriginBridge);
        Assert.AreSame(retained, prefetch);
    }
}
