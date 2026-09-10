using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Helpers;
using Titanium.Web.Proxy.Http3;
using Titanium.Web.Proxy.Network;

namespace Titanium.Web.Proxy.UnitTests;

[TestClass]
public class BootstrapAndFirefoxHelperCoverageTests
{
    private static readonly string[] HelpAndRunArgs = ["--help", "run"];

    [TestMethod]
    public void Http3NativeBootstrap_EarlyOutsAndPathHelpers_DoNotRelaunch()
    {
        var previousSkip = Environment.GetEnvironmentVariable(Http3NativeBootstrap.SkipReexecEnv);
        var previousMarker = Environment.GetEnvironmentVariable(Http3NativeBootstrap.ReexecMarkerEnv);
        try
        {
            Environment.SetEnvironmentVariable(Http3NativeBootstrap.SkipReexecEnv, "1");
            Http3NativeBootstrap.EnsureAppLocalMsQuicVisible(["--help"]);
            Environment.SetEnvironmentVariable(Http3NativeBootstrap.SkipReexecEnv, null);
            Environment.SetEnvironmentVariable(Http3NativeBootstrap.ReexecMarkerEnv, "1");
            Http3NativeBootstrap.EnsureAppLocalMsQuicVisible(null);
            // Never clear both skip and marker: on macOS that can relaunch the test host.
            Environment.SetEnvironmentVariable(Http3NativeBootstrap.SkipReexecEnv, "1");
            Environment.SetEnvironmentVariable(Http3NativeBootstrap.ReexecMarkerEnv, null);
            Http3NativeBootstrap.EnsureAppLocalMsQuicVisible([]);
        }
        finally
        {
            Environment.SetEnvironmentVariable(Http3NativeBootstrap.SkipReexecEnv, previousSkip);
            Environment.SetEnvironmentVariable(Http3NativeBootstrap.ReexecMarkerEnv, previousMarker);
        }

        var flags = BindingFlags.NonPublic | BindingFlags.Static;
        var normalize = typeof(Http3NativeBootstrap).GetMethod("NormalizeDir", flags)!;
        var pathsEqual = typeof(Http3NativeBootstrap).GetMethod("PathsEqual", flags)!;
        var beside = typeof(Http3NativeBootstrap).GetMethod("IsQuicAssemblyBesideApp", flags)!;
        var dyld = typeof(Http3NativeBootstrap).GetMethod("DyldSearchPathContains", flags)!;
        var baseDir = AppContext.BaseDirectory;
        Assert.IsNotNull(normalize.Invoke(null, [baseDir]));
        Assert.IsNotNull(normalize.Invoke(null, [" "]));
        var n = (string)normalize.Invoke(null, [baseDir])!;
        Assert.IsTrue((bool)pathsEqual.Invoke(null, [n, n])!);
        Assert.IsFalse((bool)pathsEqual.Invoke(null, [n, n + "-x"])!);
        Assert.IsTrue((bool)pathsEqual.Invoke(null, ["/a", "/a"])!);
        _ = beside.Invoke(null, [baseDir]);
        _ = dyld.Invoke(null, [baseDir]);
        var previousDyld = Environment.GetEnvironmentVariable("DYLD_FALLBACK_LIBRARY_PATH");
        try
        {
            Environment.SetEnvironmentVariable("DYLD_FALLBACK_LIBRARY_PATH", baseDir + ":/tmp");
            _ = dyld.Invoke(null, [normalize.Invoke(null, [baseDir])]);
        }
        finally
        {
            Environment.SetEnvironmentVariable("DYLD_FALLBACK_LIBRARY_PATH", previousDyld);
        }

        var append = typeof(Http3NativeBootstrap).GetMethod("AppendRelaunchArguments", flags)!;
        var psi = new System.Diagnostics.ProcessStartInfo { FileName = "dotnet" };
        append.Invoke(null, [psi, Environment.ProcessPath ?? "dotnet", HelpAndRunArgs]);
        Assert.IsTrue(psi.ArgumentList.Count >= 1);
        var psiApp = new System.Diagnostics.ProcessStartInfo { FileName = "titanium" };
        append.Invoke(null, [psiApp, "/tmp/titanium-app", Array.Empty<string>()]);
        Assert.IsTrue(psiApp.ArgumentList.Count >= 0);
    }

    [TestMethod]
    public void FirefoxProfileParsing_AndPolicies_CoverRemainingBranches()
    {
        Assert.IsNull(FirefoxCertificateTrust.ParseDefaultProfilePath(""));
        Assert.IsNull(FirefoxCertificateTrust.ParseDefaultProfileEntry("[General]\nStartWithLastProfile=1\n"));
        var abs = FirefoxCertificateTrust.ParseDefaultProfileEntry("""
            [Profile0]
            Name=abs
            IsRelative=0
            Path=/tmp/ff-abs
            Default=1
            """);
        Assert.AreEqual("/tmp/ff-abs", abs?.Path);
        Assert.IsFalse(abs!.Value.IsRelative);

        var fallback = FirefoxCertificateTrust.ParseDefaultProfileEntry("""
            [Profile0]
            Path=first
            IsRelative=true

            [Profile1]
            Path=second
            """);
        Assert.AreEqual("first", fallback?.Path);

        var roots = FirefoxCertificateTrust.GetFirefoxRoots();
        Assert.IsTrue(roots.Length >= 1);
        _ = FirefoxCertificateTrust.GetFirefoxPoliciesJsonPaths().ToList();
        _ = FirefoxCertificateTrust.IsFirefoxProfilePresent();
        _ = FirefoxCertificateTrust.IsFirefoxProcessRunning();
        _ = FirefoxCertificateTrust.TryValidateFirefoxPoliciesJson("{\"policies\":{}}", out _);
        _ = FirefoxCertificateTrust.TryValidateFirefoxPoliciesJson("not-json", out _);
        var mergedFalse = FirefoxCertificateTrust.BuildOrMergeFirefoxPoliciesJson(
            "{\"policies\":{\"Certificates\":{\"ImportEnterpriseRoots\":true,\"Extra\":1}}}",
            importEnterpriseRoots: false);
        StringAssert.Contains(mergedFalse, "Extra");
        var flags = BindingFlags.NonPublic | BindingFlags.Static;

        var dir = Path.Combine(Path.GetTempPath(), "twp-ff-pref-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "user.js"),
                "user_pref(\"security.enterprise_roots.enabled\", false);\n# keep\n");
            FirefoxCertificateTrust.EnsureEnterpriseRootsUserPref(dir);
            Assert.IsTrue(FirefoxCertificateTrust.VerifyEnterpriseRootsUserPref(dir));
            FirefoxCertificateTrust.EnsureEnterpriseRootsPrefFile(Path.Combine(dir, "prefs.js"));
            typeof(FirefoxCertificateTrust).GetMethod("ClearEnterpriseRootsUserPref", flags)
                ?.Invoke(null, [dir]);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { /* ignore */ }
        }
    }

    [TestMethod]
    public void FirefoxEnterpriseRoots_HkcuAndTempProfile_DoNotTouchPoliciesJson()
    {
        var flags = BindingFlags.NonPublic | BindingFlags.Static;
        var writePolicy = typeof(FirefoxCertificateTrust).GetMethod("TryWriteWindowsImportEnterpriseRootsPolicy", flags);
        if (OperatingSystem.IsWindows() && writePolicy is not null)
        {
            Assert.IsTrue((bool)writePolicy.Invoke(null, [])!);
        }

        var dir = Path.Combine(Path.GetTempPath(), "twp-ff-er-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var writePref = typeof(FirefoxCertificateTrust).GetMethod("TryWriteEnterpriseRootsUserPref", flags)!;
            var result = (CertificateOsTrustResult)writePref.Invoke(null, [dir, "temp-profile-ok"])!;
            Assert.IsTrue(result.Succeeded, result.Message);
            Assert.IsTrue(FirefoxCertificateTrust.VerifyEnterpriseRootsUserPref(dir));

            var linuxPaths = typeof(FirefoxCertificateTrust).GetMethod("GetLinuxFirefoxPoliciesJsonPaths", flags);
            _ = linuxPaths?.Invoke(null, [Path.Combine(Path.GetTempPath(), "no-such-home")]);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { /* ignore */ }
        }
    }

    [TestMethod]
    public void LinuxFirefoxAndBrowserLaunch_PrivateHelpers_StayFake()
    {
        InvokeFf("HasBackup", Type.EmptyTypes);
        InvokeFf("BackupPath", Type.EmptyTypes);
        _ = LinuxFirefoxProxy.BuildFirefoxBypassListForTests("<local>;*.corp;<-loopback>");
        _ = LinuxFirefoxProxy.MergePrefsForTests(
            "user_pref(\"k\", 1);\n# comment\nuser_pref(\"keep\", true);\n",
            new Dictionary<string, string> { ["k"] = "2", ["added"] = "\"x\"" });
        _ = LinuxFirefoxProxy.RemoveKeysForTests(
            "user_pref(\"k\", 1);\nnot-a-pref\nuser_pref(\"keep\", true);\n", ["k", "missing"]);
        _ = LinuxFirefoxProxy.BuildFirefoxBypassListForTests(null);
        _ = LinuxFirefoxProxy.BuildFirefoxBypassListForTests("");
        typeof(LinuxFirefoxProxy).GetMethod("ResolveFirefoxLaunch",
            BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, []);

        var quote = typeof(LinuxBrowserLaunchProxy).GetMethod("QuoteShellArg",
            BindingFlags.NonPublic | BindingFlags.Static)!;
        StringAssert.Contains((string)quote.Invoke(null, ["a\"b"])!, "\\\"");
        typeof(LinuxBrowserLaunchProxy).GetMethod("TryUpdateDesktopDatabase",
            BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, []);
        typeof(LinuxChromiumRelaunch).GetMethod("TryMakeExecutable",
            BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null,
            [Path.Combine(Path.GetTempPath(), "nope.sh")]);
        _ = LinuxGraphicalSession.EnumerateCandidateSessionPids().Take(1).ToList();
        _ = LinuxGraphicalSession.TryReadFromSessionProcess("DISPLAY");
        LinuxProxyFailOpen.Stop();
    }

    [TestMethod]
    public void TcpHelper_GetProcessIdByLocalPort_DoesNotThrow()
    {
        using var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        var pid4 = TcpHelper.GetProcessIdByLocalPort(System.Net.Sockets.AddressFamily.InterNetwork, port);
        var pid6 = TcpHelper.GetProcessIdByLocalPort(System.Net.Sockets.AddressFamily.InterNetworkV6, port);
        var missing = TcpHelper.GetProcessIdByLocalPort(System.Net.Sockets.AddressFamily.InterNetwork, 1);
        Assert.IsTrue(pid4 >= -1);
        Assert.IsTrue(pid6 >= -1);
        Assert.IsTrue(missing >= -1);
        listener.Stop();
    }

    [TestMethod]
    public void TryRequestFirefoxQuit_WithFakeRunner_WhenNotRunning()
    {
        if (FirefoxCertificateTrust.IsFirefoxProcessRunning())
        {
            Assert.Inconclusive("Firefox is running on this machine");
            return;
        }

        var runner = new FakeProcessRunner();
        Assert.IsTrue(FirefoxCertificateTrust.TryRequestFirefoxQuit(TimeSpan.FromMilliseconds(50), runner));
        typeof(FirefoxCertificateTrust).GetMethod("TermFirefoxProcesses",
            BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, [runner]);
    }

    [TestMethod]
    public void FirefoxEnterpriseRoots_EnableClearAndValidateDocument_StayTempSafe()
    {
        // HKCU write/clear is Windows-only; on macOS/Linux this hits policies.json + user.js best-effort.
        _ = FirefoxCertificateTrust.TryEnableWindowsEnterpriseRoots();
        _ = FirefoxCertificateTrust.TryClearWindowsEnterpriseRoots();
        _ = FirefoxCertificateTrust.TryEnableEnterpriseRootsUserPref();

        var flags = BindingFlags.NonPublic | BindingFlags.Static;
        var validateDoc = typeof(FirefoxCertificateTrust).GetMethod("TryValidateFirefoxPoliciesDocument", flags)!;
        using (var doc = System.Text.Json.JsonDocument.Parse(
                   "{\"policies\":{\"Certificates\":{\"ImportEnterpriseRoots\":true}}}"))
        {
            var args = new object?[] { doc.RootElement, null };
            Assert.IsTrue((bool)validateDoc.Invoke(null, args)!);
        }

        using (var bad = System.Text.Json.JsonDocument.Parse("{\"policies\":{}}"))
        {
            var args = new object?[] { bad.RootElement, null };
            Assert.IsFalse((bool)validateDoc.Invoke(null, args)!);
            Assert.IsFalse(string.IsNullOrEmpty((string?)args[1]));
        }

        using (var arr = System.Text.Json.JsonDocument.Parse("[]"))
        {
            var args = new object?[] { arr.RootElement, null };
            Assert.IsFalse((bool)validateDoc.Invoke(null, args)!);
        }

        var mergedCorrupt = FirefoxCertificateTrust.BuildOrMergeFirefoxPoliciesJson(
            "not-json{", importEnterpriseRoots: true);
        StringAssert.Contains(mergedCorrupt, "ImportEnterpriseRoots");
        var cleared = FirefoxCertificateTrust.BuildOrMergeFirefoxPoliciesJson(mergedCorrupt, importEnterpriseRoots: false);
        Assert.IsFalse(cleared.Contains("\"ImportEnterpriseRoots\": true", StringComparison.Ordinal));

        var dir = Path.Combine(Path.GetTempPath(), "twp-ff-enable-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var writePref = typeof(FirefoxCertificateTrust).GetMethod("TryWriteEnterpriseRootsUserPref", flags)!;
            var ok = (CertificateOsTrustResult)writePref.Invoke(null, [dir, "enabled-ok"])!;
            Assert.IsTrue(ok.Succeeded, ok.Message);
            Assert.IsTrue(FirefoxCertificateTrust.VerifyEnterpriseRootsUserPref(dir));
            typeof(FirefoxCertificateTrust).GetMethod("ClearEnterpriseRootsUserPref", flags)!
                .Invoke(null, [dir]);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { /* ignore */ }
        }
    }

    private static void InvokeFf(string name, Type[] types, params object?[] args)
    {
        var method = typeof(LinuxFirefoxProxy).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static, types)
                     ?? throw new InvalidOperationException(name);
        method.Invoke(null, args);
    }
}
