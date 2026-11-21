using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Inspector.Services;

namespace Titanium.Inspector.Tests;

[TestClass]
public class DecryptHttpsProbeTests
{
    [TestMethod]
    public async Task VerifyAsync_WhenProxyNotRunning_FailsFast()
    {
        using var interception = new InterceptionService();
        var result = await DecryptHttpsProbe.VerifyAsync(
            interception,
            TimeSpan.FromSeconds(2));
        Assert.IsFalse(result.Ok);
        Assert.IsFalse(result.Inconclusive);
        StringAssert.Contains(result.Message, "not listening");
    }

    [TestMethod]
    public async Task VerifyAsync_WhenDecryptOff_FailsFast()
    {
        using var interception = new InterceptionService();
        // BoundPort stays 0 until Start; still reports not listening first.
        interception.DecryptHttps = false;
        var result = await DecryptHttpsProbe.VerifyAsync(
            interception,
            TimeSpan.FromSeconds(2));
        Assert.IsFalse(result.Ok);
        Assert.IsFalse(result.Inconclusive);
    }

    [TestMethod]
    public void LeafChainsToMitmRoot_NullLeaf_IsFalse()
    {
        using var root = CreateSelfSigned("CN=Titanium Probe Root");
        Assert.IsFalse(DecryptHttpsProbe.LeafChainsToMitmRoot(null, root));
    }

    [TestMethod]
    public void LeafChainsToMitmRoot_RootAsLeaf_IsTrue()
    {
        using var root = CreateSelfSigned("CN=Titanium Probe Root");
        Assert.IsTrue(DecryptHttpsProbe.LeafChainsToMitmRoot(root, root));
    }

    [TestMethod]
    public void LeafChainsToMitmRoot_UnrelatedLeaf_IsFalse()
    {
        using var root = CreateSelfSigned("CN=Titanium Probe Root");
        using var other = CreateSelfSigned("CN=Other Root");
        Assert.IsFalse(DecryptHttpsProbe.LeafChainsToMitmRoot(other, root));
    }

    [TestMethod]
    public void LeafChainsToMitmRoot_ExpiredLeaf_IsFalse()
    {
        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest("CN=Expired Leaf", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var expired = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-30), DateTimeOffset.UtcNow.AddDays(-1));
        using var root = CreateSelfSigned("CN=Titanium Probe Root");
        Assert.IsFalse(DecryptHttpsProbe.LeafChainsToMitmRoot(expired, root));
    }

    [TestMethod]
    public void IsTlsAuthFailure_DetectsWrappedAuthenticationException()
    {
        var inner = new AuthenticationException("leaf not trusted");
        var wrapped = new HttpRequestException("SSL failed", inner);
        Assert.IsTrue(DecryptHttpsProbe.IsTlsAuthFailure(wrapped));
        Assert.IsFalse(DecryptHttpsProbe.IsTlsAuthFailure(new HttpRequestException("connection refused")));
        Assert.IsFalse(DecryptHttpsProbe.IsTlsAuthFailure(null));
    }

    [TestMethod]
    public void Result_Inconclusive_KeepsOkFalse()
    {
        var inconclusive = new DecryptHttpsProbe.Result(false, "Could not verify; example.com unreachable", Inconclusive: true);
        Assert.IsFalse(inconclusive.Ok);
        Assert.IsTrue(inconclusive.Inconclusive);
    }

    [TestMethod]
    public void OrphanClear_TreatsAListeningPortAsStillInUse()
    {
        var direct = new ProxyServerSystemProxyController().ClearOrphanedLocalSystemProxy((_, _) => true);
        var alias = new WinInetSystemProxyController().ClearOrphanedLocalSystemProxy((_, _) => true);
        Assert.IsTrue(direct.Succeeded);
        Assert.IsTrue(alias.Succeeded);
    }

    [TestMethod]
    public async Task VerifyAsync_BlackholeUpstream_TimesOut()
    {
        var held = new List<TcpClient>();
        var hold = new TcpListener(IPAddress.Loopback, 0);
        hold.Start();
        var upstreamPort = ((IPEndPoint)hold.LocalEndpoint!).Port;
        _ = Task.Run(async () =>
        {
            try
            {
                while (true)
                {
                    var client = await hold.AcceptTcpClientAsync();
                    lock (held)
                        held.Add(client);
                }
            }
            catch
            {
                // listener stopped
            }
        });

        object? enable = null;
        object? server = null;
        Microsoft.Win32.RegistryKey? reg = null;
        if (OperatingSystem.IsWindows())
        {
            reg = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Internet Settings", writable: true);
            if (reg is not null)
            {
                enable = reg.GetValue("ProxyEnable");
                server = reg.GetValue("ProxyServer");
            }
        }

        try
        {
            using var interception = new InterceptionService(new RecordingSystemProxyController())
            {
                UseInMemoryTrustState = true,
            };
            await interception.StartAsync(IPAddress.Loopback, 0);
            var proxy = (Titanium.Web.Proxy.ProxyServer)typeof(InterceptionService)
                .GetField("_proxy", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .GetValue(interception)!;
            proxy.UpStreamHttpsProxy = new Titanium.Web.Proxy.Models.ExternalProxy("127.0.0.1", upstreamPort);
            interception.DecryptHttps = true;
            // A captured CONNECT would count as success and hide the timeout.
            interception.Capturing = false;
            var timedOut = await DecryptHttpsProbe.VerifyAsync(interception, TimeSpan.FromMilliseconds(400));
            Assert.IsFalse(timedOut.Ok);
            StringAssert.Contains(timedOut.Message, "timed out");
            interception.Stop();
        }
        finally
        {
            try { hold.Stop(); } catch { /* already stopped */ }
            List<TcpClient> clients;
            lock (held)
                clients = [.. held];
            foreach (var client in clients)
                client.Dispose();
            if (OperatingSystem.IsWindows() && reg is not null)
            {
                if (enable is null) reg.DeleteValue("ProxyEnable", throwOnMissingValue: false);
                else reg.SetValue("ProxyEnable", enable);
                if (server is null) reg.DeleteValue("ProxyServer", throwOnMissingValue: false);
                else reg.SetValue("ProxyServer", server);
                reg.Dispose();
            }
        }
    }

    [TestMethod]
    public async Task VerifyAsync_ListeningProxy_CoversDecryptOffAndAShortProbe()
    {
        object? enable = null;
        object? server = null;
        Microsoft.Win32.RegistryKey? reg = null;
        if (OperatingSystem.IsWindows())
        {
            reg = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Internet Settings", writable: true);
            if (reg is not null)
            {
                enable = reg.GetValue("ProxyEnable");
                server = reg.GetValue("ProxyServer");
            }
        }

        try
        {
            using var interception = new InterceptionService(new RecordingSystemProxyController())
            {
                UseInMemoryTrustState = true,
            };
            await interception.StartAsync(IPAddress.Loopback, 0);
            interception.DecryptHttps = false;
            var off = await DecryptHttpsProbe.VerifyAsync(interception, TimeSpan.FromSeconds(2));
            Assert.IsFalse(off.Ok);
            StringAssert.Contains(off.Message, "Decrypt HTTPS is off");

            interception.DecryptHttps = true;
            var probed = await DecryptHttpsProbe.VerifyAsync(interception, TimeSpan.FromSeconds(3));
            Assert.IsFalse(string.IsNullOrWhiteSpace(probed.Message));

            var proxy = (Titanium.Web.Proxy.ProxyServer)typeof(InterceptionService)
                .GetField("_proxy", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .GetValue(interception)!;
            proxy.CertificateManager.ClearRootCertificate();
            var missingRoot = await DecryptHttpsProbe.VerifyAsync(interception, TimeSpan.FromMilliseconds(50));
            Assert.IsFalse(missingRoot.Ok);
            StringAssert.Contains(missingRoot.Message, "Root CA is not loaded");

            typeof(InterceptionService).GetMethod(
                "TryImportRootIntoFirefoxProfiles",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .Invoke(interception, null);
            interception.Stop();
        }
        finally
        {
            if (OperatingSystem.IsWindows() && reg is not null)
            {
                if (enable is null) reg.DeleteValue("ProxyEnable", throwOnMissingValue: false);
                else reg.SetValue("ProxyEnable", enable);
                if (server is null) reg.DeleteValue("ProxyServer", throwOnMissingValue: false);
                else reg.SetValue("ProxyServer", server);
                reg.Dispose();
            }
        }
    }

    private static X509Certificate2 CreateSelfSigned(string subject)
    {
        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest(subject, rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        req.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        return req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
    }
}
