using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Gateway.Kube;
using Titanium.Gateway.Model;
using Titanium.Gateway.Tests.Support;

namespace Titanium.Gateway.Tests;

[TestClass]
public sealed class InformerOptionalTests
{
    [TestMethod]
    public async Task Optional404_SyncsEmptyWithoutThrowing()
    {
        var api = new FakeKubeApi();
        var informer = new Informer<TlsRoute>(
            api,
            "/apis/gateway.networking.k8s.io/v1/tlsroutes",
            null,
            ResourceParsers.Typed<TlsRoute>,
            NullLogger.Instance,
            optional: true);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        var run = informer.RunAsync(stop.Token);
        for (var i = 0; i < 50 && !informer.HasSynced; i++)
        {
            await Task.Delay(20);
        }

        Assert.IsTrue(informer.HasSynced);
        Assert.AreEqual(0, informer.Items.Count);
        await stop.CancelAsync();
        try
        {
            await run;
        }
        catch (OperationCanceledException)
        {
        }
    }
}
