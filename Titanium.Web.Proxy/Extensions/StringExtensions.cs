using System.Globalization;

namespace Titanium.Web.Proxy.Extensions
{
    internal static class StringExtensions
    {
        internal static bool ContainsIgnoreCase(this string str, string value)
        {
            return CultureInfo.CurrentCulture.CompareInfo.IndexOf(str, value, CompareOptions.IgnoreCase) >= 0;
        }
    }
}
