using System;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace Titanium.Web.Proxy.Helpers
{
    /// <summary>
    ///     Run time helpers
    /// </summary>
    public static class RunTime
    {
        private static readonly Lazy<bool> isRunningOnMono = new Lazy<bool>(() => Type.GetType("Mono.Runtime") != null);

#if NET451 || NET461
        /// <summary>
        /// cache for Windows platform check
        /// </summary>
        /// <returns></returns>
        private static bool IsRunningOnWindows => true;

        /// <summary>
        ///     cache for mono runtime check
        /// </summary>
        /// <returns></returns>
        private static bool IsRunningOnLinux => false;

        /// <summary>
        ///     cache for mac runtime check
        /// </summary>
        /// <returns></returns>
        private static bool IsRunningOnMac => false;
#else
        /// <summary>
        /// cache for Windows platform check
        /// </summary>
        /// <returns></returns>
        private static bool IsRunningOnWindows => RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

        /// <summary>
        ///     cache for mono runtime check
        /// </summary>
        /// <returns></returns>
        private static bool IsRunningOnLinux => RuntimeInformation.IsOSPlatform(OSPlatform.Linux);

        /// <summary>
        ///     cache for mac runtime check
        /// </summary>
        /// <returns></returns>
        private static bool IsRunningOnMac => RuntimeInformation.IsOSPlatform(OSPlatform.OSX);
#endif

        /// <summary>
        ///     Is running on Mono?
        /// </summary>
        internal static bool IsRunningOnMono => isRunningOnMono.Value;

        public static bool IsLinux => IsRunningOnLinux;

        public static bool IsWindows => IsRunningOnWindows;

        public static bool IsUwpOnWindows => IsWindows && UwpHelper.IsRunningAsUwp();

        public static bool IsMac => IsRunningOnMac;

        private static bool? _isSocketReuseAvailable;

        /// <summary>
        /// Is socket reuse available to use?
        /// </summary>
        public static bool IsSocketReuseAvailable()
        {
            // use the cached value if we have one
            if (_isSocketReuseAvailable != null)
                return _isSocketReuseAvailable.Value;

            try
            {
                if (IsWindows)
                {
                    // since we are on windows just return true
                    // store the result in our static object so we don't have to be bothered going through all this more than once
                    _isSocketReuseAvailable = true;
                    return true;
                }

                // get the currently running framework name and version (EX: .NETFramework,Version=v4.5.1) (Ex: .NETCoreApp,Version=v2.0)
                string? ver = Assembly.GetEntryAssembly()?.GetCustomAttribute<TargetFrameworkAttribute>()?.FrameworkName;

                if (ver == null)
                    return false; // play it safe if we can not figure out what the framework is

                // make sure we are on .NETCoreApp
                ver = ver.ToLower(); // make everything lowercase to simplify comparison
                if (ver.Contains(".netcoreapp"))
                {
                    var versionString = ver.Replace(".netcoreapp,version=v", "");
                    var versionArr = versionString.Split('.');
                    var majorVersion = Convert.ToInt32(versionArr[0]);

                    var result = majorVersion >= 3; // version 3 and up supports socket reuse

                    // store the result in our static object so we don't have to be bothered going through all this more than once
                    _isSocketReuseAvailable = result;
                    return result;
                }

                // store the result in our static object so we don't have to be bothered going through all this more than once
                _isSocketReuseAvailable = false;
                return false;
            }
            catch
            {
                // store the result in our static object so we don't have to be bothered going through all this more than once
                _isSocketReuseAvailable = false;
                return false;
            }
        }

        // https://github.com/qmatteoq/DesktopBridgeHelpers/blob/master/DesktopBridge.Helpers/Helpers.cs
        private class UwpHelper
        {
            const long AppmodelErrorNoPackage = 15700L;

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

                    return result != AppmodelErrorNoPackage;
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
