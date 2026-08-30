using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;

namespace Titanium.Inspector.Services;

public sealed class AppContainerInfo
{
    public required string DisplayName { get; init; }
    public required string PackageFamilyName { get; init; }
    public required string AppContainerSid { get; init; }
    public bool IsExempt { get; set; }
}

/// <summary>
/// Windows AppContainer loopback exemption (Fiddler WinConfig-style) via FirewallAPI.
/// </summary>
public static partial class AppContainerLoopback
{
    [SupportedOSPlatformGuard("windows")]
    public static bool IsSupported =>
        OperatingSystem.IsWindowsVersionAtLeast(6, 2);

    public static List<AppContainerInfo> ListContainers()
    {
        if (!IsSupported)
        {
            return [];
        }

        return ListContainersWindows();
    }

    public static bool SetExemptions(IEnumerable<string> appContainerSids)
    {
        if (!IsSupported)
        {
            return false;
        }

        return SetExemptionsWindows(appContainerSids);
    }

    public static bool ClearExemptions() => SetExemptions([]);

    /// <summary>Smoke: P/Invoke entry points resolve on Windows (read + SID conversion).</summary>
    public static bool TryProbeApis(out string message)
    {
        if (!IsSupported)
        {
            message = "AppContainer loopback APIs require Windows 8+";
            return false;
        }

        try
        {
            var hr = NativeMethods.NetworkIsolationGetAppContainerConfig(out var count, out var ptr);
            if (ptr != IntPtr.Zero)
            {
                NativeMethods.LocalFree(ptr);
            }

            // LibraryImport uses ExactSpelling — GetConfig alone does not load ConvertStringSidToSidW.
            if (!NativeMethods.ConvertStringSidToSid("S-1-1-0", out var everyoneSid) || everyoneSid == IntPtr.Zero)
            {
                message = "ConvertStringSidToSidW failed for well-known SID S-1-1-0";
                return false;
            }

            NativeMethods.LocalFree(everyoneSid);

            message = hr == 0
                ? $"FirewallAPI bound (current exemptions: {count}; ConvertStringSidToSidW ok)"
                : $"FirewallAPI bound (GetAppContainerConfig HRESULT/DWORD={hr}; ConvertStringSidToSidW ok)";
            return true;
        }
        catch (Exception ex)
        {
            message = ex.Message;
            return false;
        }
    }

    [SupportedOSPlatform("windows")]
    private static List<AppContainerInfo> ListContainersWindows()
    {
        var exempt = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var sid in GetExemptSidsWindows())
        {
            exempt.Add(sid);
        }

        var list = new List<AppContainerInfo>();
        var err = NativeMethods.NetworkIsolationEnumAppContainers(0, out var count, out var arrayPtr);
        if (err != 0 || arrayPtr == IntPtr.Zero || count == 0)
        {
            if (arrayPtr != IntPtr.Zero)
            {
                NativeMethods.NetworkIsolationFreeAppContainers(arrayPtr);
            }

            return list;
        }

        try
        {
            var stride = Marshal.SizeOf<NativeMethods.INET_FIREWALL_APP_CONTAINER>();
            for (uint i = 0; i < count; i++)
            {
                var itemPtr = IntPtr.Add(arrayPtr, (int)(i * stride));
                var item = Marshal.PtrToStructure<NativeMethods.INET_FIREWALL_APP_CONTAINER>(itemPtr);
                var sid = SidToString(item.appContainerSid);
                if (string.IsNullOrEmpty(sid))
                {
                    continue;
                }

                var display = Marshal.PtrToStringUni(item.displayName) ?? "";
                var pfn = Marshal.PtrToStringUni(item.appContainerName) ?? "";
                list.Add(new AppContainerInfo
                {
                    DisplayName = string.IsNullOrWhiteSpace(display) ? pfn : display,
                    PackageFamilyName = pfn,
                    AppContainerSid = sid,
                    IsExempt = exempt.Contains(sid),
                });
            }
        }
        finally
        {
            NativeMethods.NetworkIsolationFreeAppContainers(arrayPtr);
        }

        return list
            .OrderBy(c => c.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    [SupportedOSPlatform("windows")]
    private static IEnumerable<string> GetExemptSidsWindows()
    {
        var err = NativeMethods.NetworkIsolationGetAppContainerConfig(out var count, out var ptr);
        if (err != 0 || ptr == IntPtr.Zero || count == 0)
        {
            if (ptr != IntPtr.Zero)
            {
                NativeMethods.LocalFree(ptr);
            }

            yield break;
        }

        try
        {
            var stride = Marshal.SizeOf<NativeMethods.SID_AND_ATTRIBUTES>();
            for (uint i = 0; i < count; i++)
            {
                var itemPtr = IntPtr.Add(ptr, (int)(i * stride));
                var item = Marshal.PtrToStructure<NativeMethods.SID_AND_ATTRIBUTES>(itemPtr);
                var sid = SidToString(item.Sid);
                if (!string.IsNullOrEmpty(sid))
                {
                    yield return sid;
                }
            }
        }
        finally
        {
            NativeMethods.LocalFree(ptr);
        }
    }

    [SupportedOSPlatform("windows")]
    private static bool SetExemptionsWindows(IEnumerable<string> appContainerSids)
    {
        var sidPtrs = new List<IntPtr>();
        try
        {
            var attrs = new List<NativeMethods.SID_AND_ATTRIBUTES>();
            foreach (var sid in appContainerSids.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (!NativeMethods.ConvertStringSidToSid(sid, out var pSid) || pSid == IntPtr.Zero)
                {
                    continue;
                }

                sidPtrs.Add(pSid);
                attrs.Add(new NativeMethods.SID_AND_ATTRIBUTES { Sid = pSid, Attributes = 0 });
            }

            var arr = attrs.ToArray();
            var err = NativeMethods.NetworkIsolationSetAppContainerConfig((uint)arr.Length, arr.Length == 0 ? null : arr);
            return err == 0;
        }
        finally
        {
            foreach (var p in sidPtrs)
            {
                NativeMethods.LocalFree(p);
            }
        }
    }

    [SupportedOSPlatform("windows")]
    private static string SidToString(IntPtr sid)
    {
        if (sid == IntPtr.Zero)
        {
            return "";
        }

        try
        {
            return new SecurityIdentifier(sid).Value;
        }
        catch
        {
            return "";
        }
    }

    private static partial class NativeMethods
    {
        [StructLayout(LayoutKind.Sequential)]
        public struct SID_AND_ATTRIBUTES // NOSONAR S101 -- Win32 struct name required for P/Invoke
        {
            public IntPtr Sid;
            public uint Attributes;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct INET_FIREWALL_APP_CONTAINER // NOSONAR S101 -- Win32 struct name required for P/Invoke
        {
            public IntPtr appContainerSid;
            public IntPtr userSid;
            public IntPtr appContainerName;
            public IntPtr displayName;
            public IntPtr description;
            public INET_FIREWALL_AC_CAPABILITIES capabilities;
            public INET_FIREWALL_AC_BINARIES binaries;
            public IntPtr workingDirectory;
            public IntPtr packageFullName;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct INET_FIREWALL_AC_CAPABILITIES // NOSONAR S101 -- Win32 struct name required for P/Invoke
        {
            public uint count;
            public IntPtr capabilities;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct INET_FIREWALL_AC_BINARIES // NOSONAR S101 -- Win32 struct name required for P/Invoke
        {
            public uint count;
            public IntPtr binaries;
        }

        [LibraryImport("FirewallAPI.dll")]
        public static partial uint NetworkIsolationEnumAppContainers(
            uint flags,
            out uint pdwCntPublicACs,
            out IntPtr ppACs);

        [LibraryImport("FirewallAPI.dll")]
        public static partial void NetworkIsolationFreeAppContainers(IntPtr pACs);

        [LibraryImport("FirewallAPI.dll")]
        public static partial uint NetworkIsolationGetAppContainerConfig(
            out uint pdwCntACs,
            out IntPtr appContainerSids);

        [LibraryImport("FirewallAPI.dll")]
        public static partial uint NetworkIsolationSetAppContainerConfig(
            uint dwNumPublicAppCs,
            [In] SID_AND_ATTRIBUTES[]? appContainerSids);

        // LibraryImport uses ExactSpelling — must use the W export, not the A/W-less alias.
        [LibraryImport("advapi32.dll", EntryPoint = "ConvertStringSidToSidW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static partial bool ConvertStringSidToSid(string strSid, out IntPtr pSid);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        public static partial IntPtr LocalFree(IntPtr hMem);
    }
}
