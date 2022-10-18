using System;

namespace Titanium.Web.Proxy.Helpers;

internal struct ResponseStatusInfo
{
    public Version Version { get; set; }

    public int StatusCode { get; set; }

    public string Description { get; set; }
}