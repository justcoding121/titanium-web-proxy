using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Helpers;

namespace Titanium.Web.Proxy.UnitTests;

[TestClass]
[DoNotParallelize]
public class LinuxBrowserProxyCoverageTests
{
    [TestMethod]
    public void ChromeProfile_ApplyClearAndWatcherReassert_OnAnyOs()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var chromeRoot = Path.Combine(home, ".config", "google-chrome");
        var createdRoot = !Directory.Exists(chromeRoot);
        var profileDir = Path.Combine(chromeRoot, "Profile TWP-COV");
        var prefs = Path.Combine(profileDir, "Preferences");
        Directory.CreateDirectory(profileDir);
        File.WriteAllText(prefs, """{"homepage":"https://example.test"}""");
        try
        {
            var written = LinuxChromeProfileProxy.Apply("127.0.0.1", 18866);
            Assert.IsTrue(written >= 1, "expected at least the disposable Chrome profile to be rewritten");
            var text = File.ReadAllText(prefs);
            StringAssert.Contains(text, "fixed_servers");
            StringAssert.Contains(text, "127.0.0.1:18866");

            File.WriteAllText(prefs, """{"proxy":{"mode":"system"}}""");
            Thread.Sleep(500);
            var reasserted = File.ReadAllText(prefs);
            StringAssert.Contains(reasserted, "fixed_servers");

            LinuxChromeProfileProxy.Clear();
            LinuxProxyFailOpen.Stop();
            Thread.Sleep(500);
            LinuxProxyFailOpen.Stop();
        }
        finally
        {
            LinuxChromeProfileProxy.Clear();
            LinuxProxyFailOpen.Stop();
            try
            {
                if (Directory.Exists(profileDir))
                    Directory.Delete(profileDir, true);
                if (createdRoot && Directory.Exists(chromeRoot))
                    Directory.Delete(chromeRoot, true);
            }
            catch
            {
                // best-effort home cleanup
            }
        }
    }

    [TestMethod]
    public void BrowserLaunch_ApplyClearWritePoliciesAndDesktopHelpers()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var policyDir = Path.Combine(home, ".config", "google-chrome", "policies", "managed");
        var desktopDir = Path.Combine(home, ".local", "share", "applications");
        Directory.CreateDirectory(desktopDir);
        var fakeDesktop = Path.Combine(Path.GetTempPath(), "twp-chrome-" + Guid.NewGuid().ToString("N") + ".desktop");
        File.WriteAllText(fakeDesktop, "[Desktop Entry]\nName=Chrome\nExec=/usr/bin/google-chrome-stable %U\n");

        try
        {
            _ = LinuxBrowserLaunchProxy.Apply("10.0.0.8", 8866, "localhost;127.0.0.1;<local>");
            LinuxBrowserLaunchProxy.Clear();

            Assert.IsTrue(LinuxBrowserLaunchProxy.WritePolicies("127.0.0.1", 9050, [policyDir]) >= 1);
            Assert.IsTrue(File.Exists(Path.Combine(policyDir, LinuxBrowserLaunchProxy.PolicyFileName)));

            var dest = Path.Combine(desktopDir, "twp-cov.desktop");
            Invoke("WriteMarkedDesktopExec",
                [typeof(string), typeof(string), typeof(string), typeof(int)],
                fakeDesktop, dest, "127.0.0.1", 9050);
            Assert.IsTrue(File.Exists(dest));
            StringAssert.Contains(File.ReadAllText(dest), "--proxy-server=");
            Invoke("DeleteMarkedDesktopOverride", [typeof(string)], dest);

            Invoke("TryUpdateDesktopDatabase", Type.EmptyTypes);
            _ = LinuxBrowserLaunchProxy.ChromeProxyFlags("127.0.0.1", 9050);
            _ = LinuxBrowserLaunchProxy.InjectChromeProxyArgs(
                "Exec=/usr/bin/google-chrome %U", "127.0.0.1", 9050);

            _ = LinuxBrowserLaunchProxy.Apply("127.0.0.1", 9050);
            LinuxBrowserLaunchProxy.Clear();
        }
        finally
        {
            LinuxBrowserLaunchProxy.Clear();
            TryDelete(fakeDesktop);
            TryDelete(Path.Combine(policyDir, LinuxBrowserLaunchProxy.PolicyFileName));
        }
    }

    [TestMethod]
    public void FirefoxPrefsMergeRemoveBypassAndBackupHooks()
    {
        var existing = """
            user_pref("network.proxy.type", 5);
            user_pref("browser.startup.page", 1);
            # comment
            user_pref("network.proxy.http", "old");
            """;
        var merged = LinuxFirefoxProxy.MergePrefsForTests(existing, new Dictionary<string, string>
        {
            ["network.proxy.type"] = "1",
            ["network.proxy.http"] = "\"127.0.0.1\"",
            ["titanium.inspector.proxy.managed"] = "true",
        });
        StringAssert.Contains(merged, "network.proxy.type");
        StringAssert.Contains(merged, "127.0.0.1");
        StringAssert.Contains(merged, "browser.startup.page");

        var removed = LinuxFirefoxProxy.RemoveKeysForTests(merged, ["network.proxy.type", "titanium.inspector.proxy.managed"]);
        Assert.IsFalse(removed.Contains("titanium.inspector.proxy.managed", StringComparison.Ordinal));

        Assert.AreEqual("localhost, 127.0.0.1", LinuxFirefoxProxy.BuildFirefoxBypassListForTests(null));
        StringAssert.Contains(LinuxFirefoxProxy.BuildFirefoxBypassListForTests("localhost;*.corp"), "localhost");

        InvokeFf("BackupIfNeeded", [typeof(string)], existing);
        Assert.IsTrue((bool)InvokeFf("HasBackup", Type.EmptyTypes)!);
        InvokeFf("DeleteBackup", Type.EmptyTypes);
        Assert.IsFalse(LinuxFirefoxProxy.Apply("127.0.0.1", 8866));
        LinuxFirefoxProxy.Clear();
    }

    [TestMethod]
    public void ChromiumRelaunch_ClassifiesFamiliesAndBuildsScript()
    {
        Assert.AreEqual("Chrome", LinuxChromiumRelaunch.ClassifyFamilyForTests("/opt/google/chrome/chrome"));
        Assert.AreEqual("Chromium", LinuxChromiumRelaunch.ClassifyFamilyForTests("/usr/lib/chromium/chromium"));
        Assert.AreEqual("Brave", LinuxChromiumRelaunch.ClassifyFamilyForTests("/usr/bin/brave"));
        Assert.AreEqual("Edge", LinuxChromiumRelaunch.ClassifyFamilyForTests("/opt/microsoft/msedge/msedge"));
        Assert.IsNull(LinuxChromiumRelaunch.ClassifyFamilyForTests("/usr/bin/firefox"));
        _ = LinuxChromiumRelaunch.ResolveLaunchBinaryForExeForTests("/opt/google/chrome/chrome");
        Assert.IsFalse(LinuxChromiumRelaunch.TryRelaunchForProxyChange("127.0.0.1", 8866, enableProxy: true));
        Assert.IsFalse(LinuxChromiumRelaunch.TryRelaunchForProxyChange(null, 0, enableProxy: false));

        var scriptPath = Path.Combine(Path.GetTempPath(), "twp-relaunch-" + Guid.NewGuid().ToString("N") + ".sh");
        var family = typeof(LinuxChromiumRelaunch).GetNestedType("BrowserFamily", BindingFlags.NonPublic)!;
        var tupleType = typeof(ValueTuple<,>).MakeGenericType(typeof(int), family);
        var listType = typeof(List<>).MakeGenericType(tupleType);
        var mains = Activator.CreateInstance(listType)!;
        var tuple = Activator.CreateInstance(tupleType, 4242, Enum.Parse(family, "Chrome"))!;
        listType.GetMethod("Add")!.Invoke(mains, [tuple]);

        var script = (string)typeof(LinuxChromiumRelaunch)
            .GetMethod("BuildRelaunchScript", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null,
            [
                scriptPath,
                mains,
                new List<string> { "/usr/bin/google-chrome-stable" },
                "--proxy-server=http://127.0.0.1:8866 --restore-last-session",
                ":0",
                "unix:path=/tmp/dbus",
                "/tmp/.Xauthority",
                true,
            ])!;
        StringAssert.Contains(script, "google-chrome-stable");
        StringAssert.Contains(script, "4242");
        InvokeCr("TryMakeExecutable", [typeof(string)], scriptPath);
        InvokeCr("ShellQuote", [typeof(string)], "it's");
        _ = LinuxGraphicalSession.TryGetDisplay();
        _ = LinuxGraphicalSession.TryGetDbusSessionAddress();
        Assert.IsFalse(LinuxGraphicalSession.IsUsableDbusAddress(null));
        Assert.IsFalse(LinuxGraphicalSession.IsUsableDbusAddress("disabled:"));
        LinuxProxyFailOpen.Start("127.0.0.1", 1);
        LinuxProxyFailOpen.Stop();
    }

    [TestMethod]
    public void XfceHelpers_AndPolicyRejects_AndChromeMarkerCorruptPrefs()
    {
        Assert.IsFalse((bool)Invoke("WriteXfceWebBrowserHelper", [typeof(string), typeof(int)], "127.0.0.1", 8866)!);

        var rcPath = (string)Invoke("UserXfceHelpersRcPath", Type.EmptyTypes)!;
        var helperPath = (string)Invoke("UserXfceChromeHelperPath", Type.EmptyTypes)!;
        string? rcBackup = File.Exists(rcPath) ? File.ReadAllText(rcPath) : null;
        var rcExisted = File.Exists(rcPath);
        try
        {
            Assert.IsTrue((bool)Invoke("WriteXfceHelpersRc", Type.EmptyTypes)!);
            Invoke("RestoreXfceHelpersRc", Type.EmptyTypes);
        }
        finally
        {
            if (rcBackup is not null)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(rcPath)!);
                File.WriteAllText(rcPath, rcBackup);
            }
            else if (!rcExisted && File.Exists(rcPath))
            {
                TryDelete(rcPath);
            }

            TryDelete(helperPath);
        }

        Assert.IsFalse(LinuxBrowserLaunchProxy.TryValidatePolicyJson("[]", "127.0.0.1", 8866, out _));
        Assert.IsFalse(LinuxBrowserLaunchProxy.TryValidatePolicyJson("""{"ProxyServer":"http://127.0.0.1:8866"}""", "127.0.0.1", 8866, out _));
        Assert.IsFalse(LinuxBrowserLaunchProxy.TryValidatePolicyJson(
            """{"ProxyMode":"fixed_servers"}""", "127.0.0.1", 8866, out _));

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var roots = ((IEnumerable<string>)InvokeOn(
            typeof(LinuxChromeProfileProxy), "BrowserConfigRoots", [typeof(string)], home)!).ToList();
        Assert.IsTrue(roots.Count >= 8);
        StringAssert.Contains(string.Join('\n', roots), "google-chrome");

        var corrupt = Path.Combine(Path.GetTempPath(), "twp-prefs-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            File.WriteAllText(corrupt, "[1,2,3]");
            _ = LinuxChromeProfileProxy.TryApplyToFileForTests(corrupt, "127.0.0.1", 1);
            File.WriteAllText(corrupt, "{");
            Assert.IsFalse(LinuxChromeProfileProxy.TryApplyToFileForTests(corrupt, "127.0.0.1", 1));
        }
        finally
        {
            TryDelete(corrupt);
            TryDelete(corrupt + LinuxChromeProfileProxy.BackupSuffix);
        }

        InvokeOn(typeof(LinuxChromeProfileProxy), "WriteMarker", [typeof(string), typeof(int)], "127.0.0.1", 18866);
        object?[] readArgs = ["", 0];
        Assert.IsTrue((bool)InvokeOn(typeof(LinuxChromeProfileProxy), "TryReadMarker",
            [typeof(string).MakeByRefType(), typeof(int).MakeByRefType()], readArgs)!);
        Assert.AreEqual("127.0.0.1", readArgs[0]);
        var markerPath = (string?)InvokeOn(typeof(LinuxChromeProfileProxy), "MarkerPath", Type.EmptyTypes);
        if (!string.IsNullOrEmpty(markerPath))
            File.WriteAllText(markerPath, "{not-json");
        Assert.IsFalse((bool)InvokeOn(typeof(LinuxChromeProfileProxy), "TryReadMarker",
            [typeof(string).MakeByRefType(), typeof(int).MakeByRefType()], readArgs)!);
        InvokeOn(typeof(LinuxChromeProfileProxy), "DeleteMarker", Type.EmptyTypes);
    }

    private static object? Invoke(string name, Type[] types, params object?[] args) =>
        InvokeOn(typeof(LinuxBrowserLaunchProxy), name, types, args);

    private static object? InvokeFf(string name, Type[] types, params object?[] args) =>
        InvokeOn(typeof(LinuxFirefoxProxy), name, types, args);

    private static object? InvokeCr(string name, Type[] types, params object?[] args) =>
        InvokeOn(typeof(LinuxChromiumRelaunch), name, types, args);

    private static object? InvokeOn(Type type, string name, Type[] types, params object?[] args)
    {
        var method = type.GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static, types)
                     ?? throw new InvalidOperationException($"{type.Name}.{name}");
        try
        {
            return method.Invoke(null, args);
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            throw ex.InnerException;
        }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* ignore */ }
    }
}
