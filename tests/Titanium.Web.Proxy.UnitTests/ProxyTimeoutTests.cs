using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Exceptions;
using Titanium.Web.Proxy.Helpers;

namespace Titanium.Web.Proxy.UnitTests;

[TestClass]
public class ProxyTimeoutTests
{
    [TestMethod]
    public void ProxyServer_Timeout_Defaults_Are_Disabled_And_Legacy_Timeouts_Unchanged()
    {
        using var proxy = new ProxyServer(false, false, false);
        Assert.AreEqual(0, proxy.ResponseHeaderTimeoutSeconds);
        Assert.AreEqual(0, proxy.IdleReadTimeoutSeconds);
        Assert.AreEqual(0, proxy.IdleWriteTimeoutSeconds);
        Assert.AreEqual(0, proxy.RequestTimeoutSeconds);
        Assert.AreEqual(60, proxy.ConnectionTimeOutSeconds);
        Assert.AreEqual(20, proxy.ConnectTimeOutSeconds);
    }

    [TestMethod]
    public async Task ProxyTimeoutScope_CancelAfter_Raises_Typed_Timeout()
    {
        using var parent = new CancellationTokenSource();
        using var scope = ProxyTimeoutScope.Create(parent.Token, TimeSpan.FromMilliseconds(50),
            ProxyTimeoutKind.ResponseHeader);

        try
        {
            await Task.Delay(Timeout.Infinite, scope.Token);
            Assert.Fail("Expected cancellation");
        }
        catch (OperationCanceledException ex)
        {
            try
            {
                scope.ThrowIfTimedOut(ex);
                Assert.Fail("Expected ProxyTimeoutException");
            }
            catch (ProxyTimeoutException timeout)
            {
                Assert.AreEqual(ProxyTimeoutKind.ResponseHeader, timeout.Kind);
            }
        }
    }

    [TestMethod]
    public void ProxyTimeoutScope_Parent_Cancel_Does_Not_Report_Timeout()
    {
        using var parent = new CancellationTokenSource();
        using var scope = ProxyTimeoutScope.Create(parent.Token, TimeSpan.FromSeconds(30),
            ProxyTimeoutKind.Request);

        parent.Cancel();
        Assert.IsFalse(scope.IsTimedOut());

        try
        {
            scope.ThrowIfTimedOut(new OperationCanceledException(scope.Token));
            Assert.Fail("Expected OperationCanceledException");
        }
        catch (ProxyTimeoutException)
        {
            Assert.Fail("Parent cancellation must not be reported as ProxyTimeoutException");
        }
        catch (OperationCanceledException)
        {
            // expected
        }
    }

    [TestMethod]
    public void ProxyTimeoutScope_Without_Deadline_Forwards_Parent_Token()
    {
        using var parent = new CancellationTokenSource();
        using var scope = ProxyTimeoutScope.Create(parent.Token, null, ProxyTimeoutKind.IdleRead);

        Assert.IsFalse(scope.HasDeadline);
        Assert.AreEqual(parent.Token, scope.Token);
    }
}
