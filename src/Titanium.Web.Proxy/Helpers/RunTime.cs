using System;
using System.Text;
using System.Runtime.InteropServices;

namespace Titanium.Web.Proxy.Helpers
{
    /// <summary>
    ///     Run time helpers
    /// </summary>
    public static class RunTime
    {
        /// <summary>
        ///     cache for mono runtime check
        /// </summary>
        /// <returns></returns>
        private static readonly Lazy<bool> isRunningOnMono = new Lazy<bool>(() => Type.GetType("Mono.Runtime") != null);

        /// <summary>
        ///     cache for mono runtime check
        /// </summary>
        /// <returns></returns>
        private static readonly Lazy<bool> isRunningOnMonoLinux = new Lazy<bool>(() => IsRunningOnMono && (int)Environment.OSVersion.Platform == 4);

        /// <summary>
        ///     cache for mono runtime check
        /// </summary>
        /// <returns></returns>
        private static readonly Lazy<bool> isRunningOnMonoMac = new Lazy<bool>(() => IsRunningOnMono && (int)Environment.OSVersion.Platform == 6);

#if NETSTANDARD2_0
        /// <summary>
        /// cache for Windows platform check
        /// </summary>
        /// <returns></returns>
        private static bool isRunningOnWindows => RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
        private static bool isRunningOnLinux => RuntimeInformation.IsOSPlatform(OSPlatform.Linux);
        private static bool isRunningOnMac => RuntimeInformation.IsOSPlatform(OSPlatform.OSX);
#endif
        /// <summary>
        ///     Is running on Mono?
        /// </summary>
        internal static bool IsRunningOnMono => isRunningOnMono.Value;

#if NETSTANDARD2_0
        public static bool IsLinux => isRunningOnLinux;
#else
        public static bool IsLinux => isRunningOnMonoLinux.Value;
#endif

#if NETSTANDARD2_0
        public static bool IsWindows => isRunningOnWindows;
#else
        public static bool IsWindows => !IsLinux && !IsMac;
#endif
        public static bool IsUwpOnWindows => IsWindows && UwpHelper.IsRunningAsUwp();

#if NETSTANDARD2_0
        public static bool IsMac => isRunningOnMac;
#else
        public static bool IsMac => isRunningOnMonoMac.Value;
#endif

        //https://github.com/qmatteoq/DesktopBridgeHelpers/blob/master/DesktopBridge.Helpers/Helpers.cs
        private class UwpHelper
        {
            const long APPMODEL_ERROR_NO_PACKAGE = 15700L;

            [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
            static extern int GetCurrentPackageFullName(ref int packageFullNameLength, StringBuilder packageFullName);

            internal static bool IsRunningAsUwp()
            {
                if (IsWindows7OrLower)
                {
                    return false;
                }
                else
                {
                    int length = 0;
                    var sb = new StringBuilder(0);
                    int result = GetCurrentPackageFullName(ref length, sb);

                    sb = new StringBuilder(length);
                    result = GetCurrentPackageFullName(ref length, sb);

                    return result != APPMODEL_ERROR_NO_PACKAGE;
                }
            }

            private static bool IsWindows7OrLower
            {
                get
                {
                    int versionMajor = Environment.OSVersion.Version.Major;
                    int versionMinor = Environment.OSVersion.Version.Minor;
                    double version = versionMajor + (double)versionMinor / 10;
                    return version <= 6.1;
                }
            }
        }
    }
}
