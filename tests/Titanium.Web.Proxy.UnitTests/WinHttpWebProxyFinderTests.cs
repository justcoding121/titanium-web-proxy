using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.Versioning;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Helpers.WinHttp;
using WinHttpNative = Titanium.Web.Proxy.Helpers.WinHttp.NativeMethods;

namespace Titanium.Web.Proxy.UnitTests;

/// <summary>
///     Phase E.15: <see cref="WinHttpWebProxyFinder.TryPickFirstUsableProxy" /> walks the ordered
///     PAC/auto-detect candidate list <c>WinHttpGetProxyForUrl</c> returns, rather than trusting index
///     0 unconditionally and crashing on a malformed entry.
/// </summary>
[SupportedOSPlatform("windows")]
[TestClass]
public class WinHttpWebProxyFinderTests
{
    [TestMethod]
    public void TryPickFirstUsableProxy_SingleValidEntry_Resolves()
    {
        var ok = WinHttpWebProxyFinder.TryPickFirstUsableProxy(new List<string> { "127.0.0.1:8080" },
            out var host, out var port);

        Assert.IsTrue(ok);
        Assert.AreEqual("127.0.0.1", host);
        Assert.AreEqual(8080, port);
    }

    [TestMethod]
    public void TryPickFirstUsableProxy_FirstEntryMalformed_FallsBackToNextValidEntry()
    {
        // Simulates a PAC script whose first fallback proxy string is garbage - previously this
        // crashed proxy resolution outright (an unguarded int.Parse on index 0) instead of trying the
        // remaining ordered entries.
        var ok = WinHttpWebProxyFinder.TryPickFirstUsableProxy(
            new List<string> { "not-a-valid:::entry", "127.0.0.1:8080" }, out var host, out var port);

        Assert.IsTrue(ok);
        Assert.AreEqual("127.0.0.1", host);
        Assert.AreEqual(8080, port);
    }

    [TestMethod]
    public void TryPickFirstUsableProxy_EntryWithNoPort_DefaultsToPort80()
    {
        var ok = WinHttpWebProxyFinder.TryPickFirstUsableProxy(new List<string> { "proxy.example.com" },
            out var host, out var port);

        Assert.IsTrue(ok);
        Assert.AreEqual("proxy.example.com", host);
        Assert.AreEqual(80, port);
    }

    [TestMethod]
    public void TryPickFirstUsableProxy_BracketedIPv6Entry_ParsesHostWithoutBrackets()
    {
        var ok = WinHttpWebProxyFinder.TryPickFirstUsableProxy(new List<string> { "[::1]:3128" },
            out var host, out var port);

        Assert.IsTrue(ok);
        Assert.AreEqual("::1", host);
        Assert.AreEqual(3128, port);
    }

    [TestMethod]
    public void TryPickFirstUsableProxy_AllEntriesMalformed_ReturnsFalse()
    {
        var ok = WinHttpWebProxyFinder.TryPickFirstUsableProxy(
            new List<string> { "::1:8080", "" }, out _, out _);

        Assert.IsFalse(ok);
    }

    [TestMethod]
    public void TryPickFirstUsableProxy_EmptyList_ReturnsFalse()
    {
        var ok = WinHttpWebProxyFinder.TryPickFirstUsableProxy(new List<string>(), out _, out _);

        Assert.IsFalse(ok);
    }

    [TestMethod]
    public void RemoveWhitespaces_StripsSpacesAndTabs()
    {
        var method = typeof(WinHttpWebProxyFinder).GetMethod("RemoveWhitespaces",
            BindingFlags.Static | BindingFlags.NonPublic)!;
        Assert.AreEqual("a:b:c", method.Invoke(null, [" a :\tb : c "]));
    }

    [TestMethod]
    public void AutoProxyErrorHelpers_ClassifyRecoverableFatalAndState()
    {
        var recoverable = typeof(WinHttpWebProxyFinder).GetMethod("IsRecoverableAutoProxyError",
            BindingFlags.Static | BindingFlags.NonPublic)!;
        var fatal = typeof(WinHttpWebProxyFinder).GetMethod("IsErrorFatalForAutoDetect",
            BindingFlags.Static | BindingFlags.NonPublic)!;
        var state = typeof(WinHttpWebProxyFinder).GetMethod("GetStateFromErrorCode",
            BindingFlags.Static | BindingFlags.NonPublic)!;

        var timeout = WinHttpNative.WinHttp.ErrorCodes.Timeout;
        Assert.IsTrue((bool)recoverable.Invoke(null, [timeout])!);
        Assert.IsTrue((bool)fatal.Invoke(null, [timeout])!);

        var badScript = WinHttpNative.WinHttp.ErrorCodes.BadAutoProxyScript;
        Assert.IsTrue((bool)recoverable.Invoke(null, [badScript])!);
        Assert.IsFalse((bool)fatal.Invoke(null, [badScript])!);

        Assert.AreEqual("DiscoveryFailure",
            state.Invoke(null, [WinHttpNative.WinHttp.ErrorCodes.AudodetectionFailed])!.ToString());
        Assert.AreEqual("DownloadFailure",
            state.Invoke(null, [WinHttpNative.WinHttp.ErrorCodes.UnableToDownloadScript])!.ToString());
        Assert.AreEqual("Completed", state.Invoke(null, [WinHttpNative.WinHttp.ErrorCodes.Success])!.ToString());
    }
}
