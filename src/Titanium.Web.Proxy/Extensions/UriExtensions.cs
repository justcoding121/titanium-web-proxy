using System;

namespace Titanium.Web.Proxy.Extensions
{
    internal static class UriExtensions
    {
        internal static string GetOriginalPathAndQuery(this Uri uri)
        {
            string leftPart = uri.GetLeftPart(UriPartial.Authority);
            if (uri.OriginalString.StartsWith(leftPart))
                return uri.OriginalString.Substring(leftPart.Length);

            return uri.IsWellFormedOriginalString() ? uri.PathAndQuery : uri.GetComponents(UriComponents.PathAndQuery, UriFormat.Unescaped);
        }
    }   
}
