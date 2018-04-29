using System;

#if NETSTANDARD2_0
using System.Runtime.InteropServices;
#endif
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

#if NETSTANDARD2_0
/// <summary>
/// cache for Windows platform check
/// </summary>
/// <returns></returns>
private static readonly Lazy<bool> isRunningOnWindows
    = new Lazy<bool>(() => RuntimeInformation.IsOSPlatform(OSPlatform.Windows));
#endif

        /// <summary>
        ///     Is running on Mono?
        /// </summary>
        internal static bool IsRunningOnMono => isRunningOnMono.Value;

#if NETSTANDARD2_0
        internal static bool IsWindows => isRunningOnWindows.Value;
#else
        internal static bool IsWindows => true;
#endif
    }
}
