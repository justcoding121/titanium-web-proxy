using System;
using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Titanium.Tls.Tests;

[TestClass]
public sealed class TlsAlgorithmPolicyTests
{
    [TestMethod]
    public void FipsPolicyKeepsAesGcmNistGroupsAndPss()
    {
        Assert.IsTrue(TlsAlgorithmSet.AllowsCipher(TlsAlgorithmPolicy.FipsApprovedOnly, 0x1301));
        Assert.IsTrue(TlsAlgorithmSet.AllowsCipher(TlsAlgorithmPolicy.FipsApprovedOnly, 0x1302));
        Assert.IsFalse(TlsAlgorithmSet.AllowsCipher(TlsAlgorithmPolicy.FipsApprovedOnly, 0x1303));
        Assert.IsTrue(TlsAlgorithmSet.AllowsGroup(TlsAlgorithmPolicy.FipsApprovedOnly, 0x0017));
        Assert.IsTrue(TlsAlgorithmSet.AllowsGroup(TlsAlgorithmPolicy.FipsApprovedOnly, 0x0018));
        Assert.IsFalse(TlsAlgorithmSet.AllowsGroup(TlsAlgorithmPolicy.FipsApprovedOnly, 0x001D));
        Assert.IsFalse(TlsAlgorithmSet.AllowsGroup(TlsAlgorithmPolicy.FipsApprovedOnly, 0x11EC));
        Assert.IsTrue(TlsAlgorithmSet.AllowsSignature(TlsAlgorithmPolicy.FipsApprovedOnly, 0x0403));
        Assert.IsTrue(TlsAlgorithmSet.AllowsSignature(TlsAlgorithmPolicy.FipsApprovedOnly, 0x0503));
        Assert.IsTrue(TlsAlgorithmSet.AllowsSignature(TlsAlgorithmPolicy.FipsApprovedOnly, 0x0804));
        Assert.IsTrue(TlsAlgorithmSet.AllowsSignature(TlsAlgorithmPolicy.FipsApprovedOnly, 0x0805));
        Assert.IsTrue(TlsAlgorithmSet.AllowsSignature(TlsAlgorithmPolicy.FipsApprovedOnly, 0x0806));
        Assert.IsFalse(TlsAlgorithmSet.AllowsSignature(TlsAlgorithmPolicy.FipsApprovedOnly, 0x0401));
        Assert.IsFalse(TlsAlgorithmSet.AllowsSignature(TlsAlgorithmPolicy.FipsApprovedOnly, 0x0807));
        Assert.IsFalse(TlsAlgorithmSet.AllowsRsa(TlsAlgorithmPolicy.FipsApprovedOnly, 2047));
        Assert.IsTrue(TlsAlgorithmSet.AllowsRsa(TlsAlgorithmPolicy.FipsApprovedOnly, 2048));
        Assert.IsTrue(TlsAlgorithmSet.AllowsCipher(TlsAlgorithmPolicy.Default, 0x1303));
        Assert.IsTrue(TlsAlgorithmSet.AllowsGroup(TlsAlgorithmPolicy.Default, 0x001D));
    }

    [TestMethod]
    public void RegistryAndLinuxFileSelectFipsMode()
    {
        Assert.IsTrue(FipsEnvironment.Evaluate(new FakeProbe(windows: true, enabled: 1, linux: null, mac: false)));
        Assert.IsFalse(FipsEnvironment.Evaluate(new FakeProbe(windows: true, enabled: 0, linux: null, mac: false)));
        Assert.IsFalse(FipsEnvironment.Evaluate(new FakeProbe(windows: true, enabled: null, linux: null, mac: false)));
        Assert.IsTrue(FipsEnvironment.Evaluate(new FakeProbe(windows: false, enabled: null, linux: "1\n", mac: false, linuxOs: true)));
        Assert.IsFalse(FipsEnvironment.Evaluate(new FakeProbe(windows: false, enabled: null, linux: "0\n", mac: false, linuxOs: true)));
        Assert.IsFalse(FipsEnvironment.Evaluate(new FakeProbe(windows: false, enabled: null, linux: null, mac: false, linuxOs: true)));
        Assert.IsFalse(FipsEnvironment.Evaluate(new FakeProbe(windows: false, enabled: 1, linux: "1\n", mac: true, linuxOs: true)));
    }

    [TestMethod]
    public void FipsModeRefusesTheManagedStackUntilTheOverrideIsSet()
    {
        FipsEnvironment.Use(new FakeProbe(windows: true, enabled: 1, linux: null, mac: false));
        try
        {
            using var stream = new TlsStream(new MemoryStream());
            Assert.AreEqual(TlsFingerprintFidelity.ReducedFipsPolicy, stream.FingerprintFidelity);
            NotSupportedException error = Assert.ThrowsException<NotSupportedException>(() =>
                stream.AuthenticateAsClient(new System.Net.Security.SslClientAuthenticationOptions()));
            StringAssert.Contains(error.Message, "TITANIUM_MANAGED_STACK_FIPS_OVERRIDE=1");

            using var allowed = new TlsStream(new MemoryStream(), new TlsStreamOptions { AllowManagedStackUnderFips = true });
            Assert.AreEqual(TlsFingerprintFidelity.ReducedFipsPolicy, allowed.FingerprintFidelity);
            try
            {
                allowed.AuthenticateAsClient(new System.Net.Security.SslClientAuthenticationOptions { TargetHost = "localhost" });
                Assert.Fail("An empty stream cannot finish a handshake.");
            }
            catch (NotSupportedException denied)
            {
                Assert.Fail(denied.Message);
            }
            catch (IOException)
            {
            }
        }
        finally
        {
            FipsEnvironment.Reset();
        }
    }

    [TestMethod]
    public void ManagedStackRefusalNamesTheOverride()
    {
        FipsEnvironment.Use(new FakeProbe(windows: true, enabled: 1, linux: null, mac: false));
        string? previous = Environment.GetEnvironmentVariable("TITANIUM_MANAGED_STACK_FIPS_OVERRIDE");
        try
        {
            Environment.SetEnvironmentVariable("TITANIUM_MANAGED_STACK_FIPS_OVERRIDE", null);
            string? refused = FipsEnvironment.ManagedStackRefusal(managedRequested: true, configOverride: false);
            StringAssert.Contains(refused, "TITANIUM_MANAGED_STACK_FIPS_OVERRIDE=1");
            StringAssert.Contains(refused, "managedStackFipsOverride");
            Assert.IsNull(FipsEnvironment.ManagedStackRefusal(managedRequested: false, configOverride: false));
            Assert.IsNull(FipsEnvironment.ManagedStackRefusal(managedRequested: true, configOverride: true));
            Environment.SetEnvironmentVariable("TITANIUM_MANAGED_STACK_FIPS_OVERRIDE", "1");
            Assert.IsNull(FipsEnvironment.ManagedStackRefusal(managedRequested: true, configOverride: false));
        }
        finally
        {
            Environment.SetEnvironmentVariable("TITANIUM_MANAGED_STACK_FIPS_OVERRIDE", previous);
            FipsEnvironment.Reset();
        }
    }

    [TestMethod]
    public void FipsClientHelloOmitsX25519AndChaCha()
    {
        using var engine = new TlsEngine(TlsRole.Client) { AlgorithmPolicy = TlsAlgorithmPolicy.FipsApprovedOnly };
        var output = new TlsOutput();
        engine.Advance([], out _, output);
        Assert.IsTrue(HandshakeFraming.TryUnwrapHandshakeRecord(output.Bytes, out ReadOnlySpan<byte> payload, out _, out _));
        Assert.IsTrue(HandshakeMessages.TryReadClientHello(payload, out ClientHelloView hello, out _));
        Assert.IsFalse(ContainsPair(hello.CipherSuites, 0x1303));
        Assert.IsTrue(ContainsPair(hello.CipherSuites, 0x1301));
        ReadOnlySpan<byte> groups = Extension(hello.Extensions, ExtensionType.SupportedGroups);
        ReadOnlySpan<byte> signatures = Extension(hello.Extensions, ExtensionType.SignatureAlgorithms);
        Assert.IsFalse(ContainsPair(groups, 0x001D));
        Assert.IsFalse(ContainsPair(groups, 0x11EC));
        Assert.IsTrue(ContainsPair(groups, 0x0017));
        Assert.IsTrue(ContainsPair(groups, 0x0018));
        Assert.IsTrue(ContainsPair(signatures, 0x0403));
        Assert.IsTrue(ContainsPair(signatures, 0x0804));
        Assert.IsFalse(ContainsPair(signatures, 0x0401));
    }

    [TestMethod]
    public void FipsProfileDropsTheHybridShare()
    {
        var profile = new ChromeFingerprintProfile(new SystemEntropy(), TlsFingerprintFidelity.ReducedFipsPolicy);
        Assert.AreEqual(TlsFingerprintFidelity.ReducedFipsPolicy, profile.Fidelity);
        var writer = new System.Buffers.ArrayBufferWriter<byte>();
        profile.BuildClientHello(new ClientHelloContext { ServerName = "localhost" }, writer);
        byte[] record = writer.WrittenSpan.ToArray();
        Assert.IsTrue(HandshakeFraming.TryUnwrapHandshakeRecord(record, out ReadOnlySpan<byte> payload, out _, out _));
        Assert.IsTrue(HandshakeMessages.TryReadClientHello(payload, out ClientHelloView hello, out _));
        Assert.IsFalse(ContainsPair(hello.CipherSuites, 0x1303));
        ReadOnlySpan<byte> groups = Extension(hello.Extensions, ExtensionType.SupportedGroups);
        Assert.IsFalse(ContainsPair(groups, 0x11EC));
        Assert.IsFalse(ContainsPair(groups, 0x001D));
        Assert.IsTrue(ContainsPair(groups, 0x0017));
    }

    private static bool ContainsPair(ReadOnlySpan<byte> data, ushort value)
    {
        for (int i = 0; i + 1 < data.Length; i++)
        {
            if (((data[i] << 8) | data[i + 1]) == value)
            {
                return true;
            }
        }

        return false;
    }

    private static byte[] Extension(ReadOnlySpan<byte> extensions, ushort type)
    {
        TlsReader reader = new(extensions);
        while (reader.TryReadUInt16(out ushort found) && reader.TryReadVector16(out ReadOnlySpan<byte> body))
        {
            if (found == type)
            {
                return body.ToArray();
            }
        }

        return [];
    }

    private sealed class FakeProbe(bool windows, int? enabled, string? linux, bool mac, bool linuxOs = false) : IFipsProbe
    {
        public bool IsWindows => windows;

        public bool IsLinux => linuxOs;

        public bool IsMacOs => mac;

        public int? WindowsEnabled => enabled;

        public string? LinuxFile => linux;
    }
}
