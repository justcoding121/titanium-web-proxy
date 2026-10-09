using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Exceptions;
using Titanium.Web.Proxy.Helpers;

namespace Titanium.Web.Proxy.UnitTests;

/// <summary>
///     Regression-2026-10-07: the log read <c>ConnectTimeOutSeconds=Connect; observed 0</c> because the
///     timeout kind was logged as the limit. The exception now carries the configured limit and the
///     observed elapsed time so the line names the real value.
/// </summary>
[TestClass]
public class TimeoutLogDetailTests
{
    [TestMethod]
    [TestCategory("Regression-2026-10-07")]
    public async Task Deadline_TimeoutException_CarriesConfiguredLimitAndObservedElapsed()
    {
        var registry = new DeadlineRegistry();
        using var deadline = registry.Start(CancellationToken.None, TimeSpan.FromMilliseconds(80),
            ProxyTimeoutKind.ResponseHeader);

        var ex = await Assert.ThrowsExactlyAsync<TaskCanceledException>(
            () => Task.Delay(TimeSpan.FromSeconds(30), deadline.Token));

        Assert.IsTrue(deadline.TryGetTimeoutException(ex, out var timeout));
        Assert.AreEqual(ProxyTimeoutKind.ResponseHeader, timeout!.Kind);
        Assert.AreEqual(TimeSpan.FromMilliseconds(80), timeout.ConfiguredTimeout);
        Assert.IsTrue(timeout.ObservedElapsed >= TimeSpan.FromMilliseconds(60),
            $"observed={timeout.ObservedElapsed}");
    }

    [TestMethod]
    [TestCategory("Regression-2026-10-07")]
    public void FormatConfiguredLimit_NamesTheLimit_NotTheKind()
    {
        var withLimit = new ProxyTimeoutException("x", ProxyTimeoutKind.Connect)
        {
            ConfiguredTimeout = TimeSpan.FromSeconds(20)
        };
        Assert.AreEqual("20s", ProxyServer.FormatConfiguredLimit(withLimit));

        var fractional = new ProxyTimeoutException("x", ProxyTimeoutKind.Connect)
        {
            ConfiguredTimeout = TimeSpan.FromMilliseconds(250)
        };
        Assert.AreEqual("250ms", ProxyServer.FormatConfiguredLimit(fractional));

        Assert.AreEqual("unknown",
            ProxyServer.FormatConfiguredLimit(new ProxyTimeoutException("x", ProxyTimeoutKind.Connect)));
    }
}
