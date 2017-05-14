using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

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
