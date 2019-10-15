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
        private static readonly Lazy<bool> isRunningOnMono = new Lazy<bool>(() => Type.GetType("Mono.Runtime") != null);

        /// <summary>
        /// cache for Windows platform check
        /// </summary>
        /// <returns></returns>
        private static bool isRunningOnWindows => RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

        /// <summary>
        ///     cache for mono runtime check
        /// </summary>
        /// <returns></returns>
        private static bool isRunningOnLinux => RuntimeInformation.IsOSPlatform(OSPlatform.Linux);

        /// <summary>
        ///     cache for mac runtime check
        /// </summary>
        /// <returns></returns>
        private static bool isRunningOnMac => RuntimeInformation.IsOSPlatform(OSPlatform.OSX);
      
        /// <summary>
        ///     Is running on Mono?
        /// </summary>
        internal static bool IsRunningOnMono => isRunningOnMono.Value;

        public static bool IsLinux => isRunningOnLinux;

        public static bool IsWindows => isRunningOnWindows;

        public static bool IsUwpOnWindows => IsWindows && UwpHelper.IsRunningAsUwp();

        public static bool IsMac => isRunningOnMac;

        // https://github.com/qmatteoq/DesktopBridgeHelpers/blob/master/DesktopBridge.Helpers/Helpers.cs
        private class UwpHelper
        {
            const long APPMODEL_ERROR_NO_PACKAGE = 15700L;

            [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
            static extern int GetCurrentPackageFullName(ref int packageFullNameLength, StringBuilder packageFullName);

            internal static bool IsRunningAsUwp()
            {
                if (isWindows7OrLower)
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

            private static bool isWindows7OrLower
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
