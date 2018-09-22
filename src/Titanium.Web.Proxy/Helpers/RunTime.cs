using System;
#if NETSTANDARD2_0
using System.Runtime.InteropServices;
#endif

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

#if NETSTANDARD2_0
        public static bool IsMac => isRunningOnMac;
#else
        public static bool IsMac => isRunningOnMonoMac.Value;
#endif

    }
}
