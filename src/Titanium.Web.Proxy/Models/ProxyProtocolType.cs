using System;

namespace Titanium.Web.Proxy.Models;

[Flags]
public enum ProxyProtocolType // NOSONAR S2342 -- Public API enum name is retained for compatibility.
{
    /// <summary>
    ///     The none
    /// </summary>
    None = 0,

    /// <summary>
    ///     HTTP
    /// </summary>
    Http = 1,

    /// <summary>
    ///     HTTPS
    /// </summary>
    Https = 2,

    /// <summary>
    ///     Both HTTP and HTTPS
    /// </summary>
    AllHttp = Http | Https
}