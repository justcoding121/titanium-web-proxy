using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Inspector.Services;
using Titanium.Inspector.ViewModels;

namespace Titanium.Inspector.Tests;

[TestClass]
public class BreakpointViewModelTests
{
    [TestMethod]
    public void TryEnter_RequiresEnabledAndMatchingUrl()
    {
        var vm = new BreakpointViewModel { Enabled = false, UrlFilter = "*" };
        var session = new SessionSnapshot { Url = "https://example.com/a" };
        Assert.IsFalse(vm.TryEnter(session, out _));

        vm.Enabled = true;
        Assert.IsTrue(vm.TryEnter(session, out var hit));
        Assert.IsNotNull(vm.Active);
        Assert.AreSame(session, hit.Session);
        vm.Continue();
        Assert.IsNull(vm.Active);
    }

    [TestMethod]
    public void TryEnter_OverflowAutoContinuesSecond()
    {
        var vm = new BreakpointViewModel { Enabled = true, UrlFilter = "*" };
        Assert.IsTrue(vm.TryEnter(new SessionSnapshot { Url = "https://a/" }, out _));
        Assert.IsFalse(vm.TryEnter(new SessionSnapshot { Url = "https://b/" }, out _));
        vm.Abort();
        Assert.IsNull(vm.Active);
    }

    [TestMethod]
    public void UrlFilter_GlobMatch()
    {
        var vm = new BreakpointViewModel { Enabled = true, UrlFilter = "https://api.*/v1/*" };
        Assert.IsTrue(vm.TryEnter(new SessionSnapshot { Url = "https://api.example/v1/x" }, out _));
        vm.Continue();

        Assert.IsFalse(vm.TryEnter(new SessionSnapshot { Url = "https://other.example/v1/x" }, out _));

        vm.UrlFilter = "";
        Assert.IsTrue(vm.TryEnter(new SessionSnapshot { Url = "https://anything/" }, out _));
        vm.Continue();
    }

    [TestMethod]
    public async Task Continue_Abort_EditBody()
    {
        var vm = new BreakpointViewModel { Enabled = true };
        Assert.IsTrue(vm.TryEnter(new SessionSnapshot { Url = "https://e/" }, out var hit));

        vm.EditBody("patched-body");
        Assert.AreEqual("patched-body", hit.EditedBody);
        Assert.AreEqual(System.Text.Encoding.UTF8.GetByteCount("patched-body"), hit.ContentLength);

        var wait = hit.WaitAsync();
        vm.Continue();
        Assert.AreEqual(BreakpointAction.Continue, await wait.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.IsNull(vm.Active);

        Assert.IsTrue(vm.TryEnter(new SessionSnapshot { Url = "https://e/" }, out var hit2));
        var waitAbort = hit2.WaitAsync();
        vm.Abort();
        Assert.AreEqual(BreakpointAction.Abort, await waitAbort.WaitAsync(TimeSpan.FromSeconds(2)));

        // EditBody with no active hit is a no-op
        vm.EditBody("ignored");
    }

    [TestMethod]
    public async Task BreakpointHit_ShortTimeout_AutoContinues()
    {
        var hit = new BreakpointHit(new SessionSnapshot { Url = "https://timeout/" }, TimeSpan.FromMilliseconds(40));
        var action = await hit.WaitAsync().WaitAsync(TimeSpan.FromSeconds(2));
        Assert.AreEqual(BreakpointAction.Continue, action);
    }
}
