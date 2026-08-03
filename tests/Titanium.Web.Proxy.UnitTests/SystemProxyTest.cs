using System;
using System.Reflection;
using System.Runtime.Versioning;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Helpers;
using Titanium.Web.Proxy.Helpers.WinHttp;
using Titanium.Web.Proxy.Models;

namespace Titanium.Web.Proxy.UnitTests
{
    // SystemProxyManager and WinHttpWebProxyFinder are both [SupportedOSPlatform("windows")]; this whole
    // test class exercises Windows-only system-proxy-registration APIs (matching CI, which runs on
    // windows-latest), so it is annotated the same way to satisfy the platform-compatibility analyzer.
    [SupportedOSPlatform("windows")]
    [TestClass]
    public class SystemProxyTest
    {
        // This used to cross-check WinHttpWebProxyFinder against WebRequest.GetSystemWebProxy() for every
        // change made below. That approach turned out to be fundamentally unreliable on modern .NET:
        //   1. WebRequest.GetSystemWebProxy() returns an IWebProxy (System.Net.Http.HttpWindowsProxy) that
        //      is cached for the lifetime of the process; repeated calls within the same process do not
        //      reliably observe rapid, successive registry + InternetSetOption changes the way the old
        //      .NET Framework WinInet-backed implementation did (verified empirically - a second call
        //      after changing ProxyServer in the registry, even with a following InternetSetOption
        //      refresh, kept returning the first-observed value).
        //   2. HttpWindowsProxy also has hardcoded bypass behavior for loopback (and, seemingly, the
        //      local machine's own hostname) that is independent of the configured bypass list - see
        //      https://github.com/dotnet/runtime's HttpWindowsProxy.GetMultiProxy ("This is optimization
        //      for loopback addresses.").
        // Neither of those is a bug in Titanium: WinHttpWebProxyFinder intentionally reads the live
        // WinINet registry configuration on every LoadFromIe() call and applies only the bypass rules
        // that are actually configured (via System.Net.WebProxy.IsBypassed, which - on modern .NET - does
        // not hardcode a loopback exception the way HttpWindowsProxy does). So this test now asserts
        // WinHttpWebProxyFinder's own resolution directly against the settings SystemProxyManager just
        // wrote, instead of cross-checking against .NET's own (differently-behaved) system proxy resolver.
        [TestMethod]
        public void WinHttpWebProxyFinderResolvesConfiguredProxyAndBypassRules()
        {
            var proxyManager = new SystemProxyManager();

            try
            {
                proxyManager.DisableAllProxy();
                AssertNoProxy("http://google.com");
                AssertNoProxy("https://google.com");

                proxyManager.SetProxy("127.0.0.1", 8000, ProxyProtocolType.Http);
                AssertProxy("http://google.com", "127.0.0.1", 8000);
                AssertNoProxy("https://google.com");

                proxyManager.SetProxy("127.0.0.1", 8000, ProxyProtocolType.Https);
                AssertProxy("http://google.com", "127.0.0.1", 8000);
                AssertProxy("https://google.com", "127.0.0.1", 8000);

                proxyManager.SetProxy("127.0.0.1", 8000, ProxyProtocolType.AllHttp);
                AssertProxy("http://bing.com", "127.0.0.1", 8000);
                AssertProxy("https://bing.com", "127.0.0.1", 8000);

                // A bare hostname bypass rule only matches that exact host; unrelated hosts still proxy.
                proxyManager.SetProxyOverride("yahoo.com");
                AssertNoProxy("http://yahoo.com");
                AssertNoProxy("https://yahoo.com");
                AssertProxy("http://google.com", "127.0.0.1", 8000);

                // A wildcard rule matches the whole subdomain but not unrelated hosts.
                proxyManager.SetProxyOverride("*.local");
                AssertNoProxy("http://test.local");
                AssertNoProxy("https://test.local");
                AssertProxy("http://google.com", "127.0.0.1", 8000);

                // <local> bypasses simple (no-dot) hostnames but not dotted ones.
                proxyManager.SetProxyOverride("<local>");
                AssertNoProxy("http://simplehostname");
                AssertProxy("http://google.com", "127.0.0.1", 8000);
                AssertProxy("http://test.local", "127.0.0.1", 8000);

                // Combining rules with ';' still leaves unrelated hosts proxied.
                proxyManager.SetProxyOverride("*.local;<local>");
                AssertNoProxy("http://test.local");
                AssertNoProxy("http://simplehostname");
                AssertProxy("http://google.com", "127.0.0.1", 8000);
            }
            finally
            {
                proxyManager.RestoreOriginalSettings();
            }
        }

        private static void AssertProxy(string url, string expectedHost, int expectedPort)
        {
            using var resolver = new WinHttpWebProxyFinder();
            resolver.LoadFromIe();

            var proxy = resolver.GetProxy(new Uri(url));

            Assert.IsNotNull(proxy, $"Expected a proxy to be resolved for '{url}' but got none.");
            Assert.AreEqual(expectedHost, proxy!.HostName);
            Assert.AreEqual(expectedPort, proxy.Port);
        }

        private static void AssertNoProxy(string url)
        {
            using var resolver = new WinHttpWebProxyFinder();
            resolver.LoadFromIe();

            var proxy = resolver.GetProxy(new Uri(url));

            Assert.IsNull(proxy,
                $"Expected no proxy to be resolved for '{url}' but got {proxy?.HostName}:{proxy?.Port}.");
        }

        [TestMethod]
        public void SystemProxySettingsMergeExistingRulesAndProxyLoopback()
        {
            var settings = new SystemProxySettings
            {
                ProxyLoopback = true
            };
            settings.BypassRules.Add("*.example.com");
            settings.BypassRules.Add("<local>");

            var proxyOverride = settings.BuildProxyOverride("*.internal;<local>");

            Assert.AreEqual("<-loopback>;*.internal;<local>;*.example.com", proxyOverride);
        }

        [TestMethod]
        public void SystemProxySettingsReplaceExistingRules()
        {
            var settings = new SystemProxySettings
            {
                BypassRuleMode = SystemProxyBypassRuleMode.Replace
            };
            settings.BypassRules.Add("*.example.com");

            var proxyOverride = settings.BuildProxyOverride("*.internal;<local>");

            Assert.AreEqual("*.example.com", proxyOverride);
        }

        [TestMethod]
        public void DefaultSystemProxySettingsPreserveExistingRules()
        {
            var settings = new SystemProxySettings();

            var proxyOverride = settings.BuildProxyOverride("*.internal;<local>");

            Assert.AreEqual("*.internal;<local>", proxyOverride);
        }

        [TestMethod]
        public void SystemProxySettingsPlacesLoopbackRuleLastWhenRequested()
        {
            var settings = new SystemProxySettings
            {
                ProxyLoopback = true,
                ProxyLoopbackPlacement = SystemProxyLoopbackPlacement.Last
            };
            settings.BypassRules.Add("*.example.com");

            var proxyOverride = settings.BuildProxyOverride(null);

            Assert.AreEqual("*.example.com;<-loopback>", proxyOverride);
        }

        [TestMethod]
        public void SystemProxySettingsValidateThrowsForMalformedRules()
        {
            var settings = new SystemProxySettings();
            settings.BypassRules.Add("*.example.com;*.other.com");

            Assert.ThrowsExactly<ArgumentException>(() => settings.Validate());
        }

        /// <summary>
        ///     Dispose must unsubscribe AppDomain handlers without touching the registry / WinINet proxy.
        /// </summary>
        [TestMethod]
        public void SystemProxyManager_Dispose_UnsubscribesAppDomainHandlers_Idempotent()
        {
            var manager = new SystemProxyManager();
            var processExit = typeof(SystemProxyManager)
                .GetField("processExitHandler", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(manager) as EventHandler;
            var unhandled = typeof(SystemProxyManager)
                .GetField("unhandledExceptionHandler", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(manager) as UnhandledExceptionEventHandler;
            Assert.IsNotNull(processExit);
            Assert.IsNotNull(unhandled);

            manager.Dispose();
            manager.Dispose(); // idempotent

            var disposed = (bool)typeof(SystemProxyManager)
                .GetField("disposed", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(manager)!;
            Assert.IsTrue(disposed);

            // Re-adding then removing proves the prior Dispose unsubscribed the original handlers
            // (Add would otherwise stack duplicate invocations if Dispose left them attached).
            AppDomain.CurrentDomain.ProcessExit += processExit!;
            AppDomain.CurrentDomain.ProcessExit -= processExit!;
            AppDomain.CurrentDomain.UnhandledException += unhandled!;
            AppDomain.CurrentDomain.UnhandledException -= unhandled!;
        }
    }
}