#pragma warning disable TWP001
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Cli.Certificates;
using Titanium.Cli.Config;
using Titanium.Web.Proxy;
using Titanium.Web.Proxy.Configuration.Models;
using Titanium.Web.Proxy.Models;

namespace Titanium.Cli.Tests;

/// <summary>
///     <see cref="RunCommand.ExecuteCoreAsync" /> exits and listener wiring that only show up when something is wrong
///     or unusual: busy ports, a background ACME failure, and per-listener passthrough / TLS-terminate / Alt-Svc knobs.
///     Each test asserts what the operator or the endpoint model observes, not that a line ran.
/// </summary>
[TestClass]
[DoNotParallelize]
public class RunCommandFailurePathBehaviorTests
{
    private static readonly BindingFlags Priv = BindingFlags.NonPublic | BindingFlags.Static;

    // Bug class: a second `titanium run` on a busy port hangs, throws an unhandled exception, or exits 0 without a
    // message that points the operator at the config key to change.
    [TestMethod]
    [Timeout(60_000)]
    public async Task ListenerPortAlreadyInUse_ExitsOneAndNamesTheListenerKey()
    {
        using var occupier = new TcpListener(IPAddress.Loopback, 0);
        occupier.Start();
        var port = ((IPEndPoint)occupier.LocalEndpoint).Port;
        using var dir = new TempDir();
        var path = dir.Write("twp.yaml", $"""
            schemaVersion: "7.1"
            listeners:
              - host: "127.0.0.1"
                port: {port}
                decryptSsl: false
            server:
              drainTimeoutSeconds: 0
            """);

        var (code, _, stderr) = await RunToCompletionAsync(path);

        Assert.AreEqual(1, code, stderr);
        StringAssert.Contains(stderr, "listeners[0].port");
    }

    // Bug class: a metrics bind failure is swallowed (proxy runs with no /metrics) or surfaces as a raw SocketException
    // stack instead of telling the operator which setting to change.
    [TestMethod]
    [Timeout(60_000)]
    public async Task MetricsPortAlreadyInUse_ExitsOneWithAnActionableMessage()
    {
        using var occupier = new TcpListener(IPAddress.Loopback, 0);
        occupier.Start();
        var metricsPort = ((IPEndPoint)occupier.LocalEndpoint).Port;
        using var dir = new TempDir();
        var path = dir.Write("twp.yaml", $"""
            schemaVersion: "7.1"
            listeners:
              - host: "127.0.0.1"
                port: {FreePort()}
                decryptSsl: false
            server:
              drainTimeoutSeconds: 0
              metrics:
                enabled: true
                host: "127.0.0.1"
                port: {metricsPort}
            """);

        var (code, _, stderr) = await RunToCompletionAsync(path);

        Assert.AreEqual(1, code, stderr);
        StringAssert.Contains(stderr, $"Failed to bind metrics endpoint 127.0.0.1:{metricsPort}");
        StringAssert.Contains(stderr, "server.metrics.port");
    }

    // Bug class: a failed background ACME issue either crashes the running proxy (unobserved task exception) or is
    // silently dropped, leaving the operator with no clue why the certificate never arrived.
    [TestMethod]
    [Timeout(60_000)]
    public async Task AcmeIssueFailure_IsReportedAndTheProxyKeepsServing()
    {
        var proxyPort = FreePort();
        using var dir = new TempDir();
        var path = dir.Write("twp.yaml", $"""
            schemaVersion: "7.1"
            listeners:
              - host: "127.0.0.1"
                port: {proxyPort}
                decryptSsl: false
            server:
              drainTimeoutSeconds: 0
            certificates:
              acmeEmail: "ops@example.test"
              acmeDomain: "acme-failure.example.test"
              acmeDirectory: "http://127.0.0.1:1/directory"
            """);

        var previousOverride = CertificateBootstrap.IssueOverride;
        CertificateBootstrap.IssueOverride = static (_, _, _) =>
            throw new InvalidOperationException("directory unreachable (test)");
        var stdoutBuffer = new StringWriter();
        var stderrBuffer = new StringWriter();
        var stdout = TextWriter.Synchronized(stdoutBuffer);
        var stderr = TextWriter.Synchronized(stderrBuffer);
        var previousOut = Console.Out;
        var previousErr = Console.Error;
        var previousCwd = Directory.GetCurrentDirectory();
        using var cts = new CancellationTokenSource();
        try
        {
            Console.SetOut(stdout);
            Console.SetError(stderr);
            var run = RunCommand.ExecuteCoreAsync(path, verbose: false, serviceMode: false, cts.Token);

            await WaitForAsync(
                async () =>
                {
                    await AsyncConsole.FlushAsync();
                    return stderrBuffer.ToString().Contains("ACME IssueOrRenew failed: directory unreachable (test)", StringComparison.Ordinal);
                },
                TimeSpan.FromSeconds(30),
                () => stdoutBuffer + Environment.NewLine + stderrBuffer);
            Assert.IsTrue(await CanConnectAsync(proxyPort), "an ACME failure must not take the listener down");

            await cts.CancelAsync();
            Assert.AreEqual(0, await run.WaitAsync(TimeSpan.FromSeconds(30)));
        }
        finally
        {
            CertificateBootstrap.IssueOverride = previousOverride;
            if (!cts.IsCancellationRequested)
            {
                await cts.CancelAsync();
            }

            Console.SetOut(previousOut);
            Console.SetError(previousErr);
            Directory.SetCurrentDirectory(previousCwd);
        }
    }

    // Bug class: a listener-level enableHttp3:false is ignored when the server section does not mention HTTP/3, so the
    // proxy advertises HTTP/3 on a deployment that explicitly opted a listener out.
    [TestMethod]
    public void ConfigureProxyFlags_ListenerOptOutWithoutServerSetting_DisablesHttp3()
    {
        using var proxy = new ProxyServer(userTrustRootCertificate: false);
        proxy.EnableHttp3 = true;

        RunCommand.ConfigureProxyFlags(
            proxy,
            new TwpConfig { Listeners = [new ListenerConfig { Host = "127.0.0.1", Port = 8080, EnableHttp3 = false }] },
            requiresSessionPath: false);

        Assert.IsFalse(proxy.EnableHttp3);
    }

    // Bug class: passthrough SNI routing keys, timeouts, or the PROXY v2 switch are dropped on the way from YAML to the
    // endpoint, so a passthrough listener silently falls back to defaults (wrong cluster, wrong timeouts, no header).
    [TestMethod]
    public void AddListener_PassthroughSni_CopiesRoutesTimeoutsAndProxyProtocolOntoTheEndpoint()
    {
        using var proxy = new ProxyServer(userTrustRootCertificate: false);

        var endpoint = (TransparentProxyEndPoint)Invoke("AddListener", proxy, new ListenerConfig
        {
            Host = "127.0.0.1",
            Port = FreePort(),
            Type = "transparent",
            DecryptSsl = false,
            Passthrough = true,
            SniRoutes =
            [
                new SniRouteConfig { ServerName = "*.example.test", ClusterId = "wild" },
                new SniRouteConfig { ServerName = null, ClusterId = null },
            ],
            SniDefaultClusterId = "fallback",
            SniReadTimeoutSeconds = 7,
            IdleTimeoutSeconds = 45,
            SendProxyProtocol = "V2",
        })!;

        Assert.IsTrue(endpoint.Passthrough);
        Assert.AreEqual(2, endpoint.PassthroughRoutes!.Count);
        Assert.AreEqual("*.example.test", endpoint.PassthroughRoutes[0].ServerName);
        Assert.AreEqual("wild", endpoint.PassthroughRoutes[0].ClusterId);
        Assert.AreEqual(string.Empty, endpoint.PassthroughRoutes[1].ServerName, "a missing serverName becomes empty, not null");
        Assert.AreEqual("fallback", endpoint.PassthroughDefaultClusterId);
        Assert.AreEqual(TimeSpan.FromSeconds(7), endpoint.PassthroughSniReadTimeout);
        Assert.AreEqual(TimeSpan.FromSeconds(45), endpoint.PassthroughIdleTimeout);
        Assert.AreEqual(PassthroughProxyProtocolMode.V2, endpoint.PassthroughProxyProtocol);
    }

    // Bug class: SNI routes configured on a decrypting (non-passthrough) listener leak into passthrough routing and
    // send decrypted traffic to the wrong cluster.
    [TestMethod]
    public void AddListener_SniRoutesOnANonPassthroughListener_AreIgnored()
    {
        using var proxy = new ProxyServer(userTrustRootCertificate: false);

        var endpoint = (TransparentProxyEndPoint)Invoke("AddListener", proxy, new ListenerConfig
        {
            Host = "127.0.0.1",
            Port = FreePort(),
            Type = "transparent",
            DecryptSsl = true,
            Passthrough = false,
            SniRoutes = [new SniRouteConfig { ServerName = "a.example.test", ClusterId = "c" }],
            SniReadTimeoutSeconds = 9,
        })!;

        Assert.IsFalse(endpoint.Passthrough);
        Assert.IsNull(endpoint.PassthroughRoutes);
        Assert.AreEqual(TimeSpan.FromSeconds(5), endpoint.PassthroughSniReadTimeout, "the default read timeout must stay");
    }

    // Bug class: terminate-TLS knobs (handshake timeout, concurrency cap, ALPN, client-cert mode) are ignored, so a
    // listener that asked for mTLS "require" accepts anonymous clients or the defaults are applied wrongly.
    [TestMethod]
    public void AddListener_TerminateTls_AppliesHandshakeLimitsAlpnAndClientCertMode()
    {
        using var proxy = new ProxyServer(userTrustRootCertificate: false);

        var tuned = (TransparentProxyEndPoint)Invoke("AddListener", proxy, new ListenerConfig
        {
            Host = "127.0.0.1",
            Port = FreePort(),
            Type = "transparent",
            DecryptSsl = false,
            Passthrough = true,
            TerminateTls = true,
            TlsHandshakeTimeoutSeconds = 3,
            MaxConcurrentHandshakes = 17,
            Alpn = ["h2", "http/1.1"],
            ClientCertificateMode = "Require",
            AcceptProxyProtocol = true,
            TrustedProxyProtocolSources = ["10.0.0.0/8"],
        })!;
        Assert.IsTrue(tuned.TerminateTls);
        Assert.AreEqual(TimeSpan.FromSeconds(3), tuned.TlsHandshakeTimeout);
        Assert.AreEqual(17, tuned.MaxConcurrentTlsHandshakes);
        CollectionAssert.AreEqual(new[] { "h2", "http/1.1" }, tuned.TerminateTlsAlpn!.ToArray());
        Assert.AreEqual(ClientCertificateMode.Require, tuned.ClientCertificateMode);
        Assert.IsTrue(tuned.AcceptProxyProtocol);
        CollectionAssert.AreEqual(new[] { "10.0.0.0/8" }, tuned.TrustedProxyProtocolSources.ToArray());

        var defaults = (TransparentProxyEndPoint)Invoke("AddListener", proxy, new ListenerConfig
        {
            Host = "127.0.0.1",
            Port = FreePort(),
            Type = "transparent",
            DecryptSsl = false,
            Passthrough = true,
            TerminateTls = true,
            ClientCertificateMode = "ALLOW",
        })!;
        Assert.AreEqual(TimeSpan.FromSeconds(10), defaults.TlsHandshakeTimeout, "unset handshake timeout defaults to 10s");
        Assert.AreEqual(256, defaults.MaxConcurrentTlsHandshakes);
        Assert.IsNull(defaults.TerminateTlsAlpn, "no alpn list means the stack default, not an empty list");
        Assert.AreEqual(ClientCertificateMode.Allow, defaults.ClientCertificateMode, "mode is matched case-insensitively");

        var unknownMode = (TransparentProxyEndPoint)Invoke("AddListener", proxy, new ListenerConfig
        {
            Host = "127.0.0.1",
            Port = FreePort(),
            Type = "transparent",
            DecryptSsl = false,
            ClientCertificateMode = "bogus",
        })!;
        Assert.AreEqual(ClientCertificateMode.None, unknownMode.ClientCertificateMode, "an unknown mode must not demand certs");
    }

    // Bug class: terminate-TLS settings applied when the listener did not ask for TLS termination (the flag is the gate).
    [TestMethod]
    public void AddListener_TerminateTlsOff_LeavesTlsLimitsAtDefaults()
    {
        using var proxy = new ProxyServer(userTrustRootCertificate: false);

        var endpoint = (TransparentProxyEndPoint)Invoke("AddListener", proxy, new ListenerConfig
        {
            Host = "127.0.0.1",
            Port = FreePort(),
            Type = "transparent",
            DecryptSsl = false,
            Passthrough = true,
            TerminateTls = false,
            TlsHandshakeTimeoutSeconds = 3,
            MaxConcurrentHandshakes = 17,
        })!;

        Assert.IsFalse(endpoint.TerminateTls);
        Assert.AreEqual(256, endpoint.MaxConcurrentTlsHandshakes);
        Assert.AreEqual(TimeSpan.FromSeconds(10), endpoint.TlsHandshakeTimeout);
    }

    // Bug class: the Alt-Svc block is lost (always advertises) or an unset "enabled" turns advertisement off.
    [TestMethod]
    public void AddListener_AltSvc_MapsEnabledMaxAgeAndPort()
    {
        using var proxy = new ProxyServer(userTrustRootCertificate: false);

        var off = (TransparentProxyEndPoint)Invoke("AddListener", proxy, new ListenerConfig
        {
            Host = "127.0.0.1",
            Port = FreePort(),
            Type = "transparent",
            DecryptSsl = true,
            AltSvc = new AltSvcConfig { Enabled = false, MaxAgeSeconds = 60, Port = 8443 },
        })!;
        Assert.IsNotNull(off.AltSvc);
        Assert.IsFalse(off.AltSvc!.Enabled);
        Assert.AreEqual(60, off.AltSvc.MaxAgeSeconds);
        Assert.AreEqual(8443, off.AltSvc.Port);

        var inherited = (TransparentProxyEndPoint)Invoke("AddListener", proxy, new ListenerConfig
        {
            Host = "127.0.0.1",
            Port = FreePort(),
            Type = "transparent",
            DecryptSsl = true,
            AltSvc = new AltSvcConfig(),
        })!;
        Assert.IsTrue(inherited.AltSvc!.Enabled, "a null enabled inherits enabled, it does not disable Alt-Svc");

        var none = (TransparentProxyEndPoint)Invoke("AddListener", proxy, new ListenerConfig
        {
            Host = "127.0.0.1",
            Port = FreePort(),
            Type = "transparent",
            DecryptSsl = true,
        })!;
        Assert.IsNull(none.AltSvc);
    }

    // Bug class: allowHttp11Upgrade and allowedPorts on a MASQUE listener are not forwarded, so the endpoint keeps the
    // wider default policy than the operator configured.
    [TestMethod]
    public void CreateMasqueEndPoint_ForwardsHttp11UpgradeAndAllowedPorts()
    {
        var masque = (MasqueProxyEndPoint)Invoke("CreateMasqueEndPoint", IPAddress.Loopback, new ListenerConfig
        {
            Port = 0,
            Masque = new MasqueListenerConfig
            {
                AllowHttp11Upgrade = true,
                AllowedPorts = [443, 8443],
            },
        })!;

        Assert.IsTrue(masque.Options.AllowHttp11Upgrade);
        CollectionAssert.AreEqual(new[] { 443, 8443 }, masque.Options.AllowedPorts!.ToArray());
    }

    private static object? Invoke(string name, params object?[] args)
    {
        var method = typeof(RunCommand).GetMethods(Priv).First(m =>
            m.Name == name && m.GetParameters().Length >= args.Length
            && m.GetParameters().Skip(args.Length).All(static p => p.IsOptional)
            && m.GetParameters().Take(args.Length).Zip(args, static (p, a) => a is null || p.ParameterType.IsInstanceOfType(a)).All(static ok => ok));
        var parameters = method.GetParameters();
        var full = args.Length < parameters.Length
            ? [.. args, .. parameters.Skip(args.Length).Select(static p => p.DefaultValue)]
            : args;
        return method.Invoke(null, full);
    }

    private static async Task<(int Code, string Stdout, string Stderr)> RunToCompletionAsync(string configPath)
    {
        var stdoutBuffer = new StringWriter();
        var stderrBuffer = new StringWriter();
        var stdout = TextWriter.Synchronized(stdoutBuffer);
        var stderr = TextWriter.Synchronized(stderrBuffer);
        var previousOut = Console.Out;
        var previousErr = Console.Error;
        var previousCwd = Directory.GetCurrentDirectory();
        try
        {
            Console.SetOut(stdout);
            Console.SetError(stderr);
            var code = await RunCommand.ExecuteCoreAsync(
                configPath, verbose: false, serviceMode: false, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(45));
            await AsyncConsole.FlushAsync();
            return (code, stdoutBuffer.ToString(), stderrBuffer.ToString());
        }
        finally
        {
            Console.SetOut(previousOut);
            Console.SetError(previousErr);
            Directory.SetCurrentDirectory(previousCwd);
        }
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static async Task<bool> CanConnectAsync(int port)
    {
        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, port).WaitAsync(TimeSpan.FromSeconds(3));
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static async Task WaitForAsync(Func<Task<bool>> ready, TimeSpan timeout, Func<string> detail)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (await ready())
            {
                return;
            }

            await Task.Delay(50);
        }

        Assert.Fail(detail());
    }

    private sealed class TempDir : IDisposable
    {
        public TempDir()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "twp-run-fail-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public string Write(string name, string content)
        {
            var full = System.IO.Path.Combine(Path, name);
            File.WriteAllText(full, content);
            return full;
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch
            {
                // The run may still hold a handle briefly.
            }
        }
    }
}
