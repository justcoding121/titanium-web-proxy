using System;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Titanium.Web.Proxy.Http3;

namespace Titanium.Web.Proxy.UnitTests;

[TestClass]
public class MacHttp3NativeArchTests
{
    [TestMethod]
    public void TryReadCpuType_ReadsLittleEndianX64()
    {
        var header = BuildThinHeader(0xFEEDFACF, MacHttp3NativeArch.CpuTypeX64);

        Assert.IsTrue(MacHttp3NativeArch.TryReadCpuType(header, out var cpu));
        Assert.AreEqual(MacHttp3NativeArch.CpuTypeX64, cpu);
    }

    [TestMethod]
    public void TryReadCpuType_ReadsLittleEndianArm64()
    {
        var header = BuildThinHeader(0xFEEDFACF, MacHttp3NativeArch.CpuTypeArm64);

        Assert.IsTrue(MacHttp3NativeArch.TryReadCpuType(header, out var cpu));
        Assert.AreEqual(MacHttp3NativeArch.CpuTypeArm64, cpu);
    }

    [TestMethod]
    public void TryReadCpuType_ReadsBigEndianArm64()
    {
        // On-disk MH_CIGAM_64 + CPU_TYPE_ARM64 (big-endian words).
        var header = new byte[] { 0xFE, 0xED, 0xFA, 0xCF, 0x01, 0x00, 0x00, 0x0C };

        Assert.IsTrue(MacHttp3NativeArch.TryReadCpuType(header, out var cpu));
        Assert.AreEqual(MacHttp3NativeArch.CpuTypeArm64, cpu);
    }

    [TestMethod]
    public void TryReadCpuType_RejectsFatBinary()
    {
        var header = new byte[] { 0xCA, 0xFE, 0xBA, 0xBE, 0, 0, 0, 1 };

        Assert.IsFalse(MacHttp3NativeArch.TryReadCpuType(header, out _));
    }

    [TestMethod]
    public void MatchesProcessArchitecture_RequiresExactCpu()
    {
        Assert.IsTrue(MacHttp3NativeArch.MatchesProcessArchitecture(
            MacHttp3NativeArch.CpuTypeX64, Architecture.X64));
        Assert.IsFalse(MacHttp3NativeArch.MatchesProcessArchitecture(
            MacHttp3NativeArch.CpuTypeArm64, Architecture.X64));
        Assert.IsTrue(MacHttp3NativeArch.MatchesProcessArchitecture(
            MacHttp3NativeArch.CpuTypeArm64, Architecture.Arm64));
    }

    [TestMethod]
    public void DetectMismatch_ReportsWrongArchitecture()
    {
        var dir = CreateTempDylib(MacHttp3NativeArch.CpuTypeArm64);
        try
        {
            var kind = MacHttp3NativeArch.DetectMismatch(
                dir,
                processArchitecture: Architecture.X64,
                processTranslated: false);

            Assert.AreEqual(MacHttp3NativeMismatchKind.WrongArchitecture, kind);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [TestMethod]
    public void DetectMismatch_ReportsRosettaHint()
    {
        var dir = CreateTempDylib(MacHttp3NativeArch.CpuTypeArm64);
        try
        {
            var kind = MacHttp3NativeArch.DetectMismatch(
                dir,
                processArchitecture: Architecture.X64,
                processTranslated: true);

            Assert.AreEqual(MacHttp3NativeMismatchKind.WrongArchitectureUnderRosetta, kind);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [TestMethod]
    public void DetectMismatch_IgnoresMatchingCpu()
    {
        var dir = CreateTempDylib(MacHttp3NativeArch.CpuTypeArm64);
        try
        {
            var kind = MacHttp3NativeArch.DetectMismatch(
                dir,
                processArchitecture: Architecture.Arm64,
                processTranslated: false);

            Assert.AreEqual(MacHttp3NativeMismatchKind.None, kind);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [TestMethod]
    public void IsProcessTranslated_ReturnsFalseOffMac_AndDoesNotThrowOnMac()
    {
        var translated = MacHttp3NativeArch.IsProcessTranslated();

        if (!OperatingSystem.IsMacOS())
        {
            Assert.IsFalse(translated);
        }
    }

    private static byte[] BuildThinHeader(uint magicLe, int cpuType)
    {
        var header = new byte[8];
        BitConverter.TryWriteBytes(header.AsSpan(0, 4), magicLe);
        BitConverter.TryWriteBytes(header.AsSpan(4, 4), cpuType);
        return header;
    }

    private static string CreateTempDylib(int cpuType)
    {
        var dir = Path.Combine(Path.GetTempPath(), "twp-mac-http3-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, "libmsquic.dylib"), BuildThinHeader(0xFEEDFACF, cpuType));
        return dir;
    }
}
