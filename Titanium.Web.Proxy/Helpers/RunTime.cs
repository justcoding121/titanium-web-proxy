using System;

namespace Titanium.Web.Proxy.Helpers
{
    /// <summary>
    /// Run time helpers
    /// </summary>
    internal class RunTime
    {
        private static Lazy<bool> isMonoRuntime 
            = new Lazy<bool>(()=> Type.GetType("Mono.Runtime") != null);
        /// <summary>
        /// Checks if current run time is Mono
        /// </summary>
        /// <returns></returns>
        internal static bool IsRunningOnMono()
        {
            return isMonoRuntime.Value;
        }
    }
}
