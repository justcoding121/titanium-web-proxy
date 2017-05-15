using System;

namespace Titanium.Web.Proxy.Helpers
{
    /// <summary>
    /// Run time helpers
    /// </summary>
    internal class RunTime
    {
        /// <summary>
        /// Checks if current run time is Mono
        /// </summary>
        /// <returns></returns>
        internal static bool IsRunningOnMono()
        {
            return Type.GetType("Mono.Runtime") != null;
        }
    }
}
