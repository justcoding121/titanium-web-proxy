using System;

namespace Titanium.Web.Proxy.Helpers
{
    /// <summary>
    /// Run time helpers
    /// </summary>
    internal class RunTime
    {
        /// <summary>
        /// cache for mono runtime check
        /// </summary>
        /// <returns></returns>
        private static Lazy<bool> isRunningOnMono
            = new Lazy<bool>(()=> Type.GetType("Mono.Runtime") != null);
      
        /// <summary>
        /// Is running on Mono?
        /// </summary>
        internal static bool IsRunningOnMono => isRunningOnMono.Value;
    }
}
