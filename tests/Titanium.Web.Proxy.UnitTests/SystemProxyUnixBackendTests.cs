using System;
using System.Collections.Generic;
using System.Runtime.Versioning;
using Microsoft.VisualStudio.TestTools.UnitTesting;
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
    public void ToNoProxyEnv_IncludesLocalhost()
    {
        var env = UnixProxyBypassMapper.ToNoProxyEnv("*.corp");
        StringAssert.Contains(env, "localhost");
        StringAssert.Contains(env, "127.0.0.1");
        StringAssert.Contains(env, "*.corp");
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
[SupportedOSPlatform("linux")]
public class LinuxSystemProxyBackendTests
{
    [TestMethod]
    public void SetProxy_AppliesGnomeAndEnvironment()
    {
        var runner = new FakeProcessRunner();
        runner.When("sh", "command -v gsettings", "/usr/bin/gsettings\n");
        runner.When("gsettings", "list-schemas", "org.gnome.system.proxy\n");
        runner.When("gsettings", "get org.gnome.system.proxy mode", "'none'\n");
        runner.When("gsettings", "get org.gnome.system.proxy.http host", "''\n");
        runner.When("gsettings", "get org.gnome.system.proxy.http port", "0\n");
        runner.When("gsettings", "get org.gnome.system.proxy.https host", "''\n");
        runner.When("gsettings", "get org.gnome.system.proxy.https port", "0\n");
        runner.When("gsettings", "get org.gnome.system.proxy ignore-hosts", "[]\n");
        runner.When("sh", "command -v kwriteconfig6", "\n");
        runner.When("sh", "command -v kwriteconfig5", "\n");
        runner.DefaultSuccess = true;

        Environment.SetEnvironmentVariable("http_proxy", null);
        Environment.SetEnvironmentVariable("https_proxy", null);
        Environment.SetEnvironmentVariable("HTTP_PROXY", null);
        Environment.SetEnvironmentVariable("HTTPS_PROXY", null);

        using var backend = new LinuxSystemProxyBackend(runner);
        backend.SetProxy("127.0.0.1", 8866, ProxyProtocolType.AllHttp, "localhost");

        Assert.IsTrue(runner.Commands.Exists(c =>
            c.Contains("gsettings", StringComparison.Ordinal) &&
            c.Contains("mode", StringComparison.Ordinal) &&
            c.Contains("manual", StringComparison.Ordinal)));
        Assert.AreEqual("http://127.0.0.1:8866", Environment.GetEnvironmentVariable("http_proxy"));
        Assert.AreEqual("http://127.0.0.1:8866", Environment.GetEnvironmentVariable("https_proxy"));

        backend.RestoreOriginalSettings();
        Assert.IsTrue(string.IsNullOrEmpty(Environment.GetEnvironmentVariable("http_proxy")));
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

    public List<string> Commands { get; } = new();
    public bool DefaultSuccess { get; set; } = true;
    public string? FailMatching { get; set; }
    public string FailError { get; set; } = "error";

    public void When(string fileName, string argsContains, string stdout) =>
        _responses.Add((fileName + " " + argsContains, stdout));

    public ProcessRunResult? Run(string fileName, string arguments,
        IDictionary<string, string?>? environment = null, string? workingDirectory = null)
    {
        var cmd = fileName + " " + arguments;
        Commands.Add(cmd);

        if (FailMatching != null && cmd.Contains(FailMatching, StringComparison.Ordinal))
            return new ProcessRunResult(1, string.Empty, FailError);

        foreach (var (match, output) in _responses)
        {
            var space = match.IndexOf(' ');
            var file = space < 0 ? match : match[..space];
            var args = space < 0 ? string.Empty : match[(space + 1)..];
            if (cmd.Contains(file, StringComparison.Ordinal) &&
                (args.Length == 0 || cmd.Contains(args, StringComparison.Ordinal)))
                return new ProcessRunResult(0, output, string.Empty);
        }

        return DefaultSuccess
            ? new ProcessRunResult(0, string.Empty, string.Empty)
            : new ProcessRunResult(1, string.Empty, "fail");
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
        Assert.IsInstanceOfType(backend, typeof(MacOsSystemProxyBackend));
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
        Assert.IsInstanceOfType(backend, typeof(LinuxSystemProxyBackend));
    }

    [TestMethod]
    [TestCategory("E2E-UI")]
    public void Create_OnCurrentOs_ReturnsBackend()
    {
        using var backend = SystemProxyBackendFactory.Create();
        Assert.IsNotNull(backend);
    }
}