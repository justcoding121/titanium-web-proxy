using System;
using System.Runtime.InteropServices;

namespace Titanium.Web.Proxy.Helpers
{
    /// <summary>
    ///     Run time helpers
    /// </summary>
    internal class RunTime
    {
        /// <summary>
        ///     cache for mono runtime check
        /// </summary>
        /// <returns></returns>
        private static readonly Lazy<bool> isRunningOnMono = new Lazy<bool>(() => Type.GetType("Mono.Runtime") != null);

        /// <summary>
        /// cache for Windows platform check
        /// </summary>
        /// <returns></returns>
        private static bool isRunningOnWindows => RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
        private static bool isRunningOnLinux => RuntimeInformation.IsOSPlatform(OSPlatform.Linux);
        private static bool isRunningOnMac => RuntimeInformation.IsOSPlatform(OSPlatform.OSX);

        /// <summary>
        ///     Is running on Mono?
        /// </summary>
        internal static bool IsRunningOnMono => isRunningOnMono.Value;

        internal static bool IsLinux => isRunningOnLinux;

        internal static bool IsWindows => isRunningOnWindows;

        internal static bool IsMac => isRunningOnMac;

    }
}
