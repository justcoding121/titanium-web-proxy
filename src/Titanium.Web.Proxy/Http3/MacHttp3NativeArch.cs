using System;
using System.IO;
using System.Runtime.InteropServices;

namespace Titanium.Web.Proxy.Http3;

/// <summary>
/// Detects when a Mac app-local <c>libmsquic.dylib</c> was built for a different CPU than this process.
/// </summary>
public static class MacHttp3NativeArch
{
    /// <summary>Mach-O <c>CPU_TYPE_X86_64</c>.</summary>
    public const int CpuTypeX64 = 0x01000007;

    /// <summary>Mach-O <c>CPU_TYPE_ARM64</c>.</summary>
    public const int CpuTypeArm64 = 0x0100000C;

    private const uint MachOMagic64Le = 0xFEEDFACF;
    private const uint MachOMagic32Le = 0xFEEDFACE;
    private const uint MachOMagic64Be = 0xCFFAEDFE;
    private const uint MachOMagic32Be = 0xCEFAEDFE;
    private const uint FatMagicBe = 0xCAFEBABE;
    private const uint FatMagicLe = 0xBEBAFECA;

    /// <summary>
    /// Reads the CPU type from the first eight bytes of a thin Mach-O image.
    /// Fat binaries are rejected (return false) so callers can fail the package build.
    /// </summary>
    public static bool TryReadCpuType(ReadOnlySpan<byte> header, out int cpuType)
    {
        cpuType = 0;
        if (header.Length < 8)
        {
            return false;
        }

        var magic = ReadUInt32(header, littleEndian: true);
        var bigEndian = false;
        switch (magic)
        {
            case MachOMagic64Le:
            case MachOMagic32Le:
                break;
            case MachOMagic64Be:
            case MachOMagic32Be:
                bigEndian = true;
                break;
            case FatMagicBe:
            case FatMagicLe:
                return false;
            default:
                // Magic may have been read little-endian on a BE image path already covered;
                // also accept when the host already swapped the known BE constants.
                return false;
        }

        cpuType = unchecked((int)ReadUInt32(header.Slice(4), littleEndian: !bigEndian));
        return true;
    }

    /// <summary>True when the dylib CPU matches the given process architecture.</summary>
    public static bool MatchesProcessArchitecture(int dylibCpuType, Architecture processArchitecture) =>
        processArchitecture switch
        {
            Architecture.X64 => dylibCpuType == CpuTypeX64,
            Architecture.Arm64 => dylibCpuType == CpuTypeArm64,
            _ => false,
        };

    /// <summary>
    /// When app-local <c>libmsquic.dylib</c> exists and its CPU does not match this process,
    /// returns the mismatch kind. Otherwise <see cref="MacHttp3NativeMismatchKind.None"/>.
    /// </summary>
    /// <param name="baseDirectory">Directory that should contain <c>libmsquic.dylib</c>. Defaults to <see cref="AppContext.BaseDirectory"/>.</param>
    /// <param name="processArchitecture">Process architecture under test. Defaults to <see cref="RuntimeInformation.ProcessArchitecture"/>.</param>
    /// <param name="processTranslated">When set, overrides the live <c>sysctl.proc_translated</c> probe (unit tests).</param>
    public static MacHttp3NativeMismatchKind DetectMismatch(
        string? baseDirectory = null,
        Architecture? processArchitecture = null,
        bool? processTranslated = null)
    {
        if (!OperatingSystem.IsMacOS() && processArchitecture is null && processTranslated is null
            && baseDirectory is null)
        {
            // Production path on Windows/Linux: nothing to check.
            return MacHttp3NativeMismatchKind.None;
        }

        var dir = string.IsNullOrEmpty(baseDirectory) ? AppContext.BaseDirectory : baseDirectory;
        if (string.IsNullOrEmpty(dir))
        {
            return MacHttp3NativeMismatchKind.None;
        }

        var path = Path.Combine(dir, "libmsquic.dylib");
        if (!File.Exists(path))
        {
            return MacHttp3NativeMismatchKind.None;
        }

        Span<byte> header = stackalloc byte[8];
        try
        {
            using var stream = File.OpenRead(path);
            var read = stream.Read(header);
            if (read < 8 || !TryReadCpuType(header, out var cpuType))
            {
                return MacHttp3NativeMismatchKind.None;
            }

            var arch = processArchitecture ?? RuntimeInformation.ProcessArchitecture;
            if (MatchesProcessArchitecture(cpuType, arch))
            {
                return MacHttp3NativeMismatchKind.None;
            }

            var translated = processTranslated ?? IsProcessTranslated();
            return translated
                ? MacHttp3NativeMismatchKind.WrongArchitectureUnderRosetta
                : MacHttp3NativeMismatchKind.WrongArchitecture;
        }
        catch
        {
            return MacHttp3NativeMismatchKind.None;
        }
    }

    /// <summary>True when this Intel process is running under Rosetta on Apple silicon.</summary>
    public static bool IsProcessTranslated()
    {
        if (!OperatingSystem.IsMacOS())
        {
            return false;
        }

        try
        {
            // Nested type so non-Mac hosts never resolve the libc P/Invoke.
            return MacSysctl.IsTranslated();
        }
        catch
        {
            return false;
        }
    }

    private static uint ReadUInt32(ReadOnlySpan<byte> span, bool littleEndian)
    {
        if (littleEndian)
        {
            return (uint)(span[0] | (span[1] << 8) | (span[2] << 16) | (span[3] << 24));
        }

        return (uint)((span[0] << 24) | (span[1] << 16) | (span[2] << 8) | span[3]);
    }

    private static class MacSysctl
    {
        public static bool IsTranslated()
        {
            nuint size = sizeof(int);
            var value = 0;
            var rc = sysctlbyname("sysctl.proc_translated", ref value, ref size, IntPtr.Zero, 0);
            return rc == 0 && value == 1;
        }

        [DllImport("libc", EntryPoint = "sysctlbyname", SetLastError = true)]
        [System.Diagnostics.CodeAnalysis.SuppressMessage(
            "Microsoft.Globalization",
            "CA2101:SpecifyMarshalingForPInvokeStringArguments",
            Justification = "The name is marshaled as UTF-8, which sysctlbyname requires.")]
        private static extern int sysctlbyname(
            [MarshalAs(UnmanagedType.LPUTF8Str)] string name,
            ref int oldp,
            ref nuint oldlenp,
            IntPtr newp,
            nuint newlen);
    }
}

/// <summary>Result of <see cref="MacHttp3NativeArch.DetectMismatch"/>.</summary>
public enum MacHttp3NativeMismatchKind
{
    /// <summary>No mismatch (or not applicable on this OS).</summary>
    None = 0,

    /// <summary>App-local MsQuic dylib CPU does not match the process.</summary>
    WrongArchitecture = 1,

    /// <summary>
    /// Same as <see cref="WrongArchitecture"/>, and this process is translated (Rosetta).
    /// Prefer the Apple silicon DMG.
    /// </summary>
    WrongArchitectureUnderRosetta = 2,
}
