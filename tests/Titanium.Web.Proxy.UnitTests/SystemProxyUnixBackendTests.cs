using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Runtime.Versioning;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy;
using Titanium.Web.Proxy.Helpers;
using Titanium.Web.Proxy.Models;

namespace Titanium.Web.Proxy.UnitTests;

[TestClass]
public class UnixProxyBypassMapperTests
{
    [TestMethod]
    public void ToUnixBypassHosts_DropsLoopback_MapsLocal()
    {
        var hosts = UnixProxyBypassMapper.ToUnixBypassHosts("<-loopback>;<local>;*.example.com");
        CollectionAssert.Contains((System.Collections.ICollection)hosts, "*.local");
        CollectionAssert.Contains((System.Collections.ICollection)hosts, "*.example.com");
        CollectionAssert.DoesNotContain((System.Collections.ICollection)hosts, "<-loopback>");
    }

    [TestMethod]
    public void ToGsettingsArray_FormatsEntries()
    {
        var array = UnixProxyBypassMapper.ToGsettingsArray("localhost;*.corp");
        StringAssert.Contains(array, "'localhost'");
        StringAssert.Contains(array, "'*.corp'");
    }

    [TestMethod]
    public void ToNoProxyEnv_OmitsLocalhost_WhenLoopbackRulePresent()
    {
        var env = UnixProxyBypassMapper.ToNoProxyEnv("<-loopback>;*.corp");
        Assert.IsFalse(env.Contains("localhost", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(env.Contains("127.0.0.1", StringComparison.Ordinal));
        StringAssert.Contains(env, "*.corp");
    }

    [TestMethod]
    public void ToNoProxyEnv_ExplicitLoopbackFalse_IncludesLocalhost()
    {
        var env = UnixProxyBypassMapper.ToNoProxyEnv("*.corp", proxyLoopback: false);
        StringAssert.Contains(env, "localhost");
        StringAssert.Contains(env, "127.0.0.1");
    }

    [TestMethod]
    public void IsLocalHost_RecognizesLoopback()
    {
        Assert.IsTrue(UnixProxyBypassMapper.IsLocalHost("localhost"));
        Assert.IsTrue(UnixProxyBypassMapper.IsLocalHost("127.0.0.1"));
        Assert.IsFalse(UnixProxyBypassMapper.IsLocalHost("example.com"));
    }
}

[TestClass]
[SupportedOSPlatform("macos")]
[SupportedOSPlatform("osx")]
public class MacOsSystemProxyBackendTests
{
    [TestMethod]
    public void SetProxy_InvokesNetworkSetup_ForHttpAndHttps()
    {
        var runner = new FakeProcessRunner();
        runner.When("networksetup", "-listallnetworkservices",
            "An asterisk (*) denotes that a network service is disabled.\nWi-Fi\n*Ethernet\n");
        runner.When("networksetup", "-getwebproxy", "Enabled: No\nServer: \nPort: 0\n");
        runner.When("networksetup", "-getsecurewebproxy", "Enabled: No\nServer: \nPort: 0\n");
        runner.When("networksetup", "-getproxybypassdomains",
            "There aren't any bypass domains currently set.\n");
        runner.DefaultSuccess = true;

        using var backend = new MacOsSystemProxyBackend(runner, new FakeElevationPrompt());
        backend.SetProxy("127.0.0.1", 8000, ProxyProtocolType.AllHttp, "localhost;*.local");

        Assert.IsTrue(runner.Commands.Exists(c =>
            c.Contains("-setwebproxy") && c.Contains("127.0.0.1") && c.Contains("8000")));
        Assert.IsTrue(runner.Commands.Exists(c => c.Contains("-setsecurewebproxy")));
        Assert.IsTrue(runner.Commands.Exists(c => c.Contains("-setproxybypassdomains")));
        Assert.IsFalse(runner.Commands.Exists(c => c.Contains("\"Ethernet\"")),
            "Disabled (*Ethernet) services must be skipped");
    }

    [TestMethod]
    public void SetProxy_Elevates_WhenNetworkSetupDenies()
    {
        var runner = new FakeProcessRunner();
        runner.When("networksetup", "-listallnetworkservices", "Wi-Fi\n");
        runner.When("networksetup", "-getwebproxy", "Enabled: No\nServer:\nPort: 0\n");
        runner.When("networksetup", "-getsecurewebproxy", "Enabled: No\nServer:\nPort: 0\n");
        runner.When("networksetup", "-getproxybypassdomains", "Empty\n");
        runner.FailMatching = "-setwebproxy";
        runner.FailError = "You must be an administrator to perform this operation.";

        var elevation = new FakeElevationPrompt();
        using var backend = new MacOsSystemProxyBackend(runner, elevation);
        backend.SetProxy("127.0.0.1", 8000, ProxyProtocolType.Http, null);

        Assert.IsTrue(elevation.Calls.Count > 0);
        Assert.IsTrue(elevation.Calls[0].FileName.Contains("networksetup"));
    }
}

[TestClass]
public class SystemProxyHostnameTests
{
    [TestMethod]
    public void FormatSystemProxyHostname_UsesIpv4LiteralForLoopbackAndAny()
    {
        Assert.AreEqual("127.0.0.1", ProxyServer.FormatSystemProxyHostname(IPAddress.Loopback));
        Assert.AreEqual("127.0.0.1", ProxyServer.FormatSystemProxyHostname(IPAddress.Any));
        Assert.AreEqual("::1", ProxyServer.FormatSystemProxyHostname(IPAddress.IPv6Loopback));
        Assert.AreEqual("::1", ProxyServer.FormatSystemProxyHostname(IPAddress.IPv6Any));
        Assert.AreEqual("192.168.1.10", ProxyServer.FormatSystemProxyHostname(IPAddress.Parse("192.168.1.10")));
    }
}

[TestClass]
[SupportedOSPlatform("linux")]
public class LinuxSystemProxyBackendTests
{
    [TestMethod]
    public void IsUnusableDbusAddress_DetectsPoisonedAndEmpty()
    {
        Assert.IsTrue(LinuxSystemProxyBackend.IsUnusableDbusAddress(null));
        Assert.IsTrue(LinuxSystemProxyBackend.IsUnusableDbusAddress(""));
        Assert.IsTrue(LinuxSystemProxyBackend.IsUnusableDbusAddress("disabled:"));
        Assert.IsTrue(LinuxSystemProxyBackend.IsUnusableDbusAddress("disabled"));
        Assert.IsFalse(LinuxSystemProxyBackend.IsUnusableDbusAddress(
            "unix:path=/tmp/dbus-test,guid=abc"));
    }

    [TestMethod]
    public void SetProxy_AppliesGnomeAndEnvironment()
    {
        var runner = new FakeProcessRunner { TrackGsettings = true };
        runner.When("sh", "command -v gsettings", "/usr/bin/gsettings\n");
        runner.When("gsettings", "list-schemas", "org.gnome.system.proxy\n");
        runner.SeedGsettings("org.gnome.system.proxy", "mode", "'none'");
        runner.SeedGsettings("org.gnome.system.proxy.http", "host", "''");
        runner.SeedGsettings("org.gnome.system.proxy.http", "port", "0");
        runner.SeedGsettings("org.gnome.system.proxy.http", "enabled", "false");
        runner.SeedGsettings("org.gnome.system.proxy.https", "host", "''");
        runner.SeedGsettings("org.gnome.system.proxy.https", "port", "0");
        runner.SeedGsettings("org.gnome.system.proxy", "ignore-hosts", "[]");
        runner.When("sh", "command -v kwriteconfig6", "\n");
        runner.When("sh", "command -v kwriteconfig5", "\n");
        runner.DefaultSuccess = true;

        Environment.SetEnvironmentVariable("http_proxy", null);
        Environment.SetEnvironmentVariable("https_proxy", null);
        Environment.SetEnvironmentVariable("HTTP_PROXY", null);
        Environment.SetEnvironmentVariable("HTTPS_PROXY", null);

        using var backend = new LinuxSystemProxyBackend(runner, applyBrowserLaunchHooks: false);
        backend.SetProxy("127.0.0.1", 8866, ProxyProtocolType.AllHttp, "localhost");

        Assert.IsTrue(runner.Commands.Exists(c =>
            c.Contains("gsettings", StringComparison.Ordinal) &&
            c.Contains("mode", StringComparison.Ordinal) &&
            c.Contains("manual", StringComparison.Ordinal)));
        Assert.AreEqual("http://127.0.0.1:8866", Environment.GetEnvironmentVariable("http_proxy"));
        Assert.AreEqual("http://127.0.0.1:8866", Environment.GetEnvironmentVariable("https_proxy"));
        Assert.AreEqual("'manual'", runner.GsettingsValue("org.gnome.system.proxy", "mode"));
        Assert.AreEqual("'127.0.0.1'", runner.GsettingsValue("org.gnome.system.proxy.http", "host"));
        Assert.AreEqual("8866", runner.GsettingsValue("org.gnome.system.proxy.http", "port"));
        Assert.AreEqual("true", runner.GsettingsValue("org.gnome.system.proxy.http", "enabled"));
        Assert.IsTrue(runner.Commands.Exists(c =>
            c.Contains("systemctl", StringComparison.Ordinal) &&
            c.Contains("set-environment", StringComparison.Ordinal)));

        backend.RestoreOriginalSettings();
        Assert.IsTrue(string.IsNullOrEmpty(Environment.GetEnvironmentVariable("http_proxy")));
        Assert.IsTrue(runner.Commands.Exists(c =>
            c.Contains("unset-environment", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void SetProxy_WhenGnomeWriteDoesNotStick_Throws()
    {
        var runner = new FakeProcessRunner();
        runner.When("sh", "command -v gsettings", "/usr/bin/gsettings\n");
        runner.When("gsettings", "list-schemas", "org.gnome.system.proxy\n");
        // Gets always return none/empty — simulates dconf commit failure with exit 0.
        runner.When("gsettings", "get org.gnome.system.proxy mode", "'none'\n");
        runner.When("gsettings", "get org.gnome.system.proxy.http host", "''\n");
        runner.When("gsettings", "get org.gnome.system.proxy.http port", "0\n");
        runner.When("gsettings", "get org.gnome.system.proxy.http enabled", "false\n");
        runner.When("gsettings", "get org.gnome.system.proxy.https host", "''\n");
        runner.When("gsettings", "get org.gnome.system.proxy.https port", "0\n");
        runner.When("gsettings", "get org.gnome.system.proxy ignore-hosts", "[]\n");
        runner.When("sh", "command -v kwriteconfig6", "\n");
        runner.When("sh", "command -v kwriteconfig5", "\n");
        runner.DefaultSuccess = true;

        using var backend = new LinuxSystemProxyBackend(runner, applyBrowserLaunchHooks: false);
        var ex = Assert.ThrowsException<InvalidOperationException>(() =>
            backend.SetProxy("127.0.0.1", 8866, ProxyProtocolType.AllHttp, "localhost"));
        StringAssert.Contains(ex.Message, "Failed to apply GNOME system proxy");
    }
}

[TestClass]
[SupportedOSPlatform("linux")]
public class LinuxBrowserLaunchProxyTests
{
    [TestMethod]
    public void InjectChromeProxyArgs_InsertsFlagsBeforePercentU()
    {
        var line = LinuxBrowserLaunchProxy.InjectChromeProxyArgs(
            "Exec=/usr/bin/google-chrome-stable %U", "127.0.0.1", 8866);
        StringAssert.Contains(line, "--proxy-server=http://127.0.0.1:8866");
        StringAssert.Contains(line, "--proxy-bypass-list=<-loopback>");
        StringAssert.Contains(line, "--disable-quic");
        Assert.IsTrue(line.EndsWith(" %U", StringComparison.Ordinal), line);
    }

    [TestMethod]
    public void InjectChromeProxyArgs_WorksForChromiumAndBraveExecLines()
    {
        var chromium = LinuxBrowserLaunchProxy.InjectChromeProxyArgs(
            "Exec=/usr/bin/chromium %U", "127.0.0.1", 8866);
        StringAssert.Contains(chromium, "--proxy-server=http://127.0.0.1:8866");
        StringAssert.Contains(chromium, "--disable-quic");
        Assert.IsTrue(chromium.EndsWith(" %U", StringComparison.Ordinal), chromium);

        var brave = LinuxBrowserLaunchProxy.InjectChromeProxyArgs(
            "Exec=/usr/bin/brave-browser %u", "10.0.0.1", 8888);
        StringAssert.Contains(brave, "--proxy-server=http://10.0.0.1:8888");
    }

    [TestMethod]
    public void PolicyDirectories_IncludeSnapAndFlatpakChromeRoots()
    {
        var dirs = LinuxBrowserLaunchProxy.PolicyDirectories().ToList();
        Assert.IsTrue(dirs.Any(d => d.Contains("snap", StringComparison.Ordinal) && d.Contains("chromium", StringComparison.Ordinal)));
        Assert.IsTrue(dirs.Any(d => d.Contains(".var/app/com.google.Chrome", StringComparison.Ordinal) ||
                                    d.Contains($".var{Path.DirectorySeparatorChar}app{Path.DirectorySeparatorChar}com.google.Chrome", StringComparison.Ordinal)));
        Assert.IsTrue(dirs.Any(d => d.Contains("BraveSoftware", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void InjectChromeProxyArgs_IsIdempotent()
    {
        var once = LinuxBrowserLaunchProxy.InjectChromeProxyArgs(
            "Exec=/usr/bin/google-chrome-stable %U", "127.0.0.1", 8866);
        var twice = LinuxBrowserLaunchProxy.InjectChromeProxyArgs(once, "127.0.0.1", 8866);
        Assert.AreEqual(once, twice);
    }

    [TestMethod]
    public void WritePolicies_WritesManagedJson()
    {
        var dir = Path.Combine(Path.GetTempPath(), "twp-chrome-policy-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            Assert.AreEqual(1, LinuxBrowserLaunchProxy.WritePolicies("127.0.0.1", 8866, [dir]));
            var json = File.ReadAllText(Path.Combine(dir, LinuxBrowserLaunchProxy.PolicyFileName));
            Assert.IsTrue(LinuxBrowserLaunchProxy.TryValidatePolicyJson(json, "127.0.0.1", 8866, out var err), err);
            StringAssert.Contains(json, "fixed_servers");
            StringAssert.Contains(json, "http://127.0.0.1:8866");
            StringAssert.Contains(json, "<-loopback>");
            StringAssert.Contains(json, "\"QuicAllowed\": false");
            // Legacy int enum for older Chromium builds that still read ProxyServerMode.
            StringAssert.Contains(json, "\"ProxyServerMode\": 2");
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* ignore */ }
        }
    }

    [TestMethod]
    public void BuildPolicyJson_EscapesSpecialHostCharacters()
    {
        var json = LinuxBrowserLaunchProxy.BuildPolicyJson("weird\"host", 8866);
        Assert.IsTrue(LinuxBrowserLaunchProxy.TryValidatePolicyJson(json, "weird\"host", 8866, out var err), err);
        // Must be valid JSON even with quotes in the host fragment.
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        Assert.AreEqual("fixed_servers", doc.RootElement.GetProperty("ProxyMode").GetString());
    }

    [TestMethod]
    public void TryValidatePolicyJson_AcceptsLegacyProxyServerModeOnly()
    {
        const string legacy =
            """
            {
              "ProxyServerMode": 2,
              "ProxyServer": "http://10.0.0.1:8888"
            }
            """;
        Assert.IsTrue(LinuxBrowserLaunchProxy.TryValidatePolicyJson(legacy, "10.0.0.1", 8888, out var err), err);
    }

    [TestMethod]
    public void TryValidatePolicyJson_RejectsCorruptOrWrongEndpoint()
    {
        Assert.IsFalse(LinuxBrowserLaunchProxy.TryValidatePolicyJson("{", "127.0.0.1", 8866, out _));
        Assert.IsFalse(LinuxBrowserLaunchProxy.TryValidatePolicyJson(
            """{"ProxyMode":"fixed_servers","ProxyServer":"http://127.0.0.1:1"}""",
            "127.0.0.1", 8866, out _));
    }
}

[TestClass]
public class ElevationPromptCancelTests
{
    [TestMethod]
    public void FakeElevation_CancelReturnsNull()
    {
        var elevation = new FakeElevationPrompt { Cancel = true };
        Assert.IsNull(elevation.RunElevated("tool", "args"));
        Assert.AreEqual(1, elevation.Calls.Count);
    }

    [TestMethod]
    public void FakeElevation_SuccessReturnsZeroExit()
    {
        var elevation = new FakeElevationPrompt();
        var result = elevation.RunElevated("tool", "args");
        Assert.IsNotNull(result);
        Assert.IsTrue(result!.Succeeded);
    }
}

internal sealed class FakeProcessRunner : IProcessRunner
{
    private readonly List<(string Match, string Output)> _responses = new();
    private readonly Dictionary<string, string> _gsettings = new(StringComparer.Ordinal);

    public List<string> Commands { get; } = new();
    public bool DefaultSuccess { get; set; } = true;
    public string? FailMatching { get; set; }
    public string FailError { get; set; } = "error";
    /// <summary>When set, matching commands that contain a quoted path create that empty file.</summary>
    public string? WriteFileOnMatch { get; set; }
    /// <summary>When true, gsettings set/get are tracked in-memory for apply verification.</summary>
    public bool TrackGsettings { get; set; }

    public void When(string fileName, string argsContains, string stdout) =>
        _responses.Add((fileName + " " + argsContains, stdout));

    public void SeedGsettings(string schema, string key, string value) =>
        _gsettings[$"{schema} {key}"] = value;

    public string? GsettingsValue(string schema, string key) =>
        _gsettings.TryGetValue($"{schema} {key}", out var value) ? value : null;

    public ProcessRunResult? Run(string fileName, string arguments,
        IDictionary<string, string?>? environment = null, string? workingDirectory = null)
    {
        var cmd = fileName + " " + arguments;
        Commands.Add(cmd);

        if (FailMatching != null && cmd.Contains(FailMatching, StringComparison.Ordinal))
            return new ProcessRunResult(1, string.Empty, FailError);

        if (TrackGsettings && fileName == "gsettings")
        {
            var tracked = TryTrackGsettings(arguments);
            if (tracked is not null)
                return tracked;
        }

        foreach (var (match, output) in _responses)
        {
            var space = match.IndexOf(' ');
            var file = space < 0 ? match : match[..space];
            var args = space < 0 ? string.Empty : match[(space + 1)..];
            if (cmd.Contains(file, StringComparison.Ordinal) &&
                (args.Length == 0 || cmd.Contains(args, StringComparison.Ordinal)))
            {
                if (WriteFileOnMatch != null &&
                    cmd.Contains(WriteFileOnMatch, StringComparison.Ordinal))
                {
                    TryTouchQuotedPath(arguments);
                }

                return new ProcessRunResult(0, output, string.Empty);
            }
        }

        return DefaultSuccess
            ? new ProcessRunResult(0, string.Empty, string.Empty)
            : new ProcessRunResult(1, string.Empty, "fail");
    }

    private ProcessRunResult? TryTrackGsettings(string arguments)
    {
        var parts = arguments.Split(' ', 4, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length >= 4 && parts[0] == "set")
        {
            _gsettings[$"{parts[1]} {parts[2]}"] = parts[3];
            return new ProcessRunResult(0, string.Empty, string.Empty);
        }

        if (parts.Length >= 3 && parts[0] == "get")
        {
            var key = $"{parts[1]} {parts[2]}";
            if (_gsettings.TryGetValue(key, out var value))
                return new ProcessRunResult(0, value + "\n", string.Empty);
        }

        return null;
    }

    private static void TryTouchQuotedPath(string arguments)
    {
        var start = arguments.IndexOf('"');
        var end = arguments.LastIndexOf('"');
        if (start < 0 || end <= start)
            return;
        var path = arguments[(start + 1)..end];
        try
        {
            File.WriteAllText(path, string.Empty);
        }
        catch
        {
            // ignore
        }
    }
}

internal sealed class FakeElevationPrompt : IElevationPrompt
{
    public bool Cancel { get; set; }
    public List<(string FileName, string Arguments)> Calls { get; } = new();

    public ProcessRunResult? RunElevated(string fileName, string arguments)
    {
        Calls.Add((fileName, arguments));
        return Cancel ? null : new ProcessRunResult(0, string.Empty, string.Empty);
    }
}

[TestClass]
public class SystemProxyBackendFactoryPlatformTests
{
    [TestMethod]
    [TestCategory("E2E-UI-Mac")]
    public void Create_OnMacOs_ReturnsMacBackend()
    {
        if (!OperatingSystem.IsMacOS())
        {
            Assert.Inconclusive("macOS-only");
            return;
        }

        using var backend = SystemProxyBackendFactory.Create();
        Assert.IsNotNull(backend);
        Assert.IsInstanceOfType<MacOsSystemProxyBackend>(backend);
    }

    [TestMethod]
    [TestCategory("E2E-UI-Linux")]
    public void Create_OnLinux_ReturnsLinuxBackend()
    {
        if (!OperatingSystem.IsLinux())
        {
            Assert.Inconclusive("Linux-only");
            return;
        }

        using var backend = SystemProxyBackendFactory.Create();
        Assert.IsNotNull(backend);
        Assert.IsInstanceOfType<LinuxSystemProxyBackend>(backend);
    }

    [TestMethod]
    [TestCategory("E2E-UI")]
    public void Create_OnCurrentOs_ReturnsBackend()
    {
        using var backend = SystemProxyBackendFactory.Create();
        Assert.IsNotNull(backend);
    }
}