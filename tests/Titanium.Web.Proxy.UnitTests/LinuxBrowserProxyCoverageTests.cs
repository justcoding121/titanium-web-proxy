using System;
using System.Collections.Generic;
using System.Diagnostics;
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
    public async Task ChromeProfile_ApplyClearAndWatcherReassert_OnAnyOs()
    {
        LiveProxySessionGuard.InconclusiveIfDesktopDependsOnInspector();
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
            StringAssert.Contains(text, "\"system\"");
            // mode=system must not pin a dead fixed endpoint after Inspector releases the port.
            Assert.IsFalse(text.Contains("127.0.0.1:18866", StringComparison.Ordinal),
                "Preferences must not store the live listener URL beside mode=system");

            File.WriteAllText(prefs, """{"proxy":{"mode":"direct"}}""");
            await Task.Delay(500);
            var reasserted = File.ReadAllText(prefs);
            StringAssert.Contains(reasserted, "\"system\"");

            LinuxChromeProfileProxy.Clear();
            LinuxProxyFailOpen.Stop();
            await Task.Delay(500);
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
        LiveProxySessionGuard.InconclusiveIfDesktopDependsOnInspector();
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
                [typeof(string), typeof(string), typeof(string), typeof(int), typeof(bool)],
                fakeDesktop, dest, "127.0.0.1", 9050, false);
            Assert.IsTrue(File.Exists(dest));
            // The desktop entry now points at a launcher script that carries the proxy flags.
            StringAssert.Contains(File.ReadAllText(dest), "DBusActivatable=false");
            var script = Path.Combine(home, ".local", "share", "titanium-inspector", "launchers", "twp-cov.sh");
            StringAssert.Contains(File.ReadAllText(script), "--proxy-server=");
            Invoke("DeleteMarkedDesktopOverride", [typeof(string)], dest);
            TryDelete(script);

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
        LiveProxySessionGuard.InconclusiveIfDesktopDependsOnInspector();
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
        LiveProxySessionGuard.InconclusiveIfDesktopDependsOnInspector();
        Assert.AreEqual("Chrome", LinuxChromiumRelaunch.ClassifyFamilyForTests("/opt/google/chrome/chrome"));
        Assert.AreEqual("Chromium", LinuxChromiumRelaunch.ClassifyFamilyForTests("/usr/lib/chromium/chromium"));
        Assert.AreEqual("Brave", LinuxChromiumRelaunch.ClassifyFamilyForTests("/usr/bin/brave"));
        Assert.AreEqual("Edge", LinuxChromiumRelaunch.ClassifyFamilyForTests("/opt/microsoft/msedge/msedge"));
        Assert.AreEqual("Opera", LinuxChromiumRelaunch.ClassifyFamilyForTests("/usr/bin/opera"));
        Assert.AreEqual("Opera", LinuxChromiumRelaunch.ClassifyFamilyForTests("/usr/bin/opera-stable"));
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
        StringAssert.Contains(script, "exec(base64.b64decode(sys.argv[1]))");
        Assert.IsFalse(script.Contains("{ShellQuote(", StringComparison.Ordinal),
            "$$ raw string must interpolate ShellQuote for the loopback-strip arg");
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
        Assert.IsFalse((bool)Invoke("WriteXfceWebBrowserHelper", [typeof(string), typeof(int), typeof(bool)], "127.0.0.1", 8866, false)!);

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

    [TestMethod]
    public void MachinePolicySudoDesktopAndProfiles_CoverLinuxOnlyBranches()
    {
        var root = Path.Combine(Path.GetTempPath(), "twp-linux-cov-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var tool = FakeOsExecutable.Ensure("twp-sonar-fake-tool-proxy.exe");
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var rcPath = (string)Invoke("UserXfceHelpersRcPath", Type.EmptyTypes)!;
        var helperPath = (string)Invoke("UserXfceChromeHelperPath", Type.EmptyTypes)!;
        string? rcBackup = File.Exists(rcPath) ? File.ReadAllText(rcPath) : null;
        var rcExisted = File.Exists(rcPath);
        try
        {
            var vendorRoot = Path.Combine(root, "etc-chrome");
            var binary = Path.Combine(root, "chrome-bin");
            Directory.CreateDirectory(vendorRoot);
            File.WriteAllText(binary, "");
            (string Root, string[] Binaries)[] vendors = [(vendorRoot, [binary])];
            (string Root, string[] Binaries)[] missing = [(Path.Combine(root, "absent-root"), [Path.Combine(root, "absent-bin")])];

            Assert.AreEqual(0, LinuxBrowserLaunchProxy.MachinePolicyFilesForTests(installedOnly: true, missing).Count);
            var files = LinuxBrowserLaunchProxy.MachinePolicyFilesForTests(installedOnly: false, vendors);
            Assert.AreEqual(1, files.Count);
            Assert.AreEqual(1, LinuxBrowserLaunchProxy.MachinePolicyFilesForTests(installedOnly: true, vendors).Count);

            var policy = files[0];
            Assert.IsFalse((bool)Invoke("MachinePolicyMatches", [typeof(string), typeof(string)], policy, "{}\n")!);
            Directory.CreateDirectory(Path.GetDirectoryName(policy)!);
            File.WriteAllText(policy, "{}\n");
            Assert.IsTrue((bool)Invoke("MachinePolicyMatches", [typeof(string), typeof(string)], policy, "{}\n")!);
            Assert.IsFalse((bool)Invoke("MachinePolicyMatches", [typeof(string), typeof(string)], vendorRoot, "{}\n")!);

            LinuxBrowserLaunchProxy.SyncMachinePoliciesForTests(null, missing);
            LinuxBrowserLaunchProxy.SyncMachinePoliciesForTests(null, vendors);
            LinuxBrowserLaunchProxy.SyncMachinePoliciesForTests("{\"ProxyMode\":\"fixed_servers\"}\n", missing);
            LinuxBrowserLaunchProxy.SyncMachinePoliciesForTests("{\"ProxyMode\":\"fixed_servers\"}\n", vendors);
            LinuxBrowserLaunchProxy.SyncMachinePoliciesForTests(null, [("C:\\bad|path", [])]);

            LinuxBrowserLaunchProxy.TrySudoNonInteractiveForTests(["echo"], "{\"a\":1}", tool, 2000);
            LinuxBrowserLaunchProxy.TrySudoNonInteractiveForTests(["SLEEP"], null, tool, 300);
            LinuxBrowserLaunchProxy.TrySudoNonInteractiveForTests(["rm"], null, Path.Combine(root, "missing-sudo.exe"), 200);

            Assert.IsNull(LinuxBrowserLaunchProxy.BuildDesktopOverride(
                "[Desktop Entry]\nName=Nope\n", Path.Combine(root, "launch.sh"), "127.0.0.1", 9));
            Assert.IsNull(LinuxBrowserLaunchProxy.BuildDesktopOverride(
                "[Desktop Entry]\nExec=\n", Path.Combine(root, "launch.sh"), "127.0.0.1", 9));
            Assert.IsNotNull(LinuxBrowserLaunchProxy.BuildDesktopOverride(
                "[Desktop Entry]\nExec=\"/opt/google/chrome/chrome\" %U\n",
                Path.Combine(root, "launch.sh"), "127.0.0.1", 9));
            Assert.IsNotNull(LinuxBrowserLaunchProxy.BuildDesktopOverride(
                "[Desktop Entry]\nExec=/usr/bin/google-chrome %U\nExec=/usr/bin/chromium %U\n",
                Path.Combine(root, "launch.sh"), "127.0.0.1", 9, proxyLoopback: true));

            var blank = Path.Combine(root, "blank.desktop");
            File.WriteAllText(blank, "[Desktop Entry]\nName=Nope\n");
            Assert.IsFalse((bool)Invoke(
                "WriteMarkedDesktopExec",
                [typeof(string), typeof(string), typeof(string), typeof(int), typeof(bool)],
                blank, Path.Combine(root, "out.desktop"), "127.0.0.1", 9, false)!);

            var desktop = Path.Combine(root, "google-chrome.desktop");
            File.WriteAllText(desktop, "[Desktop Entry]\nName=Chrome\nExec=/usr/bin/google-chrome-stable %U\n");
            Assert.IsTrue(LinuxBrowserLaunchProxy.WriteBrowserDesktopOverridesForTests(
                "127.0.0.1", 9050, proxyLoopback: true, [desktop]) >= 1);

            var launcherDir = (string)Invoke("LauncherDirectory", Type.EmptyTypes)!;
            Directory.CreateDirectory(launcherDir);
            var marked = Path.Combine(launcherDir, "google-chrome.sh");
            var plain = Path.Combine(launcherDir, "twp-cov-plain.sh");
            File.WriteAllText(marked,
                "#!/bin/sh\n# " + LinuxBrowserLaunchProxy.DesktopMarker +
                "\nexec \"/usr/bin/google-chrome-stable\" --proxy-server=http://127.0.0.1:9 \"$@\"\n");
            File.WriteAllText(plain, "#!/bin/sh\necho keep\n");
            Invoke("NeutralizeLauncherScripts", Type.EmptyTypes);
            Assert.IsTrue(File.Exists(marked));
            var neutralized = File.ReadAllText(marked);
            StringAssert.Contains(neutralized, "exec \"/usr/bin/google-chrome-stable\"");
            Assert.IsFalse(neutralized.Contains("--proxy-server=", StringComparison.Ordinal));
            Assert.IsTrue(File.Exists(plain));

            var locked = Path.Combine(launcherDir, "twp-cov-locked.sh");
            File.WriteAllText(locked,
                "#!/bin/sh\n# " + LinuxBrowserLaunchProxy.DesktopMarker +
                "\nexec \"/usr/bin/google-chrome-stable\" --proxy-server=http://127.0.0.1:9 \"$@\"\n");
            using (new FileStream(locked, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                Invoke("NeutralizeLauncherScripts", Type.EmptyTypes);
            }

            TryDelete(marked);
            TryDelete(plain);
            TryDelete(locked);

            var script = Path.Combine(root, "mode.sh");
            File.WriteAllText(script, "#!/bin/sh\n");
            LinuxBrowserLaunchProxy.TryMakeExecutableForTests(script);

            var chrome = Path.Combine(root, "google-chrome-stable");
            File.WriteAllText(chrome, "");
            Assert.IsTrue(LinuxBrowserLaunchProxy.WriteXfceWebBrowserHelperForTests(
                "127.0.0.1", 9050, proxyLoopback: true, [chrome]));

            var profileHome = Path.Combine(root, "profile-home");
            LinuxChromeProfileProxy.EnsureInstalledDefaultProfiles("");
            LinuxChromeProfileProxy.EnsureInstalledDefaultProfiles("   ");
            LinuxChromeProfileProxy.EnsureInstalledDefaultProfiles(profileHome);
            var markerA = Path.Combine(root, "marker-a");
            var markerB = Path.Combine(root, "marker-b");
            File.WriteAllText(markerA, "");
            File.WriteAllText(markerB, "");
            LinuxChromeProfileProxy.EnsureInstalledProfilesForTests(profileHome,
            [
                (markerA, "google-chrome"),
                (markerB, "google-chrome"),
                (Path.Combine(root, "absent-marker"), "chromium"),
            ]);
            Assert.IsTrue(File.Exists(Path.Combine(profileHome, ".config", "google-chrome", "First Run")));

            var loopback = typeof(LinuxChromeProfileProxy).GetField(
                "_activeLoopback", BindingFlags.NonPublic | BindingFlags.Static)!;
            var prefs = Path.Combine(root, "Preferences");
            File.WriteAllText(prefs, "{}");
            loopback.SetValue(null, true);
            try
            {
                Assert.IsTrue(LinuxChromeProfileProxy.TryApplyToFileForTests(prefs, "127.0.0.1", 9));
                StringAssert.Contains(File.ReadAllText(prefs), "bypass_list");
            }
            finally
            {
                loopback.SetValue(null, false);
            }
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
            TryDelete(Path.Combine(home, ".local", "share", "applications", "google-chrome.desktop"));
            try
            {
                if (Directory.Exists(root))
                    Directory.Delete(root, true);
            }
            catch
            {
                // best-effort temp cleanup
            }
        }
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

    [TestMethod]
    public void MachinePolicyFile_AndLauncherNeutralize_StayInsideTheTempHome()
    {
        var home = Path.Combine(Path.GetTempPath(), "twp-policy-" + Guid.NewGuid().ToString("N"));
        var root = Path.Combine(home, "vendor");
        Directory.CreateDirectory(Path.Combine(root, "policies", "managed"));
        var owned = Path.Combine(root, "policies", "managed", "titanium-inspector-ca.json");
        File.WriteAllText(owned, "{}");
        var launchers = Path.Combine(home, ".local", "share", "titanium-inspector", "launchers");
        Directory.CreateDirectory(launchers);
        File.WriteAllText(
            Path.Combine(launchers, "google-chrome.sh"),
            "# X-Titanium-Inspector-Proxy=true\n");
        File.WriteAllText(
            Path.Combine(launchers, "chromium.sh"),
            "# X-Titanium-Inspector-Proxy=true\nexec \"/usr/bin/chromium\" --flag\n");
        File.WriteAllText(
            Path.Combine(launchers, "brave-browser.sh"),
            "# X-Titanium-Inspector-Proxy=true\n");
        File.WriteAllText(
            Path.Combine(launchers, "microsoft-edge.sh"),
            "# X-Titanium-Inspector-Proxy=true\n");
        File.WriteAllText(
            Path.Combine(launchers, "opera.sh"),
            "# X-Titanium-Inspector-Proxy=true\n");
        File.WriteAllText(
            Path.Combine(launchers, "chromium-browser.sh"),
            "# X-Titanium-Inspector-Proxy=true\nexec /usr/bin/chromium --flag\n");
        var chromeDefault = Path.Combine(home, ".config", "google-chrome", "Default");
        Directory.CreateDirectory(chromeDefault);
        var chromePrefs = Path.Combine(chromeDefault, "Preferences");
        File.WriteAllText(chromePrefs, """{"proxy":{"mode":"fixed_servers","server":"127.0.0.1:8080"}}""");
        File.WriteAllText(chromePrefs + ".titanium-inspector-proxy.bak", "null");
        var firefoxProfile = Path.Combine(home, ".mozilla", "firefox", "profile.default");
        Directory.CreateDirectory(firefoxProfile);
        File.WriteAllText(
            Path.Combine(firefoxProfile, "prefs.js"),
            "user_pref(\"titanium.inspector.proxy.managed\", true);\nuser_pref(\"network.proxy.http\", \"127.0.0.1\");\n");
        var firefoxSnap = Path.Combine(home, "snap", "firefox", "common", ".mozilla", "firefox", "other");
        Directory.CreateDirectory(firefoxSnap);
        File.WriteAllText(
            Path.Combine(firefoxSnap, "prefs.js"),
            "user_pref(\"network.proxy.ssl\", \"localhost\");\n");
        Directory.CreateDirectory(Path.Combine(home, ".config", "TitaniumInspector"));
        File.WriteAllText(
            Path.Combine(home, ".config", "TitaniumInspector", "firefox-proxy-backup.json"),
            """{"network.proxy.type":"0"}""");
        var apps = Path.Combine(home, ".local", "share", "applications");
        Directory.CreateDirectory(apps);
        File.WriteAllText(
            Path.Combine(apps, "google-chrome.desktop"),
            "[Desktop Entry]\nX-Titanium-Inspector-Proxy=true\n");
        var absentRoot = Path.Combine(home, "absent-vendor");
        const string policyJson = """{"CACertificates":[]}""";
        var previousHome = CoverageOs.HomeForTests;
        var previousLinux = CoverageOs.TreatAsLinuxForTests;
        var previousResolve = UnixProcessPath.ResolveOverrideForTests;
        try
        {
            CoverageOs.HomeForTests = home;
            CoverageOs.TreatAsLinuxForTests = true;
            UnixProcessPath.ResolveOverrideForTests = _ => null;
            LinuxBrowserLaunchProxy.SyncMachinePolicyFileForTests(
                "titanium-inspector-ca.json",
                policyJson,
                [(root, new[] { Path.Combine(root, "missing-bin") })]);
            LinuxBrowserLaunchProxy.SyncMachinePolicyFileForTests(
                "titanium-inspector-ca.json",
                policyJson,
                [(absentRoot, Array.Empty<string>())]);
            File.WriteAllText(owned, policyJson);
            LinuxBrowserLaunchProxy.SyncMachinePolicyFileForTests(
                "titanium-inspector-ca.json",
                policyJson,
                [(root, Array.Empty<string>())]);
            LinuxBrowserLaunchProxy.SyncMachinePolicyFileForTests(
                "titanium-inspector-ca.json",
                null,
                [(root, Array.Empty<string>())]);
            using (var rsa = System.Security.Cryptography.RSA.Create(2048))
            {
                var request = new System.Security.Cryptography.X509Certificates.CertificateRequest(
                    "CN=twp-cov",
                    rsa,
                    System.Security.Cryptography.HashAlgorithmName.SHA256,
                    System.Security.Cryptography.RSASignaturePadding.Pkcs1);
                using var cert = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
                LinuxBrowserLaunchProxy.ApplyCaCertificatesPolicy(cert);
                LinuxBrowserLaunchProxy.RemoveCaCertificatesPolicy();
            }

            Assert.IsFalse(LinuxInspectorProcess.IsRunning());
            Assert.IsFalse(LinuxInspectorProcess.IsRunning(Path.Combine(home, "missing-proc")));
            LinuxFirefoxProxy.Clear();
            LinuxBrowserLaunchProxy.ClearInstalledHooks();
            var rewritten = File.ReadAllText(Path.Combine(launchers, "google-chrome.sh"));
            StringAssert.Contains(rewritten, "exec ");
            Assert.IsFalse(File.Exists(Path.Combine(apps, "google-chrome.desktop")));
        }
        finally
        {
            CoverageOs.HomeForTests = previousHome;
            CoverageOs.TreatAsLinuxForTests = previousLinux;
            UnixProcessPath.ResolveOverrideForTests = previousResolve;
            try { Directory.Delete(home, true); } catch { /* best-effort */ }
        }
    }
}

internal static class FakeOsExecutable
{
    private static readonly object Gate = new();

    public static string Ensure(string fileName)
    {
        var exe = Path.Combine(Path.GetTempPath(), fileName);
        lock (Gate)
        {
            if (File.Exists(exe))
                return exe;

            var sourcePath = Path.Combine(Path.GetTempPath(), fileName + ".cs");
            File.WriteAllText(sourcePath, """
                using System;
                public class Program {
                  public static int Main(string[] args) {
                    var joined = string.Join(" ", args);
                    if (joined.IndexOf("SLEEP", StringComparison.Ordinal) >= 0) {
                      System.Threading.Thread.Sleep(60000);
                      return 0;
                    }
                    if (joined.IndexOf("FAILSILENT", StringComparison.Ordinal) >= 0)
                      return 1;
                    if (joined.IndexOf("FAIL", StringComparison.Ordinal) >= 0) {
                      Console.Error.WriteLine("denied");
                      return 1;
                    }
                    return 0;
                  }
                }
                """);
            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
            };
            psi.ArgumentList.Add("-NoProfile");
            psi.ArgumentList.Add("-NonInteractive");
            psi.ArgumentList.Add("-Command");
            psi.ArgumentList.Add(
                $"Add-Type -OutputAssembly '{exe}' -OutputType ConsoleApplication -TypeDefinition (Get-Content -Raw -LiteralPath '{sourcePath}')");
            using var process = Process.Start(psi)
                ?? throw new InvalidOperationException("Could not start powershell to build the fake tool.");
            if (!process.WaitForExit(30_000) || !File.Exists(exe))
            {
                throw new InvalidOperationException(
                    "Fake tool was not compiled: " + process.StandardError.ReadToEnd());
            }

            return exe;
        }
    }
}
